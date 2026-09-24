using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using T.Abstractions;
using T.Models;

namespace T.Services;

/// <summary>
/// Short-lived SSH user certificates from a step-ca, requested with the step CLI.
///
/// Security design:
/// - A new ECDSA P-256 key pair is generated in-process for every certificate. Only the public
///   key is written, into a work directory only the current user can access, and signed with
///   "step ssh certificate --sign"; the private key never reaches step, the ssh-agent or the disk
///   in plain text.
/// - The certificate is checked before use: it must certify exactly the generated key, be a user
///   certificate and be valid now. The check is repeated whenever a stored certificate is loaded.
/// - Key and certificate are stored per profile in the app data folder, in a directory only the
///   current user can access. The private key is encrypted with the app's AES-256-GCM
///   master key (DPAPI protected on Windows) and bound to profile id, profile settings and
///   certificate, so a record cannot be moved to another profile. File names are derived from
///   the parsed profile GUID, never from raw input.
/// - A certificate stored for different profile settings (CA, identity, ...) is discarded; records
///   of removed profiles and expired certificates are deleted.
/// - Renewals are serialized per profile, so several tabs share one (browser) login. Deleting the
///   certificate or the profile during a login wins: the result is not stored.
/// </summary>
public sealed class StepCertificateService : IStepCertificateService, IDisposable
{
    private const int StoreVersion = 1;
    private const int MaxFileBytes = 64 * 1024;
    private const string WorkDirectoryPrefix = "tmp-";
    private const string PublicKeyFileName = "id_ecdsa.pub";
    private const string CertificateFileName = "id_ecdsa-cert.pub";
    private const string PrivateKeyPemLabel = "EC PRIVATE KEY";

    /// <summary>Certificates expiring within this margin (at most a quarter of their lifetime) are renewed before a manual connect.</summary>
    private static readonly TimeSpan RenewalMargin = TimeSpan.FromMinutes(5);

    /// <summary>Connections that must not start a login use a certificate while it is still valid this long.</summary>
    private static readonly TimeSpan MinimumRemaining = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    /// <summary>Time for step including a browser (OIDC) login.</summary>
    private static readonly TimeSpan StepTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Work directories older than this are leftovers of a crash (step runs at most <see cref="StepTimeout"/>).</summary>
    private static readonly TimeSpan StaleWorkDirectoryAge = TimeSpan.FromHours(1);

    private readonly ISettingsService _settingsService;
    private readonly IEncryptionService _encryptionService;
    private readonly string _storeDirectory;
    private readonly Func<StepInvocation, CancellationToken, Task<StepResult>> _runStep;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _renewalLocks = new();

    // Guards the cache, the store files and the generations.
    private readonly Lock _storeLock = new();
    private readonly Dictionary<Guid, StoredCertificate> _records = [];

    /// <summary>Incremented by <see cref="Forget"/>; a renewal only stores its result when it did not change.</summary>
    private readonly Dictionary<Guid, long> _generations = [];

    public StepCertificateService(ISettingsService settingsService, IEncryptionService encryptionService)
        : this(settingsService, encryptionService, Path.Combine(AppPaths.DataDirectory, "step"), StepCli.RunAsync, TimeProvider.System)
    {
    }

    internal StepCertificateService(
        ISettingsService settingsService,
        IEncryptionService encryptionService,
        string storeDirectory,
        Func<StepInvocation, CancellationToken, Task<StepResult>> runStep,
        TimeProvider time)
    {
        _settingsService = settingsService;
        _encryptionService = encryptionService;
        _storeDirectory = storeDirectory;
        _runStep = runStep;
        _time = time;
        PruneStore();
    }

    public string? FindExecutable(string? configuredPath) =>
        StepCli.FindExecutableIn(configuredPath, StepCli.DefaultSearchDirectories(Environment.GetEnvironmentVariable("PATH")));

    public async Task<StepCredential> GetCredentialAsync(string profileId, bool allowRenewal, Action<string>? progress, CancellationToken cancellationToken)
    {
        var profile = FindProfile(profileId)
            ?? throw new StepCertificateException("The step-ca profile of this session no longer exists. Please edit the session.");
        if (StepProfileValidation.Validate(profile) is { } error)
            throw new StepCertificateException($"step-ca profile '{StepCli.Sanitize(profile.Name)}': {error} Please check Settings → step-ca.");

        var id = Guid.ParseExact(profile.Id, "D");
        var gate = _renewalLocks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (TryLoad(id, profile, allowRenewal) is { } stored)
                return stored;

            if (!allowRenewal)
            {
                throw new StepCertificateException(
                    $"The SSH certificate of the step-ca profile '{StepCli.Sanitize(profile.Name)}' has expired. Connect again to renew it.");
            }

            return await RenewAsync(id, profile, progress, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public DateTimeOffset? GetValidUntil(StepCaProfile profile)
    {
        if (!StepProfileValidation.TryParseId(profile.Id, out var id) || ReadRecord(id) is not { } record || record.Profile != Fingerprint(profile))
            return null;

        try
        {
            var validBefore = OpenSshFormat.ParseCertificate(record.Certificate).ValidBefore;
            return validBefore > _time.GetUtcNow() ? validBefore : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public void Forget(string profileId)
    {
        if (!StepProfileValidation.TryParseId(profileId, out var id))
            return;

        lock (_storeLock)
        {
            _generations[id] = GenerationLocked(id) + 1;
            DeleteRecordLocked(id);
        }
    }

    public void Dispose()
    {
        foreach (var gate in _renewalLocks.Values)
            gate.Dispose();
    }

    // ── Renewal ──────────────────────────────────────────────────────────

    private async Task<StepCredential> RenewAsync(Guid id, StepCaProfile profile, Action<string>? progress, CancellationToken cancellationToken)
    {
        EnsureTrustedSettings();
        var executable = FindExecutable(_settingsService.Current.Step?.ExecutablePath)
            ?? throw new StepCertificateException(
                "The step CLI was not found or can be changed by other users. Install it or check its path in Settings → step-ca.");

        long generation;
        lock (_storeLock)
            generation = GenerationLocked(id);

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var workDirectory = CreateWorkDirectory();
        try
        {
            var publicKeyPath = Path.Combine(workDirectory.FullName, PublicKeyFileName);
            await File.WriteAllTextAsync(publicKeyPath, OpenSshFormat.FormatPublicKey(key, "T step-ca") + "\n", cancellationToken);

            progress?.Invoke($"Requesting an SSH certificate from step-ca ({StepCli.Sanitize(profile.Name)}) - complete the login in the browser if asked...");
            var invocation = new StepInvocation(
                executable, StepCli.BuildSignArguments(profile, publicKeyPath), workDirectory.FullName, StepTimeout, progress);
            var result = await _runStep(invocation, cancellationToken);
            if (result.ExitCode != 0)
                throw new StepCertificateException(DescribeFailure(result));

            var certificatePath = Path.Combine(workDirectory.FullName, CertificateFileName);
            if (!File.Exists(certificatePath))
                throw new StepCertificateException("step did not write a certificate.");

            var certificateLine = ReadLimited(certificatePath).Trim();
            OpenSshCertificate certificate;
            try { certificate = OpenSshFormat.ParseCertificate(certificateLine); }
            catch (FormatException ex) { throw new StepCertificateException($"step returned an invalid certificate: {ex.Message}", ex); }

            var point = OpenSshFormat.EncodePoint(key.ExportParameters(includePrivateParameters: false));
            if (Verify(certificate, point) is { } problem)
                throw new StepCertificateException(problem);

            var privateKey = key.ExportECPrivateKey();
            try
            {
                Save(id, generation, profile, privateKey, certificateLine);
                return CreateCredential(privateKey, certificateLine, certificate.ValidBefore);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(privateKey);
            }
        }
        finally
        {
            try { workDirectory.Delete(recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"[StepCertificateService] Could not delete {workDirectory.FullName}: {ex.Message}");
            }
        }
    }

    private static string DescribeFailure(StepResult result)
    {
        var detail = StepCli.Sanitize(string.Join(" ", result.Lines.TakeLast(3)), 500);
        return detail.Length == 0
            ? $"step failed with exit code {result.ExitCode}."
            : $"step failed: {detail}";
    }

    /// <summary>Returns why the certificate must not be used, or null when it is fine.</summary>
    private string? Verify(OpenSshCertificate certificate, byte[] expectedPoint)
    {
        var now = _time.GetUtcNow();
        if (!CryptographicOperations.FixedTimeEquals(certificate.PublicPoint, expectedPoint))
            return "The certificate returned by step-ca does not belong to the generated key.";
        if (certificate.Type != OpenSshFormat.UserCertificate)
            return "step-ca returned a host certificate instead of a user certificate.";
        if (certificate.ValidAfter > now + MaxClockSkew)
            return "The certificate from step-ca is not valid yet. Please check the system clock.";
        if (certificate.ValidBefore <= now)
            return "The certificate from step-ca has already expired. Please check the system clock.";
        return null;
    }

    // ── Private directories ──────────────────────────────────────────────

    /// <summary>
    /// A fresh directory for the step run inside the private store. The public key in it must not
    /// be replaceable by another user (they would get a certificate for their key), so the access
    /// is set explicitly and checked instead of relying on %TEMP%, which may be shared.
    /// </summary>
    private DirectoryInfo CreateWorkDirectory()
    {
        try
        {
            EnsurePrivateDirectory(_storeDirectory);
            for (int attempt = 0; attempt < 5; attempt++)
            {
                var path = Path.Combine(_storeDirectory, WorkDirectoryPrefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)));
                if (Directory.Exists(path) || File.Exists(path))
                    continue;

                var directory = new DirectoryInfo(path);
                if (OperatingSystem.IsWindows())
                    directory.Create(CreateOwnerOnlySecurity());
                else
                    directory = Directory.CreateDirectory(path, OwnerOnlyMode);

                VerifyPrivate(directory);
                return directory;
            }
            throw new IOException("No unused directory name was found.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            throw new StepCertificateException($"The private work directory for step could not be created: {ex.Message}", ex);
        }
    }

    private const UnixFileMode OwnerOnlyMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>Creates the directory, or restricts an existing one, to the current user and verifies the result.</summary>
    private static void EnsurePrivateDirectory(string path)
    {
        var directory = new DirectoryInfo(path);
        if (directory.Exists)
            RejectLink(directory);

        if (OperatingSystem.IsWindows())
        {
            if (directory.Exists)
                directory.SetAccessControl(CreateOwnerOnlySecurity());
            else
                directory.Create(CreateOwnerOnlySecurity());
        }
        else
        {
            Directory.CreateDirectory(path, OwnerOnlyMode);
            File.SetUnixFileMode(path, OwnerOnlyMode); // only the owner may do this
        }

        directory.Refresh();
        VerifyPrivate(directory);
    }

    /// <summary>Only the current user may access the directory, and it must own it (the owner can always change the permissions).</summary>
    private static void VerifyPrivate(DirectoryInfo directory)
    {
        RejectLink(directory);
        var isPrivate = OperatingSystem.IsWindows()
            ? FilePermissions.IsOwnerOnly(directory)
            : (File.GetUnixFileMode(directory.FullName) & ~OwnerOnlyMode) == 0;
        if (!isPrivate)
            throw new IOException($"{directory.FullName} is accessible by other users.");
    }

    /// <summary>A symlink or junction could redirect the files to a place another user controls.</summary>
    private static void RejectLink(DirectoryInfo directory)
    {
        directory.Refresh();
        if (directory.LinkTarget != null || directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException($"{directory.FullName} is a link, not a directory.");
    }

    /// <summary>
    /// settings.json decides which program T runs as step. When other users could change it (a
    /// shared T_DATA_DIR), step is not started at all.
    /// </summary>
    private void EnsureTrustedSettings()
    {
        var dataDirectory = Path.GetDirectoryName(Path.GetFullPath(_storeDirectory))!;
        var settingsFile = Path.Combine(dataDirectory, "settings.json");
        if (FilePermissions.IsWritableByOthers(dataDirectory) || (File.Exists(settingsFile) && FilePermissions.IsWritableByOthers(settingsFile)))
        {
            throw new StepCertificateException(
                $"The data folder {dataDirectory} can be changed by other users, so its settings are not trusted to start step.");
        }
    }

    /// <summary>Full control for the current user only; inherited entries (e.g. from a shared parent) are removed.</summary>
    [SupportedOSPlatform("windows")]
    private static DirectorySecurity CreateOwnerOnlySecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            CurrentUser(),
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        return security;
    }

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier CurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User ?? throw new InvalidOperationException("The current Windows user has no SID.");
    }

    // ── Store ────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads the stored credential when it matches the profile. Before a manual connect it must
    /// stay valid for the renewal margin, otherwise only for <see cref="MinimumRemaining"/>.
    /// </summary>
    private StepCredential? TryLoad(Guid id, StepCaProfile profile, bool manualConnect)
    {
        if (ReadRecord(id) is not { } record)
            return null;

        if (record.Profile != Fingerprint(profile))
        {
            DeleteRecord(id); // issued for other CA / identity settings
            return null;
        }

        var privateKey = _encryptionService.DecryptBytes(record.PrivateKey, AssociatedData(id, record.Profile, record.Certificate));
        if (privateKey == null)
        {
            DeleteRecord(id); // tampered, moved from another profile or other master key
            return null;
        }

        try
        {
            OpenSshCertificate certificate;
            byte[] point;
            try
            {
                certificate = OpenSshFormat.ParseCertificate(record.Certificate);
                using var key = ECDsa.Create();
                key.ImportECPrivateKey(privateKey, out _);
                point = OpenSshFormat.EncodePoint(key.ExportParameters(includePrivateParameters: false));
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException)
            {
                DeleteRecord(id);
                return null;
            }

            if (Verify(certificate, point) != null)
            {
                DeleteRecord(id); // expired or not usable: do not keep the key around
                return null;
            }

            // A quarter of the lifetime at most, so a freshly issued short-lived certificate is never renewed at once.
            var lifetime = certificate.ValidBefore - certificate.ValidAfter;
            var required = manualConnect
                ? TimeSpan.FromTicks(Math.Max(MinimumRemaining.Ticks, Math.Min(RenewalMargin.Ticks, lifetime.Ticks / 4)))
                : MinimumRemaining;

            return certificate.ValidBefore - _time.GetUtcNow() >= required
                ? CreateCredential(privateKey, record.Certificate, certificate.ValidBefore)
                : null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }

    private static StepCredential CreateCredential(byte[] privateKey, string certificateLine, DateTimeOffset validBefore)
    {
        var pem = PemEncoding.Write(PrivateKeyPemLabel, privateKey);
        try { return new StepCredential(Encoding.ASCII.GetBytes(pem), certificateLine, validBefore); }
        finally { Array.Clear(pem); }
    }

    /// <summary>The context the encrypted key is bound to (authenticated by AES-GCM, not stored).</summary>
    private static byte[] AssociatedData(Guid id, string profileFingerprint, string certificateLine) =>
        Encoding.UTF8.GetBytes($"T step-ca key v{StoreVersion}\n{id:D}\n{profileFingerprint}\n{certificateLine}");

    private void Save(Guid id, long generation, StepCaProfile profile, byte[] privateKey, string certificateLine)
    {
        var fingerprint = Fingerprint(profile);
        var record = new StoredCertificate
        {
            Version = StoreVersion,
            Profile = fingerprint,
            PrivateKey = _encryptionService.EncryptBytes(privateKey, AssociatedData(id, fingerprint, certificateLine)),
            Certificate = certificateLine
        };

        lock (_storeLock)
        {
            // Deleted certificate or removed profile during the login: use it for this connect only.
            if (GenerationLocked(id) != generation || FindProfile(profile.Id) == null)
                return;

            // Kept in memory as well, so a failing disk write does not force another login.
            _records[id] = record;

            try
            {
                EnsurePrivateDirectory(_storeDirectory);
                var path = RecordPath(id);
                var tempPath = path + ".tmp";
                var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
                if (!OperatingSystem.IsWindows())
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

                using (var stream = new FileStream(tempPath, options))
                    JsonSerializer.Serialize(stream, record);
                File.Move(tempPath, path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                Debug.WriteLine($"[StepCertificateService] Could not store the certificate: {ex.Message}");
            }
        }
    }

    private StoredCertificate? ReadRecord(Guid id)
    {
        lock (_storeLock)
            return ReadRecordLocked(id);
    }

    private StoredCertificate? ReadRecordLocked(Guid id)
    {
        if (_records.TryGetValue(id, out var cached))
            return cached;

        var path = RecordPath(id);
        if (!File.Exists(path))
            return null;

        try
        {
            var record = JsonSerializer.Deserialize<StoredCertificate>(ReadLimited(path));
            if (record is not { Version: StoreVersion } || string.IsNullOrEmpty(record.PrivateKey)
                || string.IsNullOrEmpty(record.Certificate) || string.IsNullOrEmpty(record.Profile))
            {
                return null;
            }

            _records[id] = record;
            return record;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Debug.WriteLine($"[StepCertificateService] Could not read the stored certificate: {ex.Message}");
            return null;
        }
    }

    private void DeleteRecord(Guid id)
    {
        lock (_storeLock)
            DeleteRecordLocked(id);
    }

    private void DeleteRecordLocked(Guid id)
    {
        _records.Remove(id);
        var path = RecordPath(id);
        foreach (var file in new[] { path, path + ".tmp" })
        {
            try { File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"[StepCertificateService] Could not delete {file}: {ex.Message}");
            }
        }
    }

    private long GenerationLocked(Guid id) => _generations.GetValueOrDefault(id);

    /// <summary>Deletes records of removed profiles, unreadable or expired records and work directories left by a crash.</summary>
    private void PruneStore()
    {
        try
        {
            // Never delete anything through a link that points somewhere else.
            var store = new DirectoryInfo(_storeDirectory);
            if (!store.Exists || store.LinkTarget != null || store.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return;

            var profileIds = (_settingsService.Current.Step?.Profiles ?? [])
                .Select(p => StepProfileValidation.TryParseId(p.Id, out var id) ? id : Guid.Empty)
                .ToHashSet();

            lock (_storeLock)
            {
                foreach (var file in Directory.GetFiles(_storeDirectory, "*.json"))
                {
                    if (!StepProfileValidation.TryParseId(Path.GetFileNameWithoutExtension(file), out var id))
                        continue;

                    if (!profileIds.Contains(id) || ReadRecordLocked(id) is not { } record || IsExpired(record))
                        DeleteRecordLocked(id);
                }
            }

            foreach (var directory in Directory.GetDirectories(_storeDirectory, WorkDirectoryPrefix + "*"))
            {
                if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(directory) > StaleWorkDirectoryAge)
                    Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[StepCertificateService] Could not clean up the certificate store: {ex.Message}");
        }
    }

    private bool IsExpired(StoredCertificate record)
    {
        try { return OpenSshFormat.ParseCertificate(record.Certificate).ValidBefore <= _time.GetUtcNow(); }
        catch (FormatException) { return true; }
    }

    // The name comes from the parsed GUID, so a crafted profile id cannot point outside the store.
    private string RecordPath(Guid id) => Path.Combine(_storeDirectory, id.ToString("D") + ".json");

    private static string ReadLimited(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buffer = new byte[MaxFileBytes + 1];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0)
            total += read;
        if (total > MaxFileBytes)
            throw new InvalidDataException($"{Path.GetFileName(path)} is too large.");
        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    /// <summary>Hash of the settings a certificate was issued for (the name is only a label).</summary>
    private static string Fingerprint(StepCaProfile profile)
    {
        var text = string.Join('\n',
            profile.Identity.Trim(),
            profile.CaUrl.Trim(),
            profile.RootCertificatePath.Trim(),
            profile.Context.Trim(),
            profile.Provisioner.Trim(),
            string.Join(',', StepProfileValidation.SplitPrincipals(profile.Principals)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private StepCaProfile? FindProfile(string profileId)
    {
        // The settings dialog replaces the list instead of changing it, so this snapshot is safe.
        var profiles = _settingsService.Current.Step?.Profiles;
        return profiles?.FirstOrDefault(p => string.Equals(p.Id, profileId, StringComparison.Ordinal))?.Clone();
    }

    private sealed class StoredCertificate
    {
        public int Version { get; set; }
        public string Profile { get; set; } = "";
        public string PrivateKey { get; set; } = "";
        public string Certificate { get; set; } = "";
    }
}
