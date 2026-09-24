using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using Renci.SshNet;
using T.Abstractions;
using T.Models;
using T.Services;

namespace T.Tests;

public sealed class StepCertificateTests : IDisposable
{
    private const string Identity = "alice@example.com";

    private readonly TempDirectory _dir = new();
    private readonly TestCertificateAuthority _ca = new();
    private readonly ManualTime _time = new(DateTimeOffset.UtcNow);
    private readonly ConcurrentQueue<StepInvocation> _invocations = new();
    private readonly AppSettings _settings = new();
    private readonly StepCaProfile _profile = new() { Name = "Homelab", Identity = Identity, CaUrl = "https://ca.example.com" };
    private readonly EncryptionService _encryption;

    /// <summary>What the fake step returns for a public key line (a valid user certificate by default).</summary>
    private Func<string, string> _issue;
    private int _exitCode;
    private TimeSpan _stepDelay = TimeSpan.Zero;

    public StepCertificateTests()
    {
        _encryption = new EncryptionService(_dir.File("data"));
        _issue = key => _ca.Issue(key, _time.Now.AddMinutes(-1), _time.Now.AddHours(16));

        var fakeStep = _dir.File(OperatingSystem.IsWindows() ? "step.exe" : "step");
        File.WriteAllText(fakeStep, "");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(fakeStep, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        _settings.Step.ExecutablePath = fakeStep;
        _settings.Step.Profiles = [_profile];
    }

    public void Dispose()
    {
        _ca.Dispose();
        _dir.Dispose();
    }

    private string StoreDirectory => _dir.File("store");

    private StepCertificateService CreateService() =>
        new(new FakeSettingsService(_settings), _encryption, StoreDirectory, RunFakeStepAsync, _time);

    /// <summary>Runs while the fake step is working (e.g. the user deletes the certificate meanwhile).</summary>
    private Action? _duringStep;

    /// <summary>Plays step: reads the public key file and writes "&lt;name&gt;-cert.pub" next to it.</summary>
    private async Task<StepResult> RunFakeStepAsync(StepInvocation invocation, CancellationToken token)
    {
        _invocations.Enqueue(invocation);
        await Task.Delay(_stepDelay, token);
        _duringStep?.Invoke();

        // Only the public key may be handed to step, in a directory nobody else can change.
        Assert.Equal("id_ecdsa.pub", Path.GetFileName(Assert.Single(Directory.GetFiles(invocation.WorkingDirectory))));
        Assert.StartsWith(StoreDirectory + Path.DirectorySeparatorChar, invocation.WorkingDirectory);
        AssertOnlyCurrentUserHasAccess(invocation.WorkingDirectory);

        var publicKeyPath = invocation.Arguments[^1];
        var publicKey = (await File.ReadAllTextAsync(publicKeyPath, token)).Trim();
        Assert.StartsWith("ecdsa-sha2-nistp256 ", publicKey);
        Assert.DoesNotContain("PRIVATE", publicKey);

        if (_exitCode != 0)
            return new StepResult(_exitCode, ["\x1b[31merror:\x1b[0m provisioner ‮not found\x07"]);

        await File.WriteAllTextAsync(publicKeyPath[..^".pub".Length] + "-cert.pub", _issue(publicKey), token);
        return new StepResult(0, []);
    }

    // ── Command line ─────────────────────────────────────────────────────

    [Fact]
    public void SignArguments_AttachValues_AndPutPositionalsAfterSeparator()
    {
        _profile.Context = "homelab";
        _profile.Provisioner = "Google SSO";
        _profile.Principals = "alice, admin";
        var publicKey = _dir.File("id_ecdsa.pub");

        var args = StepCli.BuildSignArguments(_profile, publicKey);

        Assert.Equal(
            ["ssh", "certificate", "--sign", "--no-agent", "--force",
             "--context=homelab", "--ca-url=https://ca.example.com", "--provisioner=Google SSO",
             "--principal=alice", "--principal=admin",
             "--", Identity, publicKey],
            args);
    }

    [Theory]
    [InlineData(nameof(StepCaProfile.Identity), "--host")]
    [InlineData(nameof(StepCaProfile.Identity), "-n root")]
    [InlineData(nameof(StepCaProfile.Identity), "alice\n--host")]
    [InlineData(nameof(StepCaProfile.Identity), "alice\"--host")]
    [InlineData(nameof(StepCaProfile.Identity), "alice --host")]
    [InlineData(nameof(StepCaProfile.Identity), "")]
    [InlineData(nameof(StepCaProfile.CaUrl), "http://ca.example.com")]
    [InlineData(nameof(StepCaProfile.CaUrl), "https://user:secret@ca.example.com")]
    [InlineData(nameof(StepCaProfile.CaUrl), "https://ca.example.com --insecure")]
    [InlineData(nameof(StepCaProfile.CaUrl), "file:///etc/passwd")]
    [InlineData(nameof(StepCaProfile.RootCertificatePath), "root_ca.crt")]
    [InlineData(nameof(StepCaProfile.Context), "--offline")]
    [InlineData(nameof(StepCaProfile.Context), "a b")]
    [InlineData(nameof(StepCaProfile.Provisioner), "-x")]
    [InlineData(nameof(StepCaProfile.Provisioner), "okta\n--insecure")]
    [InlineData(nameof(StepCaProfile.Principals), "alice, -root")]
    [InlineData(nameof(StepCaProfile.Principals), "alice, ro ot")]
    [InlineData(nameof(StepCaProfile.Name), "evil‮name")]
    [InlineData(nameof(StepCaProfile.Id), "..\\..\\evil")]
    public void Validation_RejectsValuesThatCouldChangeTheCommand(string property, string value)
    {
        typeof(StepCaProfile).GetProperty(property)!.SetValue(_profile, value);

        Assert.NotNull(StepProfileValidation.Validate(_profile));
        Assert.Throws<StepCertificateException>(() => StepCli.BuildSignArguments(_profile, _dir.File("id_ecdsa.pub")));
    }

    [Fact]
    public void Validation_AcceptsTypicalProfiles()
    {
        var root = _dir.File("root_ca.crt");
        File.WriteAllText(root, "-----BEGIN CERTIFICATE-----");
        _profile.Identity = "max.müller+ssh@example.com";
        _profile.CaUrl = "https://ca.example.com:9000/";
        _profile.RootCertificatePath = root;
        _profile.Context = "ca.example.com";
        _profile.Provisioner = "admin@example.com";
        _profile.Principals = "max, root";

        Assert.Null(StepProfileValidation.Validate(_profile));
    }

    [Fact]
    public void FindExecutable_IgnoresRelativePathEntries()
    {
        var executable = CreateFakeStep(_dir.File("bin"));
        Assert.Equal(executable, StepCli.FindExecutable(null, Path.GetDirectoryName(executable)));
        Assert.Equal(executable, StepCli.FindExecutable(executable, null));

        // A step below the current directory, reachable only through relative entries: never used.
        // (Created there on purpose: across drives a "relative" path to the temp folder is absolute.)
        var name = "T.Tests-relative-" + Guid.NewGuid().ToString("N");
        try
        {
            var relativeExecutable = Path.GetRelativePath(Environment.CurrentDirectory, CreateFakeStep(Path.GetFullPath(name)));
            Assert.False(Path.IsPathRooted(relativeExecutable));

            Assert.Null(StepCli.FindExecutable(null, string.Join(Path.PathSeparator, ".", name, Path.Combine(".", name))));
            Assert.Null(StepCli.FindExecutable(relativeExecutable, null));
        }
        finally
        {
            Directory.Delete(Path.GetFullPath(name), recursive: true);
        }
    }

    private static string CreateFakeStep(string directory)
    {
        Directory.CreateDirectory(directory);
        var executable = Path.Combine(directory, OperatingSystem.IsWindows() ? "step.exe" : "step");
        File.WriteAllText(executable, "");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        return executable;
    }

    [Fact]
    public void SearchDirectories_DoNotDependOnTheProcessPathAlone()
    {
        Environment.SetEnvironmentVariable("T_TEST_BIN", _dir.Path);

        var directories = StepCli.DefaultSearchDirectories($"%T_TEST_BIN%{Path.PathSeparator}relative").ToList();

        Assert.Equal(_dir.Path, directories[0]); // variables are expanded
        Assert.Equal(_dir.Path, StepCli.FindExecutableIn(null, directories) is { } found ? Path.GetDirectoryName(found) : null);
        if (OperatingSystem.IsWindows())
        {
            // An IDE or debugger with a trimmed PATH still finds tools installed with winget.
            var winget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links");
            Assert.Contains(winget, directories, StringComparer.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ExecutablePath_MustBeAnExeOnWindows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");

        var batch = _dir.File("step.cmd");
        File.WriteAllText(batch, "@echo off");

        Assert.NotNull(StepProfileValidation.ValidateExecutablePath(batch));
        Assert.Null(StepCli.FindExecutable(batch, null));
    }

    [Fact]
    public void Sanitize_RemovesEscapeSequencesControlAndFormatCharacters()
    {
        var text = "\x1b[31mred\x1b[0m \x1b]0;title\x07ok‮txt.exe\r\u009b2J\ttab\0";

        var clean = StepCli.Sanitize(text);

        Assert.Equal("red oktxt.exe2J tab", clean);
        Assert.Equal(11, StepCli.Sanitize(new string('a', 100), 10).Length);
    }

    [Fact]
    public async Task RunAsync_ClosesStandardInput_AndKillsOnTimeout()
    {
        // "findstr"/"cat" read standard input until it ends: they exit at once only when it is closed.
        var (reader, readerArgs) = OperatingSystem.IsWindows() ? ("findstr.exe", new[] { "x" }) : ("/bin/cat", Array.Empty<string>());
        var stopwatch = Stopwatch.StartNew();
        await StepCli.RunAsync(new StepInvocation(Resolve(reader), readerArgs, _dir.Path, TimeSpan.FromSeconds(20), null), TestContext.Current.CancellationToken);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15));

        var (sleeper, sleeperArgs) = OperatingSystem.IsWindows()
            ? ("ping.exe", new[] { "-n", "30", "127.0.0.1" })
            : ("/bin/sleep", new[] { "30" });
        stopwatch.Restart();
        var ex = await Assert.ThrowsAsync<StepCertificateException>(() =>
            StepCli.RunAsync(new StepInvocation(Resolve(sleeper), sleeperArgs, _dir.Path, TimeSpan.FromSeconds(1), null), TestContext.Current.CancellationToken));
        Assert.Contains("did not finish", ex.Message);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15));
    }

    private static string Resolve(string name) =>
        Path.IsPathRooted(name) ? name : Path.Combine(Environment.SystemDirectory, name);

    // ── Certificate format ───────────────────────────────────────────────

    [Fact]
    public void ParseCertificate_ReadsUserCertificate()
    {
        var certificate = OpenSshFormat.ParseCertificate(
            _ca.Issue(TestCertificateAuthority.NewPublicKeyLine(), _time.Now, _time.Now.AddHours(1), keyId: Identity, principals: ["alice", "admin"]));

        Assert.Equal(OpenSshFormat.UserCertificate, certificate.Type);
        Assert.Equal(Identity, certificate.KeyId);
        Assert.Equal(["alice", "admin"], certificate.Principals);
        Assert.Equal(_time.Now.AddHours(1).ToUnixTimeSeconds(), certificate.ValidBefore.ToUnixTimeSeconds());
    }

    [Fact]
    public void ParseCertificate_RejectsEveryTruncationAndTrailingData()
    {
        var line = _ca.Issue(TestCertificateAuthority.NewPublicKeyLine(), _time.Now, _time.Now.AddHours(1));
        var parts = line.Split(' ');
        var blob = Convert.FromBase64String(parts[1]);

        for (int length = 0; length < blob.Length; length++)
        {
            var truncated = $"{parts[0]} {Convert.ToBase64String(blob.AsSpan(0, length))}";
            Assert.Throws<FormatException>(() => OpenSshFormat.ParseCertificate(truncated));
        }

        Assert.Throws<FormatException>(() => OpenSshFormat.ParseCertificate($"{parts[0]} {Convert.ToBase64String([.. blob, 0])}"));
        Assert.Throws<FormatException>(() => OpenSshFormat.ParseCertificate($"ssh-rsa-cert-v01@openssh.com {parts[1]}"));
        Assert.Throws<FormatException>(() => OpenSshFormat.ParseCertificate("garbage"));
    }

    // ── Service ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Renewal_SignsOnlyThePublicKey_AndStoresThePrivateKeyEncrypted()
    {
        var service = CreateService();
        var progress = new List<string>();

        using var credential = await service.GetCredentialAsync(_profile.Id, allowRenewal: true, progress.Add, TestContext.Current.CancellationToken);

        var invocation = Assert.Single(_invocations);
        Assert.Equal(_settings.Step.ExecutablePath, invocation.Executable);
        Assert.Equal(["--", Identity], invocation.Arguments.Skip(invocation.Arguments.Count - 3).Take(2));
        Assert.False(Directory.Exists(invocation.WorkingDirectory), "the temporary directory must be deleted");
        Assert.Contains(progress, p => p.Contains("Homelab"));

        // SSH.NET accepts the in-memory key together with its certificate.
        using var key = new PrivateKeyFile(
            new MemoryStream(credential.PrivateKeyPem), null, new MemoryStream(Encoding.ASCII.GetBytes(credential.Certificate)));
        Assert.Equal(Identity, key.Certificate!.KeyId);

        // On disk only the encrypted private key.
        var stored = File.ReadAllText(Assert.Single(Directory.GetFiles(StoreDirectory)));
        var pem = Encoding.ASCII.GetString(credential.PrivateKeyPem);
        var der = pem.Split('\n').Where(l => !l.StartsWith("-----", StringComparison.Ordinal)).Select(l => l.Trim());
        Assert.DoesNotContain("PRIVATE KEY", stored);
        Assert.DoesNotContain(string.Concat(der)[..40], stored);
        Assert.Contains("\"PrivateKey\":\"v2:", stored);

        credential.Dispose();
        Assert.All(credential.PrivateKeyPem, b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task StoredCertificate_IsReused_AlsoAfterRestart()
    {
        using (await CreateService().GetCredentialAsync(_profile.Id, true, null, TestContext.Current.CancellationToken)) { }
        using (await CreateService().GetCredentialAsync(_profile.Id, false, null, TestContext.Current.CancellationToken)) { }
        using (await CreateService().GetCredentialAsync(_profile.Id, true, null, TestContext.Current.CancellationToken)) { }

        Assert.Single(_invocations);
        Assert.NotNull(CreateService().GetValidUntil(_profile));
    }

    [Fact]
    public async Task ConcurrentConnects_ShareOneLogin()
    {
        _stepDelay = TimeSpan.FromMilliseconds(300);
        var service = CreateService();

        var credentials = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ =>
            service.GetCredentialAsync(_profile.Id, true, null, TestContext.Current.CancellationToken)));

        Assert.Single(_invocations);
        Assert.All(credentials, c => Assert.Equal(credentials[0].Certificate, c.Certificate));
        foreach (var credential in credentials) credential.Dispose();
    }

    [Fact]
    public async Task AutomaticReconnect_NeverStartsStep()
    {
        var ex = await Assert.ThrowsAsync<StepCertificateException>(() =>
            CreateService().GetCredentialAsync(_profile.Id, allowRenewal: false, null, TestContext.Current.CancellationToken));

        Assert.Contains("expired", ex.Message);
        Assert.Empty(_invocations);
    }

    [Fact]
    public async Task ExpiredCertificate_IsDeleted_AndRenewedOnlyOnManualConnect()
    {
        var service = CreateService();
        using (await service.GetCredentialAsync(_profile.Id, true, null, TestContext.Current.CancellationToken)) { }

        _time.Now = _time.Now.AddHours(17);

        await Assert.ThrowsAsync<StepCertificateException>(() =>
            service.GetCredentialAsync(_profile.Id, false, null, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(StoreDirectory));
        Assert.Null(service.GetValidUntil(_profile));

        using (await service.GetCredentialAsync(_profile.Id, true, null, TestContext.Current.CancellationToken)) { }
        Assert.Equal(2, _invocations.Count);
    }

    [Fact]
    public async Task CertificateAboutToExpire_IsRenewedBeforeManualConnect_ButStillUsedForReconnects()
    {
        var service = CreateService();
        using (await service.GetCredentialAsync(_profile.Id, true, null, TestContext.Current.CancellationToken)) { }

        _time.Now = _time.Now.AddHours(16).AddMinutes(-3);

        using (await service.GetCredentialAsync(_profile.Id, false, null, TestContext.Current.CancellationToken)) { }
        Assert.Single(_invocations);

        using (await service.GetCredentialAsync(_profile.Id, true, null, TestContext.Current.CancellationToken)) { }
        Assert.Equal(2, _invocations.Count);
    }

    [Fact]
    public async Task ShortLivedCertificate_IsNotRenewedRightAfterIssue()
    {
        // e.g. jump host and target with the same CA: one login, not two.
        _issue = key => _ca.Issue(key, _time.Now.AddMinutes(-1), _time.Now.AddMinutes(4));
        var service = CreateService();

        for (int i = 0; i < 3; i++)
            using (await service.GetCredentialAsync(_profile.Id, true, null, TestContext.Current.CancellationToken)) { }

        Assert.Single(_invocations);
    }

    [Fact]
    public async Task DeletingDuringLogin_StoresNothing()
    {
        var service = CreateService();
        _duringStep = () => service.Forget(_profile.Id);

        using (await service.GetCredentialAsync(_profile.Id, true, null, TestContext.Current.CancellationToken)) { }

        Assert.Empty(Directory.GetFiles(StoreDirectory));
        Assert.Null(service.GetValidUntil(_profile));
    }

    [Fact]
    public async Task RemovingTheProfileDuringLogin_StoresNothing()
    {
        var service = CreateService();
        _duringStep = () => _settings.Step.Profiles = [];

        using (await service.GetCredentialAsync(_profile.Id, true, null, TestContext.Current.CancellationToken)) { }

        Assert.Empty(Directory.GetFiles(StoreDirectory));
    }

    [Fact]
    public async Task Startup_DeletesRecordsOfRemovedProfiles_ExpiredRecords_AndOldWorkDirectories()
    {
        var other = new StepCaProfile { Name = "Other", Identity = "bob@example.com" };
        _settings.Step.Profiles = [_profile, other];
        using (await CreateService().GetCredentialAsync(_profile.Id, true, null, TestContext.Current.CancellationToken)) { }
        _issue = key => _ca.Issue(key, _time.Now.AddMinutes(-1), _time.Now.AddMinutes(10));
        using (await CreateService().GetCredentialAsync(other.Id, true, null, TestContext.Current.CancellationToken)) { }
        var leftover = Directory.CreateDirectory(Path.Combine(StoreDirectory, "tmp-crashed")).FullName;
        Directory.SetLastWriteTimeUtc(leftover, DateTime.UtcNow.AddDays(-1));
        Assert.Equal(2, Directory.GetFiles(StoreDirectory).Length);

        _settings.Step.Profiles = [other];  // _profile removed
        _time.Now = _time.Now.AddMinutes(20); // other's certificate expired
        _ = CreateService();

        Assert.Empty(Directory.GetFiles(StoreDirectory));
        Assert.False(Directory.Exists(leftover));
    }

    [Fact]
    public async Task StoredKey_CannotBeMovedToAnotherProfile()
    {
        // Same settings, so the profile fingerprint matches: only the binding to the id protects.
        var twin = new StepCaProfile { Name = "Twin", Identity = _profile.Identity, CaUrl = _profile.CaUrl };
        _settings.Step.Profiles = [_profile, twin];
        using (await CreateService().GetCredentialAsync(_profile.Id, true, null, TestContext.Current.CancellationToken)) { }
        File.Copy(Path.Combine(StoreDirectory, _profile.Id + ".json"), Path.Combine(StoreDirectory, twin.Id + ".json"));

        await Assert.ThrowsAsync<StepCertificateException>(() =>
            CreateService().GetCredentialAsync(twin.Id, false, null, TestContext.Current.CancellationToken));
        Assert.Single(_invocations);
    }

    [Fact]
    public void ProfileIds_MustBeCanonical()
    {
        var id = Guid.NewGuid();

        Assert.True(StepProfileValidation.TryParseId(id.ToString("D"), out _));
        Assert.False(StepProfileValidation.TryParseId(id.ToString("D").ToUpperInvariant(), out _));
        Assert.False(StepProfileValidation.TryParseId($" {id:D}\n", out _));
        Assert.False(StepProfileValidation.TryParseId(id.ToString("N"), out _));
    }

    [Fact]
    public void NetworkPaths_AreRejectedWithoutTouchingThem()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");

        string[] networkPaths =
        [
            @"\\attacker\share\step.exe", @"//attacker/share/step.exe", @"/\attacker\share\step.exe",
            @"\/attacker\share\step.exe", @"\\?\C:\step.exe", @"\\.\C:\step.exe", @"\??\UNC\attacker\share\step.exe",
            @"C:\tools\step.exe:hidden.exe"
        ];
        foreach (var path in networkPaths)
        {
            Assert.False(StepProfileValidation.IsLocalPath(path), path);
            Assert.NotNull(StepProfileValidation.ValidateExecutablePath(path));
        }

        Assert.Contains("local", StepProfileValidation.ValidateRootCertificatePath(@"/\attacker\share\root_ca.crt"));
        Assert.Null(StepCli.FindExecutable(null, @"/\attacker\share"));
        Assert.True(StepProfileValidation.IsLocalPath(_settings.Step.ExecutablePath));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void StepInAFolderOthersCanChange_IsRejected()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");

        // Like C:\tools below a C:\ that lets every user create and modify folders.
        var tools = Directory.CreateDirectory(_dir.File("tools")).FullName;
        GrantAuthenticatedUsersModify(tools);
        var executable = Path.Combine(tools, "step.exe");
        File.WriteAllText(executable, "");

        Assert.Contains("other users", StepProfileValidation.ValidateExecutablePath(executable));
        Assert.Null(StepCli.FindExecutable(null, tools));
        Assert.Null(StepCli.FindExecutable(executable, null));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task DataFolderOthersCanChange_DoesNotStartStep()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");

        // settings.json there decides which program runs: a shared T_DATA_DIR must not be trusted.
        GrantAuthenticatedUsersModify(_dir.Path);

        var ex = await Assert.ThrowsAsync<StepCertificateException>(() =>
            CreateService().GetCredentialAsync(_profile.Id, true, null, TestContext.Current.CancellationToken));

        Assert.Contains("other users", ex.Message);
        Assert.Empty(_invocations);
    }

    [Fact]
    public async Task StoreThatIsALink_IsRefused_AndNeverPruned()
    {
        var target = Directory.CreateDirectory(_dir.File("elsewhere")).FullName;
        var victim = Path.Combine(target, _profile.Id + ".json");
        File.WriteAllText(victim, "not T's");
        CreateDirectoryLink(StoreDirectory, target);

        _settings.Step.Profiles = []; // the victim file looks like a record of a removed profile
        _ = CreateService();
        Assert.True(File.Exists(victim));

        _settings.Step.Profiles = [_profile];
        var ex = await Assert.ThrowsAsync<StepCertificateException>(() =>
            CreateService().GetCredentialAsync(_profile.Id, true, null, TestContext.Current.CancellationToken));
        Assert.Contains("link", ex.Message);
        Assert.Empty(_invocations);
        Assert.Equal(["not T's"], Directory.GetFiles(target).Select(File.ReadAllText));

        Directory.Delete(StoreDirectory); // removes only the link
        Assert.True(File.Exists(victim));
    }

    [Fact]
    public void StepCertificateException_SanitizesItsMessage()
    {
        var ex = new StepCertificateException("bad \x1b]52;c;ZXZpbA==\x07\x1b[2Jvalue\r\n");

        Assert.Equal("bad value", ex.Message);
    }

    private static void CreateDirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }

        // A junction needs no privilege (symbolic links do).
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", "mklink", "/J", link, target }, CreateNoWindow = true, RedirectStandardOutput = true })!;
        mklink.WaitForExit();
        Assert.Equal(0, mklink.ExitCode);
    }

    [SupportedOSPlatform("windows")]
    private static void GrantAuthenticatedUsersModify(string directory)
    {
        var info = new DirectoryInfo(directory);
        var security = info.GetAccessControl();
        security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.AuthenticatedUserSid, null),
            System.Security.AccessControl.FileSystemRights.Modify,
            System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
            System.Security.AccessControl.PropagationFlags.None,
            System.Security.AccessControl.AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void WritableByOthers_IsRejectedOnUnix()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix only");

        var executable = _settings.Step.ExecutablePath;
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.OtherWrite);
        Assert.NotNull(StepProfileValidation.ValidateExecutablePath(executable));

        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var directory = Path.GetDirectoryName(executable)!;
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherWrite);
        Assert.Null(StepCli.FindExecutable(null, directory));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task SharedStoreFolder_IsRestrictedToTheCurrentUser()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");

        // Like a shared C:\Temp: everybody may modify, inherited by new subdirectories.
        Directory.CreateDirectory(StoreDirectory);
        GrantAuthenticatedUsersModify(StoreDirectory);

        using (await CreateService().GetCredentialAsync(_profile.Id, true, null, TestContext.Current.CancellationToken)) { }

        AssertOnlyCurrentUserHasAccess(StoreDirectory);
    }

    [Fact]
    public void RejectedPrincipal_IsSanitizedInTheMessage()
    {
        var error = StepProfileValidation.ValidatePrincipals("alice, \x1b]52;c;ZXZpbA==\x07\x1b[2J");

        Assert.NotNull(error);
        Assert.DoesNotContain('\x1b', error);
        Assert.DoesNotContain('\x07', error);
    }

    private static void AssertOnlyCurrentUserHasAccess(string directory)
    {
        if (OperatingSystem.IsWindows())
            AssertOnlyCurrentUserInAcl(directory);
        else
            Assert.Equal(UnixFileMode.None, File.GetUnixFileMode(directory) & ~(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute));
    }

    [SupportedOSPlatform("windows")]
    private static void AssertOnlyCurrentUserInAcl(string directory)
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var security = new DirectoryInfo(directory).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        foreach (System.Security.AccessControl.FileSystemAccessRule rule in
            security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)))
        {
            Assert.Equal<System.Security.Principal.IdentityReference>(identity.User!, rule.IdentityReference);
        }
    }

    public static TheoryData<string> BadCertificates => ["other key", "host certificate", "expired", "not yet valid", "garbage"];

    [Theory]
    [MemberData(nameof(BadCertificates))]
    public async Task UnsuitableCertificates_AreRejected_AndNotStored(string kind)
    {
        _issue = kind switch
        {
            "other key" => _ => _ca.Issue(TestCertificateAuthority.NewPublicKeyLine(), _time.Now, _time.Now.AddHours(1)),
            "host certificate" => key => _ca.Issue(key, _time.Now, _time.Now.AddHours(1), type: 2),
            "expired" => key => _ca.Issue(key, _time.Now.AddHours(-2), _time.Now.AddHours(-1)),
            "not yet valid" => key => _ca.Issue(key, _time.Now.AddHours(1), _time.Now.AddHours(2)),
            _ => _ => "ecdsa-sha2-nistp256-cert-v01@openssh.com AAAA"
        };

        await Assert.ThrowsAsync<StepCertificateException>(() =>
            CreateService().GetCredentialAsync(_profile.Id, true, null, TestContext.Current.CancellationToken));

        Assert.False(Directory.Exists(StoreDirectory) && Directory.GetFiles(StoreDirectory).Length > 0);
    }

    [Fact]
    public async Task StepFailure_IsReportedWithSanitizedOutput()
    {
        _exitCode = 1;

        var ex = await Assert.ThrowsAsync<StepCertificateException>(() =>
            CreateService().GetCredentialAsync(_profile.Id, true, null, TestContext.Current.CancellationToken));

        Assert.Equal("step failed: error: provisioner not found", ex.Message);
    }

    [Fact]
    public async Task ChangedProfile_DiscardsTheStoredCertificate()
    {
        var service = CreateService();
        using (await service.GetCredentialAsync(_profile.Id, true, null, TestContext.Current.CancellationToken)) { }

        _profile.Name = "Renamed"; // a label only
        Assert.NotNull(service.GetValidUntil(_profile));

        _profile.CaUrl = "https://other-ca.example.com";
        Assert.Null(service.GetValidUntil(_profile));
        await Assert.ThrowsAsync<StepCertificateException>(() =>
            service.GetCredentialAsync(_profile.Id, false, null, TestContext.Current.CancellationToken));
        Assert.Single(_invocations);
    }

    [Fact]
    public async Task TamperedStore_IsIgnored()
    {
        using (await CreateService().GetCredentialAsync(_profile.Id, true, null, TestContext.Current.CancellationToken)) { }
        var path = Assert.Single(Directory.GetFiles(StoreDirectory));
        var json = File.ReadAllText(path);
        var index = json.IndexOf("\"PrivateKey\":\"v2:", StringComparison.Ordinal) + 20;
        File.WriteAllText(path, json[..index] + (json[index] == 'A' ? 'B' : 'A') + json[(index + 1)..]);

        using (await CreateService().GetCredentialAsync(_profile.Id, true, null, TestContext.Current.CancellationToken)) { }

        Assert.Equal(2, _invocations.Count);
    }

    [Fact]
    public async Task Forget_DeletesTheStoredKey_AndIgnoresInvalidIds()
    {
        var service = CreateService();
        using (await service.GetCredentialAsync(_profile.Id, true, null, TestContext.Current.CancellationToken)) { }
        var outside = _dir.File("victim.json");
        File.WriteAllText(outside, "keep");

        service.Forget(Path.Combine("..", "victim"));
        service.Forget(_profile.Id);

        Assert.Empty(Directory.GetFiles(StoreDirectory));
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public async Task MissingProfile_OrMissingStep_FailWithAClearMessage()
    {
        var missing = await Assert.ThrowsAsync<StepCertificateException>(() =>
            CreateService().GetCredentialAsync(Guid.NewGuid().ToString(), true, null, TestContext.Current.CancellationToken));
        Assert.Contains("no longer exists", missing.Message);

        _settings.Step.ExecutablePath = _dir.File("missing.exe");
        var noStep = await Assert.ThrowsAsync<StepCertificateException>(() =>
            CreateService().GetCredentialAsync(_profile.Id, true, null, TestContext.Current.CancellationToken));
        Assert.Contains("not found", noStep.Message);
        Assert.Empty(_invocations);
    }

    [Fact]
    public void EncryptBytes_RoundTrips_AndDetectsTampering()
    {
        byte[] secret = [1, 2, 3, 250];
        var encrypted = _encryption.EncryptBytes(secret);

        Assert.Equal(secret, _encryption.DecryptBytes(encrypted));
        var tampered = Convert.FromBase64String(encrypted["v2:".Length..]);
        tampered[^1] ^= 0x01;
        Assert.Null(_encryption.DecryptBytes("v2:" + Convert.ToBase64String(tampered)));
        Assert.Null(_encryption.DecryptBytes("plain text"));

        var bound = _encryption.EncryptBytes(secret, "profile A"u8);
        Assert.Equal(secret, _encryption.DecryptBytes(bound, "profile A"u8));
        Assert.Null(_encryption.DecryptBytes(bound, "profile B"u8));
        Assert.Null(_encryption.DecryptBytes(bound));
    }

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeSettingsService(AppSettings settings) : ISettingsService
    {
        public AppSettings Current { get; } = settings;
        public event Action<AppSettings>? SettingsChanged { add { } remove { } }
        public void Save() { }
        public Task SaveAsync() => Task.CompletedTask;
    }
}
