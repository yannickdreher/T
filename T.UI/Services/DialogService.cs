using System.Threading.Tasks;
using Avalonia.Controls;
using FluentAvalonia.UI.Controls;

namespace T.UI.Services;

public static class DialogService
{
    /// <summary>
    /// Shows a FAContentDialog on the given host window.
    /// Returns the FAContentDialogResult (Primary, Secondary, or None for Close).
    /// </summary>
    public static async Task<FAContentDialogResult> ShowAsync(
        FAContentDialog dialog, 
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
        var dialog = new FAContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = primaryText,
            CloseButtonText = closeText,
            DefaultButton = FAContentDialogButton.Close
        };

        var result = await dialog.ShowAsync(host);
        return result == FAContentDialogResult.Primary;
    }
}