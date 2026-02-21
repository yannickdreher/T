namespace T.VT;

/// <summary>
/// Represents a single character cell in the terminal buffer
/// </summary>
public struct TerminalCharacter
{
    public char Char;
    public TerminalColor Foreground;
    public TerminalColor Background;
    public TerminalAttribute Attributes;

    public static readonly TerminalCharacter Blank = new()
    {
        Char = ' ',
        Foreground = TerminalColor.White,
        Background = TerminalColor.Default,
        Attributes = TerminalAttribute.None
    };

    public readonly bool IsBold => Attributes.HasFlag(TerminalAttribute.Bold);
    public readonly bool IsUnderline => Attributes.HasFlag(TerminalAttribute.Underline);
    public readonly bool IsInverse => Attributes.HasFlag(TerminalAttribute.Inverse);
    public readonly bool IsBlink => Attributes.HasFlag(TerminalAttribute.Blink);
    public readonly bool IsHidden => Attributes.HasFlag(TerminalAttribute.Hidden);
}