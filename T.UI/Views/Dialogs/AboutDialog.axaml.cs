using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace T.UI.Views.Dialogs;

public partial class AboutDialog : UserControl
{
    public string Version { get; }

    public AboutDialog()
    {
        InitializeComponent();
        Version = GetAssemblyVersion();
        DataContext = this;
    }

    private static string GetAssemblyVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version;
        return version != null ? $"Version {version.Major}.{version.Minor}.{version.Build}" : "Version Unknown";
    }

    private void OnViewLicenseClick(object? sender, RoutedEventArgs e)
    {
        // GPL-Lizenz im Browser öffnen
        OpenUrl("https://www.gnu.org/licenses/gpl-3.0.txt");
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception) when (!OperatingSystem.IsWindows())
        {
            // Fallback for Linux/macOS; a click handler must never crash the app.
            try
            {
                var opener = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "open" : "xdg-open";
                var psi = new ProcessStartInfo(opener) { UseShellExecute = false };
                psi.ArgumentList.Add(url);
                Process.Start(psi);
            }
            catch (Exception)
            {
                // No browser available - nothing else we can do.
            }
        }
        catch (Exception)
        {
            // No browser available - nothing else we can do.
        }
    }
}
