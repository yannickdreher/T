using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using T.Services;

namespace T.UI.Services;

/// <summary>
/// Windows caches the icon of T.exe for shortcuts, the Start menu and the taskbar. An update
/// replaces the file at the same path, so a changed application icon would keep showing the old
/// one. After the first start of a new version the shell is asked to read the icon again.
/// </summary>
internal static class ShellIconCache
{
    private const uint ShcneUpdateItem = 0x00002000;
    private const uint ShcneAssocChanged = 0x08000000;
    private const uint ShcnfIdList = 0x0000;
    private const uint ShcnfPathW = 0x0005;
    private const uint ShcnfFlush = 0x1000;

    /// <summary>
    /// Call when the main window is open. Not during the update itself: while the updater swaps
    /// the files the shell finds no icon and caches the blank default one.
    /// </summary>
    public static void RefreshAfterUpdate()
    {
        if (!OperatingSystem.IsWindows()) return;
        _ = Task.Run(() =>
        {
            try
            {
                var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "";
                var marker = Path.Combine(AppPaths.DataDirectory, "icon-version");
                if (File.Exists(marker) && File.ReadAllText(marker).Trim() == version) return;

                var startMenu = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                var pinned = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar");
                string?[] paths = [Environment.ProcessPath, Path.Combine(startMenu, "T.lnk"), Path.Combine(pinned, "T.lnk")];
                foreach (var path in paths)
                {
                    if (path != null && File.Exists(path))
                        SHChangeNotify(ShcneUpdateItem, ShcnfPathW, path, IntPtr.Zero);
                }
                SHChangeNotify(ShcneAssocChanged, ShcnfIdList | ShcnfFlush, null, IntPtr.Zero);

                Directory.CreateDirectory(AppPaths.DataDirectory);
                File.WriteAllText(marker, version);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException)
            {
                // Cosmetic only: never disturb the start of the app.
                Debug.WriteLine($"[ShellIconCache] Refresh failed: {ex.Message}");
            }
        });
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(uint eventId, uint flags, string? item1, IntPtr item2);
}
