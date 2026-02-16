using System.Threading.Tasks;
using Avalonia.Controls;
using FluentAvalonia.UI.Controls;

namespace T.Services;

public static class DialogService
{
    /// <summary>
    /// Shows a ContentDialog on the given host window.
    /// Returns the ContentDialogResult (Primary, Secondary, or None for Close).
    /// </summary>
    public static async Task<ContentDialogResult> ShowAsync(
        ContentDialog dialog, 
        Window host)
    {
        return await dialog.ShowAsync(host);
    }

    /// <summary>
    /// Shows a simple confirmation dialog. Returns true if Primary was clicked.
    /// </summary>
    public static async Task<bool> ConfirmAsync(
        Window host,
        string title,
        string message,
        string primaryText = "Delete",
        string closeText = "Cancel")
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = primaryText,
            CloseButtonText = closeText,
            DefaultButton = ContentDialogButton.Close
        };

        var result = await dialog.ShowAsync(host);
        return result == ContentDialogResult.Primary;
    }
}