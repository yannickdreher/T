using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace T.Views.Dialogs;

public partial class AboutDialog : UserControl
{
    public AboutDialog()
    {
        InitializeComponent();
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