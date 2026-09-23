using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;

namespace T.UI.Services;

public static class DialogService
{
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
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 },
            PrimaryButtonText = primaryText,
            CloseButtonText = closeText,
            DefaultButton = FAContentDialogButton.Close
        };

        var result = await dialog.ShowAsync(host);
        return result == FAContentDialogResult.Primary;
    }

    /// <summary>Asks a question with up to three answers (primary, secondary, close/cancel).</summary>
    public static async Task<FAContentDialogResult> ChooseAsync(
        Window host,
        string title,
        string message,
        string primaryText,
        string secondaryText,
        string closeText = "Cancel")
    {
        var dialog = new FAContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 },
            PrimaryButtonText = primaryText,
            SecondaryButtonText = secondaryText,
            CloseButtonText = closeText,
            DefaultButton = FAContentDialogButton.Primary
        };
        return await dialog.ShowAsync(host);
    }

    /// <summary>Shows an error/information message with a single close button.</summary>
    public static async Task ShowMessageAsync(Window host, string title, string message)
    {
        var dialog = new FAContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 },
            CloseButtonText = "OK",
            DefaultButton = FAContentDialogButton.Close
        };
        await dialog.ShowAsync(host);
    }

    /// <summary>
    /// Asks for a single line of text. Returns <see langword="null"/> when canceled.
    /// With <paramref name="isSecret"/> the input is masked (passwords, 2FA codes).
    /// </summary>
    public static async Task<string?> PromptAsync(
        Window? host,
        string title,
        string message,
        string initialValue = "",
        string placeholder = "",
        bool isSecret = false,
        bool trim = true,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return null;

        var textBox = new TextBox
        {
            Text = initialValue,
            PlaceholderText = placeholder,
            Width = 380,
            PasswordChar = isSecret ? '●' : default
        };

        var panel = new StackPanel { Spacing = 8 };
        if (!string.IsNullOrWhiteSpace(message))
            panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(textBox);

        var dialog = new FAContentDialog
        {
            Title = title,
            Content = panel,
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            DefaultButton = FAContentDialogButton.Primary,
            MinWidth = 0
        };

        textBox.AttachedToVisualTree += (_, _) =>
        {
            textBox.Focus();
            textBox.SelectAll();
        };

        // The caller may withdraw the question (e.g. the SSH attempt timed out meanwhile).
        using var registration = cancellationToken.Register(() => Dispatcher.UIThread.Post(() => dialog.Hide()));

        var result = host is null ? await dialog.ShowAsync() : await dialog.ShowAsync(host);
        if (result != FAContentDialogResult.Primary || cancellationToken.IsCancellationRequested)
            return null;

        var text = textBox.Text ?? "";
        return trim ? text.Trim() : text;
    }
}
