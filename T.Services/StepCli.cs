using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using T.Models;

namespace T.Services;

/// <summary>One run of the step CLI.</summary>
/// <param name="Output">Receives sanitized output lines while step runs (e.g. the login URL).</param>
internal sealed record StepInvocation(
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    TimeSpan Timeout,
    Action<string>? Output);

/// <param name="Lines">The last sanitized output lines (for error messages).</param>
internal sealed record StepResult(int ExitCode, IReadOnlyList<string> Lines);

/// <summary>
/// Starts the step CLI without a shell: the executable is resolved to an absolute path
/// (never the current directory), arguments are passed as a list, option values are attached
/// ("--name=value") and positional arguments follow "--". Standard input is closed so step
/// can never wait for a prompt, and the output is size limited and sanitized before it is
/// shown, because it can contain messages from the CA.
/// </summary>
internal static partial class StepCli
{
    private const int MaxLineLength = 1000;
    private const int MaxKeptLines = 20;
    private const int MaxForwardedLines = 50;
    private static readonly TimeSpan OutputDrainTimeout = TimeSpan.FromSeconds(2);

    // macOS GUI apps do not inherit the shell PATH (Homebrew installs to /opt/homebrew/bin).
    private static readonly string[] UnixFallbackDirectories = ["/opt/homebrew/bin", "/usr/local/bin", "/usr/bin"];

    /// <summary>Searches only the directories of <paramref name="searchPath"/> (a PATH value).</summary>
    public static string? FindExecutable(string? configuredPath, string? searchPath) =>
        FindExecutableIn(configuredPath, SplitPath(searchPath));

    public static string? FindExecutableIn(string? configuredPath, IEnumerable<string> searchDirectories)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return StepProfileValidation.ValidateExecutablePath(configuredPath) == null
                ? Path.GetFullPath(configuredPath.Trim())
                : null;
        }

        var fileName = OperatingSystem.IsWindows() ? "step.exe" : "step";
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        foreach (var directory in searchDirectories.Distinct(comparer))
        {
            // A relative entry ("." or "bin") would resolve against the current directory. The
            // candidate check below also skips folders other users can change.
            if (!Path.IsPathFullyQualified(directory) || !StepProfileValidation.IsLocalPath(directory))
                continue;

            var candidate = Path.Combine(directory, fileName);
            if (StepProfileValidation.ValidateExecutablePath(candidate) == null)
                return Path.GetFullPath(candidate);
        }

        return null;
    }

    /// <summary>
    /// PATH of this process, then (Windows) the current user and machine PATH from the registry and
    /// the usual install folders. An IDE, debugger or launcher can hand T an outdated or trimmed
    /// PATH that misses a tool installed later (e.g. with winget).
    /// </summary>
    public static IEnumerable<string> DefaultSearchDirectories(string? processPath)
    {
        var directories = SplitPath(processPath).ToList();
        if (!OperatingSystem.IsWindows())
            return directories.Concat(UnixFallbackDirectories);

        directories.AddRange(SplitPath(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User)));
        directories.AddRange(SplitPath(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine)));

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (localAppData.Length > 0) directories.Add(Path.Combine(localAppData, "Microsoft", "WinGet", "Links"));
        if (userProfile.Length > 0) directories.Add(Path.Combine(userProfile, "scoop", "shims"));
        if (programData.Length > 0) directories.Add(Path.Combine(programData, "chocolatey", "bin"));
        return directories;
    }

    /// <summary>PATH entries with quotes removed and %VARIABLES% expanded.</summary>
    private static IEnumerable<string> SplitPath(string? searchPath) =>
        (searchPath ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => Environment.ExpandEnvironmentVariables(d.Trim('"')));

    /// <summary>Arguments for "step ssh certificate --sign" of <paramref name="publicKeyPath"/>.</summary>
    public static List<string> BuildSignArguments(StepCaProfile profile, string publicKeyPath)
    {
        if (StepProfileValidation.Validate(profile) is { } error)
            throw new StepCertificateException($"step-ca profile '{Sanitize(profile.Name)}': {error}");
        if (!Path.IsPathFullyQualified(publicKeyPath))
            throw new ArgumentException("The public key path must be absolute.", nameof(publicKeyPath));

        var arguments = new List<string> { "ssh", "certificate", "--sign", "--no-agent", "--force" };
        AddOption(arguments, "context", profile.Context);
        AddOption(arguments, "ca-url", profile.CaUrl);
        AddOption(arguments, "root", profile.RootCertificatePath);
        AddOption(arguments, "provisioner", profile.Provisioner);
        foreach (var principal in StepProfileValidation.SplitPrincipals(profile.Principals))
            AddOption(arguments, "principal", principal);

        // Everything after "--" is positional, even a value starting with '-'.
        arguments.Add("--");
        arguments.Add(profile.Identity.Trim());
        arguments.Add(publicKeyPath);
        return arguments;
    }

    // Attached to the option name, a value can never be parsed as an option of its own.
    private static void AddOption(List<string> arguments, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            arguments.Add($"--{name}={value.Trim()}");
    }

    public static async Task<StepResult> RunAsync(StepInvocation invocation, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(invocation.Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = invocation.WorkingDirectory
        };
        foreach (var argument in invocation.Arguments)
            startInfo.ArgumentList.Add(argument);

        var sync = new Lock();
        var lines = new Queue<string>();
        var forwarded = 0;

        void OnLine(string raw)
        {
            var text = Sanitize(raw);
            if (text.Length == 0) return;
            lock (sync)
            {
                lines.Enqueue(text);
                if (lines.Count > MaxKeptLines) lines.Dequeue();
                if (++forwarded > MaxForwardedLines) return;
            }
            invocation.Output?.Invoke(text);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            throw new StepCertificateException($"step could not be started: {ex.Message}", ex);
        }

        var exited = false;
        try
        {
            // Without input step fails at a prompt instead of waiting for an answer nobody can type.
            try { process.StandardInput.Close(); }
            catch (IOException) { }

            var pumps = Task.WhenAll(
                PumpAsync(process.StandardOutput, OnLine),
                PumpAsync(process.StandardError, OnLine));

            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(invocation.Timeout);
                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new StepCertificateException($"step did not finish within {invocation.Timeout.TotalMinutes:0} minutes.");
                }
            }
            exited = true;

            try { await pumps.WaitAsync(OutputDrainTimeout, CancellationToken.None); }
            catch (TimeoutException) { }

            lock (sync)
                return new StepResult(process.ExitCode, [.. lines]);
        }
        finally
        {
            if (!exited)
                Kill(process);
        }
    }

    /// <summary>Reads lines with a length limit, so a huge line cannot exhaust memory.</summary>
    private static async Task PumpAsync(StreamReader reader, Action<string> onLine)
    {
        var buffer = new char[4096];
        var line = new StringBuilder();
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer)) > 0)
            {
                for (int i = 0; i < read; i++)
                {
                    var c = buffer[i];
                    if (c is '\n' or '\r')
                    {
                        if (line.Length > 0) onLine(line.ToString());
                        line.Clear();
                    }
                    else if (line.Length < MaxLineLength)
                    {
                        line.Append(c);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The process was killed.
        }

        if (line.Length > 0) onLine(line.ToString());
    }

    private static void Kill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException) { }
    }

    /// <summary>
    /// Removes escape sequences, control and invisible format characters (the text is written
    /// to the terminal emulator, which would interpret them) and limits the length.
    /// </summary>
    internal static string Sanitize(string? text, int maxLength = MaxLineLength)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        var withoutSequences = EscapeSequence().Replace(text, "");
        var builder = new StringBuilder(Math.Min(withoutSequences.Length, maxLength + 1));
        foreach (var c in withoutSequences)
        {
            if (builder.Length >= maxLength)
            {
                builder.Append('…');
                break;
            }

            if (c == '\t')
                builder.Append(' ');
            else if (!char.IsControl(c) && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format)
                builder.Append(c);
        }
        return builder.ToString().Trim();
    }

    // CSI, OSC (terminated by BEL or ST) and two-character escape sequences.
    [GeneratedRegex(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\)?|[@-Z\\-_])")]
    private static partial Regex EscapeSequence();
}
