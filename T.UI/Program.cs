using System;
using System.Runtime.InteropServices;
using Avalonia;
using Velopack;

namespace T.UI;

internal sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var velopack = VelopackApp.Build().OnRestarted(_ => RefreshShellIcons());

        // The updater calls this right after the new version is in place (Windows only).
        if (OperatingSystem.IsWindows())
            velopack.OnAfterUpdateFastCallback(_ => RefreshShellIcons());
        velopack.Run();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    /// <summary>
    /// Windows caches the icon of T.exe for shortcuts, the Start menu and the taskbar. An update
    /// replaces the file at the same path, so a changed application icon would keep showing the
    /// old one; this tells the shell to read the icons again.
    /// </summary>
    private static void RefreshShellIcons()
    {
        if (!OperatingSystem.IsWindows()) return;
        try { SHChangeNotify(ShcneAssocChanged, ShcnfIdList, IntPtr.Zero, IntPtr.Zero); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Cosmetic only: never block an update or the start of the app.
        }
    }

    private const uint ShcneAssocChanged = 0x08000000;
    private const uint ShcnfIdList = 0x0000;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
}
