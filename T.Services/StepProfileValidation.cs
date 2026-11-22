using System.Globalization;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using T.Models;

namespace T.Services;

/// <summary>
/// Allow-list validation of everything that ends up on the step command line. It runs in the
/// settings dialog and again right before step is started (settings.json may be edited by hand).
/// Values never start with '-' so they cannot be mistaken for options, even without the "--"
/// separator the command line uses as well.
/// </summary>
public static partial class StepProfileValidation
{
    private const int MaxNameLength = 64;
    private const int MaxIdentityLength = 256;
    private const int MaxUrlLength = 2048;
    private const int MaxProvisionerLength = 128;
    private const int MaxPrincipals = 32;

    public static string? Validate(StepCaProfile profile)
    {
        if (!TryParseId(profile.Id, out _))
            return "The profile has an invalid id.";

        return ValidateName(profile.Name)
            ?? ValidateIdentity(profile.Identity)
            ?? ValidateCaUrl(profile.CaUrl)
            ?? ValidateRootCertificatePath(profile.RootCertificatePath)
            ?? ValidateContext(profile.Context)
            ?? ValidateProvisioner(profile.Provisioner)
            ?? ValidatePrincipals(profile.Principals);
    }

    public static string? ValidateName(string? value)
    {
        var name = value?.Trim() ?? "";
        if (name.Length == 0)
            return "Please enter a name.";
        if (name.Length > MaxNameLength || HasUnsafeCharacters(name))
            return $"The name must be at most {MaxNameLength} printable characters.";
        return null;
    }

    public static string? ValidateIdentity(string? value)
    {
        var identity = value?.Trim() ?? "";
        if (identity.Length == 0)
            return "Please enter the certificate identity (e.g. your e-mail address).";
        if (identity.Length > MaxIdentityLength || identity[0] == '-' || !IdentityPattern().IsMatch(identity))
            return "The identity may contain letters, digits and . _ % + @ = : ~ - (no spaces, not starting with '-').";
        return null;
    }

    public static string? ValidateCaUrl(string? value)
    {
        var url = value?.Trim() ?? "";
        if (url.Length == 0)
            return null;

        if (url.Length > MaxUrlLength || HasUnsafeCharacters(url) || url.Any(char.IsWhiteSpace)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return "The CA URL is not a valid URL.";
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
            return "The CA URL must use https.";
        if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            return "The CA URL must not contain credentials, a query or a fragment.";
        return null;
    }

    public static string? ValidateRootCertificatePath(string? value)
    {
        var path = value?.Trim() ?? "";
        if (path.Length == 0)
            return null;
        if (HasUnsafeCharacters(path) || !Path.IsPathFullyQualified(path))
            return "The root certificate must be an absolute path.";
        // Checked before File.Exists: probing \\server\share already authenticates to that server.
        if (!IsLocalPath(path))
            return "The root certificate must be a file on a local drive (no network or device path).";
        if (!File.Exists(path))
            return "The root certificate file does not exist.";
        // Whoever can replace the trust anchor can impersonate the CA (and receive the login token).
        if (FilePermissions.IsFileOrDirectoryWritableByOthers(path))
            return "The root certificate or its folder can be changed by other users.";
        return null;
    }

    public static string? ValidateContext(string? value)
    {
        var context = value?.Trim() ?? "";
        if (context.Length == 0)
            return null;
        if (context[0] == '-' || !ContextPattern().IsMatch(context))
            return "The context may contain letters, digits and . _ - (not starting with '-').";
        return null;
    }

    public static string? ValidateProvisioner(string? value)
    {
        var provisioner = value?.Trim() ?? "";
        if (provisioner.Length == 0)
            return null;
        if (provisioner.Length > MaxProvisionerLength || provisioner[0] == '-' || !ProvisionerPattern().IsMatch(provisioner))
            return "The provisioner may contain letters, digits, spaces and . _ @ + : / = - (not starting with '-').";
        return null;
    }

    public static string? ValidatePrincipals(string? value)
    {
        var principals = SplitPrincipals(value);
        if (principals.Count > MaxPrincipals)
            return $"At most {MaxPrincipals} principals are allowed.";
        foreach (var principal in principals)
        {
            // The rejected value is shown in the terminal, so it must not carry escape sequences.
            if (principal[0] == '-' || !PrincipalPattern().IsMatch(principal))
                return $"Invalid principal '{StepCli.Sanitize(principal, 64)}': use letters, digits and . _ @ - (not starting with '-').";
        }
        return null;
    }

    public static string? ValidateExecutablePath(string? value)
    {
        var path = value?.Trim() ?? "";
        if (path.Length == 0)
            return null;
        if (HasUnsafeCharacters(path) || !Path.IsPathFullyQualified(path))
            return "The step path must be empty (search PATH) or an absolute path.";

        if (!IsLocalPath(path))
            return "The step executable must be a file on a local drive (no network or device path).";

        // Batch files are re-parsed by cmd.exe, which breaks the argument quoting.
        if (OperatingSystem.IsWindows() && !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
            return "The step path must point to step.exe.";
        if (!File.Exists(path))
            return "The step executable does not exist.";
        if (!OperatingSystem.IsWindows() && !IsExecutable(path))
            return "The step file is not executable.";
        // T runs it automatically: nobody else may be able to replace it.
        if (FilePermissions.IsFileOrDirectoryWritableByOthers(path))
            return "The step executable or its folder can be changed by other users.";
        return null;
    }

    /// <summary>
    /// Parses a profile id. Only the canonical form (lower case "D" GUID) is accepted, so two
    /// spellings of one GUID can never share a store file or lock.
    /// </summary>
    public static bool TryParseId(string? value, out Guid id) =>
        Guid.TryParseExact(value, "D", out id) && string.Equals(value, id.ToString("D"), StringComparison.Ordinal);

    public static List<string> SplitPrincipals(string? value) =>
        [.. (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>
    /// Windows: a path on a local drive ("X:\" of a fixed, removable or RAM disk). Rejects UNC and
    /// device paths in every spelling (\\, //, /\, \??\), alternate data streams and mapped network
    /// drives, because even probing them authenticates to the server. Must run before the path is used.
    /// </summary>
    internal static bool IsLocalPath(string path)
    {
        if (!OperatingSystem.IsWindows())
            return true;

        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }

        if (full.Length < 3 || !char.IsAsciiLetter(full[0]) || full[1] != ':' || full[2] != '\\' || full.IndexOf(':', 2) >= 0)
            return false;

        try { return new DriveInfo(full[..3]).DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Ram; }
        catch (ArgumentException) { return false; }
    }

    [UnsupportedOSPlatform("windows")]
    private static bool IsExecutable(string path)
    {
        try { return (File.GetUnixFileMode(path) & ExecuteBits) != 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private const UnixFileMode ExecuteBits = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    /// <summary>Control characters and invisible format characters (e.g. bidi overrides).</summary>
    internal static bool HasUnsafeCharacters(string value) =>
        value.Any(c => char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format);

    [GeneratedRegex(@"^[\p{L}\p{N}._%+@=:~-]+\z")]
    private static partial Regex IdentityPattern();

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,64}\z")]
    private static partial Regex ContextPattern();

    [GeneratedRegex(@"^[\p{L}\p{N} ._@+:/=-]+\z")]
    private static partial Regex ProvisionerPattern();

    [GeneratedRegex(@"^[A-Za-z0-9._@-]{1,64}\z")]
    private static partial Regex PrincipalPattern();
}
