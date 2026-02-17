using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace T.Views.Dialogs;

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
        catch
        {
            // Fallback für Linux
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Process.Start("xdg-open", url);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", url);
            }
        }
    }
}