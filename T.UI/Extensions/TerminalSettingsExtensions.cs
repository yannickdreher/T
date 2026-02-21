using Avalonia.Media;
using T.Models;

namespace T.UI.Extensions;

/// <summary>
/// Avalonia-specific extension methods for <see cref="TerminalSettings"/>.
/// These live in T because T.Models has no dependency on Avalonia.Media.
/// </summary>
public static class TerminalSettingsExtensions
{
    public static Color GetBackgroundColor(this TerminalSettings s) => Color.Parse(s.TerminalBackground);
    public static Color GetForegroundColor(this TerminalSettings s) => Color.Parse(s.TerminalForeground);
    public static Color GetCursorColor(this TerminalSettings s) => Color.Parse(s.CursorColor);
    public static FontFamily GetFont(this TerminalSettings s) => new(s.TerminalFontFamily);
}