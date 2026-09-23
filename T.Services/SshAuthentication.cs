using System.Security.Cryptography;
using Renci.SshNet;
using Renci.SshNet.Common;
using T.Models;

namespace T.Services;

/// <summary>
/// Builds <see cref="ConnectionInfo"/> instances: authentication methods, private key
/// loading (explicit key, OpenSSH certificates, default identity files) and algorithm hardening.
/// </summary>
internal static class SshAuthentication
{
    private static readonly string[] DefaultIdentityFiles = ["id_ed25519", "id_ecdsa", "id_rsa"];
    private static readonly string[] PasswordPromptWords = ["password", "passwort", "kennwort"];

    // Broken or deprecated algorithms that OpenSSH no longer offers by default.
    private static readonly string[] WeakKeyExchanges = ["diffie-hellman-group1-sha1"];
    private static readonly string[] WeakCiphers = ["3des-cbc"];

    /// <summary>
    /// Answers keyboard-interactive prompts that cannot be satisfied with the stored
    /// password (e.g. a 2FA code). Called on an SSH.NET worker thread; returning
    /// <see langword="null"/> aborts that authentication method.
    /// </summary>
    public delegate string? PromptHandler(AuthPromptRequest request);

    public static (ConnectionInfo Info, List<IDisposable> Resources) Create(
        string host,
        int port,
        string displayHost,
        SshCredentials credentials,
        TimeSpan timeout,
        bool useDefaultIdentityFiles,
        PromptHandler? promptHandler,
        CancellationToken promptToken = default)
    {
        var username = credentials.Username.Trim();
        if (username.Length == 0)
            throw new SshAuthenticationException($"No username configured for {displayHost}.");

        var resources = new List<IDisposable>();
        try
        {
            var methods = new List<AuthenticationMethod>();

            var keys = LoadPrivateKeys(credentials, useDefaultIdentityFiles);
            resources.AddRange(keys);
            if (keys.Count > 0)
                methods.Add(new PrivateKeyAuthenticationMethod(username, [.. keys]));

            var password = credentials.Password;
            if (!string.IsNullOrEmpty(password))
                methods.Add(new PasswordAuthenticationMethod(username, password));

            // Many servers (PAM) only offer keyboard-interactive for passwords. Without a
            // handler SSH.NET aborts the whole authentication on the first prompt.
            var keyboard = new KeyboardInteractiveAuthenticationMethod(username);
            keyboard.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts)
                {
                    if (!prompt.IsEchoed && !string.IsNullOrEmpty(password) && IsPasswordPrompt(prompt.Request))
                    {
                        prompt.Response = password;
                    }
                    else if (promptHandler != null && !promptToken.IsCancellationRequested)
                    {
                        prompt.Response = promptHandler(new AuthPromptRequest
                        {
                            Host = displayHost,
                            Instruction = e.Instruction ?? "",
                            Prompt = prompt.Request,
                            IsEchoed = prompt.IsEchoed,
                            CancellationToken = promptToken
                        });
                    }
                }
            };
            methods.Add(keyboard);

            // The keyboard-interactive method is deliberately NOT disposed: SSH.NET answers
            // prompts on a thread-pool thread and signals an internal wait handle afterwards.
            // Disposing the method while a prompt is still open nulls that handle and the late
            // callback crashes the process with an unhandled NullReferenceException.
            resources.AddRange(methods.Where(m => m is not KeyboardInteractiveAuthenticationMethod).OfType<IDisposable>());

            var info = new ConnectionInfo(host, port, username, [.. methods]) { Timeout = timeout };
            Harden(info);
            return (info, resources);
        }
        catch
        {
            DisposeAll(resources);
            throw;
        }
    }

    internal static bool IsPasswordPrompt(string request) =>
        PasswordPromptWords.Any(w => request.Contains(w, StringComparison.OrdinalIgnoreCase));

    private static void Harden(ConnectionInfo info)
    {
        foreach (var name in WeakKeyExchanges) info.KeyExchangeAlgorithms.Remove(name);
        foreach (var name in WeakCiphers) info.Encryptions.Remove(name);
    }

    // ── Private keys ─────────────────────────────────────────────────────

    private static List<PrivateKeyFile> LoadPrivateKeys(SshCredentials credentials, bool useDefaultIdentityFiles)
    {
        var keys = new List<PrivateKeyFile>();
        var passphrase = string.IsNullOrEmpty(credentials.PrivateKeyPassword) ? null : credentials.PrivateKeyPassword;
        var explicitPath = ExpandPath(credentials.PrivateKeyPath);

        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            if (!File.Exists(explicitPath))
                throw new SshPrivateKeyException($"Private key file not found: {explicitPath}");

            keys.Add(LoadKey(explicitPath, passphrase, isExplicit: true)!);
            return keys;
        }

        if (!useDefaultIdentityFiles)
            return keys;

        var sshDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        foreach (var name in DefaultIdentityFiles)
        {
            var path = Path.Combine(sshDir, name);
            if (File.Exists(path) && LoadKey(path, passphrase, isExplicit: false) is { } key)
                keys.Add(key);
        }

        return keys;
    }

    /// <summary>
    /// Loads a key (with its OpenSSH certificate "&lt;key&gt;-cert.pub" when present).
    /// Default identity files that cannot be loaded (e.g. encrypted) are skipped silently.
    /// </summary>
    private static PrivateKeyFile? LoadKey(string path, string? passphrase, bool isExplicit)
    {
        var fileName = Path.GetFileName(path);
        try
        {
            var certificatePath = path + "-cert.pub";
            if (File.Exists(certificatePath))
            {
                try { return new PrivateKeyFile(path, passphrase, certificatePath); }
                catch (Exception ex) when (IsKeyLoadError(ex) && ex is not SshPassPhraseNullOrEmptyException)
                {
                    // Unusable certificate: fall back to the plain key.
                }
            }

            return new PrivateKeyFile(path, passphrase);
        }
        catch (SshPassPhraseNullOrEmptyException) when (isExplicit)
        {
            throw new SshPrivateKeyException($"The private key '{fileName}' is encrypted. Please enter its passphrase.");
        }
        catch (Exception ex) when (isExplicit && IsKeyLoadError(ex))
        {
            var hint = passphrase != null ? "Is the passphrase correct?" : "Unsupported key format?";
            throw new SshPrivateKeyException($"The private key '{fileName}' could not be loaded: {ex.Message} {hint}", ex);
        }
        catch (Exception ex) when (!isExplicit && IsKeyLoadError(ex))
        {
            return null;
        }
    }

    private static bool IsKeyLoadError(Exception ex) =>
        ex is SshException or CryptographicException or InvalidOperationException or FormatException
            or ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException;

    /// <summary>Expands "~/" and environment variables (%USERPROFILE%, $HOME is not expanded by .NET).</summary>
    public static string ExpandPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";

        var trimmed = path.Trim().Trim('"');
        if (trimmed == "~" || trimmed.StartsWith("~/", StringComparison.Ordinal) || trimmed.StartsWith("~\\", StringComparison.Ordinal))
            trimmed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), trimmed.Length > 2 ? trimmed[2..] : "");

        return Environment.ExpandEnvironmentVariables(trimmed);
    }

    public static void DisposeAll(List<IDisposable> resources)
    {
        for (int i = resources.Count - 1; i >= 0; i--)
        {
            try { resources[i].Dispose(); }
            catch (Exception) { /* best effort cleanup */ }
        }
        resources.Clear();
    }
}
