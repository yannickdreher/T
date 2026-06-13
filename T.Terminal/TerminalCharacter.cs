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
        Foreground = TerminalColor.Default,
        Background = TerminalColor.Default,
        Attributes = TerminalAttribute.None
    };

    public readonly bool IsBold => (Attributes & TerminalAttribute.Bold) != 0;
    public readonly bool IsDim => (Attributes & TerminalAttribute.Dim) != 0;
    public readonly bool IsItalic => (Attributes & TerminalAttribute.Italic) != 0;
    public readonly bool IsUnderline => (Attributes & TerminalAttribute.Underline) != 0;
    public readonly bool IsInverse => (Attributes & TerminalAttribute.Inverse) != 0;
    public readonly bool IsBlink => (Attributes & TerminalAttribute.Blink) != 0;
    public readonly bool IsHidden => (Attributes & TerminalAttribute.Hidden) != 0;
    public readonly bool IsStrikethrough => (Attributes & TerminalAttribute.Strikethrough) != 0;
}