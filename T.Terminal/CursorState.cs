namespace T.VT;

/// <summary>
/// Saved cursor state for DECSC/DECRC
/// </summary>
public class CursorState
{
    public int Column { get; set; }
    public int Row { get; set; }
    public TerminalColor Foreground { get; set; } = TerminalColor.White;
    public TerminalColor Background { get; set; } = TerminalColor.Default;
    public TerminalAttribute Attributes { get; set; }
    public bool OriginMode { get; set; }
    public bool AutoWrap { get; set; } = true;
}