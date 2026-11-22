using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace T.Services;

/// <summary>
/// Checks whether other local users could change a file or directory - and with it what T
/// executes (step), trusts (CA root) or reads its configuration from (settings.json).
/// </summary>
internal static class FilePermissions
{
    private const UnixFileMode OthersWriteBits = UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;

    // Rights that change content or permissions; GENERIC_ALL / GENERIC_WRITE can appear in raw ACEs.
    [SupportedOSPlatform("windows")]
    private const FileSystemRights ModifyingRights =
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete
        | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership
        | (FileSystemRights)0x10000000 | (FileSystemRights)0x40000000;

    /// <summary>
    /// Windows: true when the owner or an allow entry that can modify, delete or re-permission the
    /// path belongs to someone other than the current user, SYSTEM, Administrators or TrustedInstaller.
    /// Unix: group or world writable. Unreadable permissions count as writable (fail closed).
    /// </summary>
    public static bool IsWritableByOthers(string path)
    {
        if (OperatingSystem.IsWindows())
            return IsWritableByOthersWindows(path);

        try { return (File.GetUnixFileMode(path) & OthersWriteBits) != 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    /// <summary>The file, its directory and - for a link (e.g. WinGet's "Links") - the final target and its directory.</summary>
    public static bool IsFileOrDirectoryWritableByOthers(string path)
    {
        var paths = new List<string> { path, Path.GetDirectoryName(path)! };
        try
        {
            if (File.ResolveLinkTarget(path, returnFinalTarget: true) is { } target)
                paths.AddRange([target.FullName, Path.GetDirectoryName(target.FullName)!]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
        return paths.Any(IsWritableByOthers);
    }

    /// <summary>Directory whose permissions only allow the current user (checked after T restricted it).</summary>
    [SupportedOSPlatform("windows")]
    public static bool IsOwnerOnly(DirectoryInfo directory)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = directory.GetAccessControl();
        if (!security.AreAccessRulesProtected || !IsOwnerTrusted(security, identity, includeSystemAccounts: false))
            return false;

        return security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .All(rule => rule.AccessControlType != AccessControlType.Allow || rule.IdentityReference.Equals(identity.User));
    }

    [SupportedOSPlatform("windows")]
    private static bool IsWritableByOthersWindows(string path)
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            FileSystemSecurity security = Directory.Exists(path)
                ? new DirectoryInfo(path).GetAccessControl()
                : new FileInfo(path).GetAccessControl();

            if (!IsOwnerTrusted(security, identity, includeSystemAccounts: true))
                return true;

            var trusted = TrustedSids(identity, includeSystemAccounts: true);
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow
                    || rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)
                    || (rule.FileSystemRights & ModifyingRights) == 0)
                {
                    continue;
                }

                if (rule.IdentityReference is not SecurityIdentifier sid || !trusted.Contains(sid))
                    return true;
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SystemException)
        {
            return true;
        }
    }

    /// <summary>The owner can always change the permissions, so it must be trusted as well.</summary>
    [SupportedOSPlatform("windows")]
    private static bool IsOwnerTrusted(FileSystemSecurity security, WindowsIdentity identity, bool includeSystemAccounts) =>
        security.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner
        && TrustedSids(identity, includeSystemAccounts).Contains(owner);

    /// <summary>
    /// The user and the token's default owner (Administrators when T runs elevated); with
    /// <paramref name="includeSystemAccounts"/> also SYSTEM, Administrators and TrustedInstaller.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static HashSet<SecurityIdentifier> TrustedSids(WindowsIdentity identity, bool includeSystemAccounts)
    {
        var sids = new HashSet<SecurityIdentifier>();
        if (identity.User != null) sids.Add(identity.User);
        if (identity.Owner != null) sids.Add(identity.Owner);
        if (!includeSystemAccounts)
            return sids;

        sids.Add(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        sids.Add(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        try
        {
            if (new NTAccount("NT SERVICE", "TrustedInstaller").Translate(typeof(SecurityIdentifier)) is SecurityIdentifier installer)
                sids.Add(installer);
        }
        catch (IdentityNotMappedException)
        {
        }
        return sids;
    }
}
