namespace T.VT;

/// <summary>
/// XTerm mouse tracking modes (DECSET 9/1000/1002/1003)
/// </summary>
public enum MouseTrackingMode
{
    None = 0,
    /// <summary>DECSET 9: report button press only.</summary>
    X10,
    /// <summary>DECSET 1000: report button press and release.</summary>
    Normal,
    /// <summary>DECSET 1002: like Normal plus motion while a button is held.</summary>
    ButtonEvent,
    /// <summary>DECSET 1003: report all motion events.</summary>
    AnyEvent
}

/// <summary>
/// Encodes mouse events into VT escape sequences (X10 and SGR 1006 formats)
/// </summary>
public static class MouseEncoder
{
    public const int ButtonLeft = 0;
    public const int ButtonMiddle = 1;
    public const int ButtonRight = 2;
    public const int ButtonRelease = 3;
    public const int WheelUp = 64;
    public const int WheelDown = 65;
    public const int MotionFlag = 32;

    public const int ModShift = 4;
    public const int ModAlt = 8;
    public const int ModCtrl = 16;

    /// <summary>
    /// Encodes a mouse event. Column/row are 0-based; output is 1-based.
    /// </summary>
    public static string Encode(int button, int column, int row, bool isRelease, bool sgrMode)
    {
        if (sgrMode)
        {
            char final = isRelease ? 'm' : 'M';
            return $"\x1b[<{button};{column + 1};{row + 1}{final}";
        }

        // Legacy X10 encoding: limited to 223 columns/rows
        int cb = (isRelease ? ButtonRelease : button) + 32;
        int cx = Math.Min(column + 1, 222) + 32;
        int cy = Math.Min(row + 1, 222) + 32;
        return $"\x1b[M{(char)cb}{(char)cx}{(char)cy}";
    }
}
