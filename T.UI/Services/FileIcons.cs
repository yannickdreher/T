using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;

namespace T.UI.Services;

/// <summary>
/// Colored icons and type names for remote files, looked up by name. On Windows these are
/// the shell's icons (exactly what Windows Explorer shows); elsewhere colored vector icons.
/// Results are cached per extension and size. UI thread only.
/// </summary>
public static class FileIcons
{
    private const string FolderKey = "/";

    private static readonly Dictionary<(string Key, bool Link, int Pixels), IImage?> Icons = [];
    private static readonly Dictionary<string, (bool Found, int Index, string TypeName)> ShellInfo = [];

    /// <summary>
    /// Icon for a file or folder shown at <paramref name="size"/> device independent pixels.
    /// <paramref name="fallbackIconKey"/> is the vector icon used where the shell is not available.
    /// </summary>
    public static IImage? Get(string name, bool isDirectory, bool isLink, double size, string? fallbackIconKey = null)
    {
        var key = isDirectory ? FolderKey : Path.GetExtension(name).ToLowerInvariant();
        int pixels = (int)Math.Ceiling(size * RenderScaling);
        if (Icons.TryGetValue((key, isLink, pixels), out var cached))
            return cached;

        IImage? icon = null;
        if (OperatingSystem.IsWindows())
        {
            var info = QueryShell(key);
            if (info.Found)
                icon = ShellIcons.GetIcon(info.Index, pixels, isLink);
        }
        icon ??= CreateVectorIcon(isDirectory, fallbackIconKey);

        Icons[(key, isLink, pixels)] = icon;
        return icon;
    }

    public static IImage? GetFolder(double size) => Get("", isDirectory: true, isLink: false, size);

    /// <summary>Type column text: "File folder", "Text Document", ... (localized by Windows).</summary>
    public static string GetTypeName(string name, bool isDirectory)
    {
        var key = isDirectory ? FolderKey : Path.GetExtension(name).ToLowerInvariant();
        if (OperatingSystem.IsWindows())
        {
            var info = QueryShell(key);
            if (info.Found && info.TypeName.Length > 0)
                return info.TypeName;
        }

        if (isDirectory) return "File folder";
        return key.Length > 1 ? $"{key[1..].ToUpperInvariant()} File" : "File";
    }

    private static (bool Found, int Index, string TypeName) QueryShell(string key)
    {
        if (ShellInfo.TryGetValue(key, out var info))
            return info;

        if (OperatingSystem.IsWindows())
        {
            // The shell only looks at the name: a made-up file with the extension is enough.
            var found = ShellIcons.TryQuery(key == FolderKey ? "folder" : "file" + key, key == FolderKey, out var index, out var typeName);
            info = (found, index, typeName);
        }
        ShellInfo[key] = info;
        return info;
    }

    private static double RenderScaling =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window }
            ? Math.Max(1, window.RenderScaling)
            : 1;

    // ── Vector fallback (Linux, macOS) ───────────────────────────────────

    private static DrawingImage? CreateVectorIcon(bool isDirectory, string? iconKey)
    {
        var geometryKey = isDirectory ? "folder_filled" : iconKey ?? "document_regular";
        if (Application.Current is not { } app ||
            !app.TryGetResource(geometryKey, app.ActualThemeVariant, out var resource) || resource is not Geometry geometry)
            return null;

        return new DrawingImage(new GeometryDrawing
        {
            Geometry = geometry,
            Brush = new SolidColorBrush(isDirectory ? Color.FromRgb(0xE8, 0xB3, 0x39) : ColorFor(geometryKey))
        });
    }

    private static Color ColorFor(string iconKey) => iconKey switch
    {
        "document_image_regular" => Color.FromRgb(0x88, 0x6C, 0xE4),
        "document_pdf_regular" => Color.FromRgb(0xD1, 0x34, 0x38),
        "document_word_regular" => Color.FromRgb(0x2B, 0x57, 0x9A),
        "document_excel_regular" or "document_csv_regular" => Color.FromRgb(0x21, 0x73, 0x46),
        "document_powerpoint_regular" => Color.FromRgb(0xD2, 0x47, 0x26),
        "folder_zip_regular" => Color.FromRgb(0xC8, 0x92, 0x1A),
        "document_key_regular" => Color.FromRgb(0x00, 0x99, 0xBC),
        "document_link_regular" => Color.FromRgb(0x00, 0x78, 0xD4),
        "document_text_regular" or "document_regular" => Color.FromRgb(0x8A, 0x88, 0x86),
        _ => Color.FromRgb(0x3A, 0x96, 0xDD), // source code
    };
}
