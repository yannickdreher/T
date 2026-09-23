namespace T.Models;

/// <summary>Helpers for POSIX paths on the server ("/" separated, independent of the local OS).</summary>
public static class RemotePath
{
    /// <summary>"/a/b" → "/a", "/a" → "/", "/" → "/".</summary>
    public static string GetParent(string path)
    {
        var trimmed = Normalize(path);
        var index = trimmed.LastIndexOf('/');
        return index <= 0 ? "/" : trimmed[..index];
    }

    /// <summary>Last segment: "/a/b.txt" → "b.txt".</summary>
    public static string GetName(string path)
    {
        var trimmed = Normalize(path);
        return trimmed == "/" ? "/" : trimmed[(trimmed.LastIndexOf('/') + 1)..];
    }

    public static string Combine(string directory, string name) => $"{directory.TrimEnd('/')}/{name}";

    /// <summary>Removes trailing slashes (the root stays "/").</summary>
    public static string Normalize(string path)
    {
        var trimmed = path.TrimEnd('/');
        return trimmed.Length == 0 ? "/" : trimmed;
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or lies somewhere inside it.</summary>
    public static bool IsSameOrInside(string path, string folder)
    {
        path = Normalize(path);
        folder = Normalize(folder);
        return path == folder || folder == "/" || path.StartsWith(folder + "/", StringComparison.Ordinal);
    }

    /// <summary>
    /// Name for a copy in the same folder, like Windows Explorer: "report - Copy.pdf",
    /// "report - Copy (2).pdf", ... Folders and dot files (".bashrc") keep their whole name as the base.
    /// </summary>
    public static string UniqueCopyName(string name, bool isDirectory, ICollection<string> existingNames)
    {
        int dot = isDirectory ? -1 : name.LastIndexOf('.');
        var (stem, extension) = dot > 0 ? (name[..dot], name[dot..]) : (name, "");

        var candidate = $"{stem} - Copy{extension}";
        for (int i = 2; existingNames.Contains(candidate); i++)
            candidate = $"{stem} - Copy ({i}){extension}";
        return candidate;
    }
}
