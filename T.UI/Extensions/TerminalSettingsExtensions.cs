using Avalonia.Media;
using T.Models;

namespace T.UI.Extensions;

/// <summary>
/// Avalonia-specific extension methods for <see cref="TerminalSettings"/>.
/// These live in T.UI because T.Models has no dependency on Avalonia.Media.
/// Invalid values (e.g. a hand-edited settings.json) fall back to the defaults
/// instead of throwing during rendering.
/// </summary>
public static class TerminalSettingsExtensions
{
    public static Color GetBackgroundColor(this TerminalSettings s) => ParseOr(s.TerminalBackground, Colors.Black);
    public static Color GetForegroundColor(this TerminalSettings s) => ParseOr(s.TerminalForeground, Color.FromRgb(204, 204, 204));
    public static Color GetCursorColor(this TerminalSettings s) => ParseOr(s.CursorColor, Colors.Lime);

    public static FontFamily GetFont(this TerminalSettings s) =>
        new(string.IsNullOrWhiteSpace(s.TerminalFontFamily)
            ? "Cascadia Mono, Consolas, Courier New, monospace"
            : $"{s.TerminalFontFamily}, Cascadia Mono, Consolas, Courier New, monospace");

    private static Color ParseOr(string? value, Color fallback) =>
        Color.TryParse(value, out var color) ? color : fallback;
}
