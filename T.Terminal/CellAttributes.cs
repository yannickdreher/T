namespace T.VT;

/// <summary>
/// Character attributes (bold, underline, etc.) and cell layout flags.
/// </summary>
[Flags]
public enum CellAttributes
{
    None = 0,
    Bold = 1 << 0,
    Dim = 1 << 1,
    Italic = 1 << 2,
    Underline = 1 << 3,
    Blink = 1 << 4,
    Inverse = 1 << 5,
    Hidden = 1 << 6,
    Strikethrough = 1 << 7,
    DoubleUnderline = 1 << 8,

    // Layout flags - set per cell by the emulator, never part of the SGR state.
    Wide = 1 << 9,
    WideContinuation = 1 << 10,
}
