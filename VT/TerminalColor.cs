namespace VT;

/// <summary>
/// Represents a terminal color (16 ANSI, 256 palette, or 24-bit RGB)
/// </summary>
public readonly struct TerminalColor : IEquatable<TerminalColor>
{
    public static readonly TerminalColor Default = new(-1);
    public static readonly TerminalColor Black = new(0);
    public static readonly TerminalColor Red = new(1);
    public static readonly TerminalColor Green = new(2);
    public static readonly TerminalColor Yellow = new(3);
    public static readonly TerminalColor Blue = new(4);
    public static readonly TerminalColor Magenta = new(5);
    public static readonly TerminalColor Cyan = new(6);
    public static readonly TerminalColor White = new(7);
    public static readonly TerminalColor BrightBlack = new(8);
    public static readonly TerminalColor BrightRed = new(9);
    public static readonly TerminalColor BrightGreen = new(10);
    public static readonly TerminalColor BrightYellow = new(11);
    public static readonly TerminalColor BrightBlue = new(12);
    public static readonly TerminalColor BrightMagenta = new(13);
    public static readonly TerminalColor BrightCyan = new(14);
    public static readonly TerminalColor BrightWhite = new(15);

    private readonly int _value;

    public TerminalColor(int paletteIndex) => _value = paletteIndex;
    
    public static TerminalColor FromRgb(byte r, byte g, byte b) => 
        new(0x1000000 | (r << 16) | (g << 8) | b);
    
    public static TerminalColor FromPalette256(int index) => new(index);

    public bool IsDefault => _value < 0;
    public bool IsPalette => _value >= 0 && _value < 256;
    public bool IsRgb => _value >= 0x1000000;
    
    public int PaletteIndex => IsPalette ? _value : -1;
    
    public (byte R, byte G, byte B) ToRgb()
    {
        if (IsRgb)
        {
            return ((byte)((_value >> 16) & 0xFF), (byte)((_value >> 8) & 0xFF), (byte)(_value & 0xFF));
        }
        if (IsPalette)
        {
            return GetPaletteRgb(_value);
        }
        return (204, 204, 204); // Default gray
    }

    private static (byte R, byte G, byte B) GetPaletteRgb(int index)
    {
        // Standard 16 ANSI colors
        ReadOnlySpan<(byte, byte, byte)> ansi16 =
        [
            (0, 0, 0), (205, 49, 49), (13, 188, 121), (229, 229, 16),
            (36, 114, 200), (188, 63, 188), (17, 168, 205), (229, 229, 229),
            (102, 102, 102), (241, 76, 76), (35, 209, 139), (245, 245, 67),
            (59, 142, 234), (214, 112, 214), (41, 184, 219), (255, 255, 255)
        ];

        if (index < 16) return ansi16[index];
        
        if (index < 232)
        {
            // 216 color cube (6x6x6)
            int idx = index - 16;
            int r = (idx / 36) * 51;
            int g = ((idx / 6) % 6) * 51;
            int b = (idx % 6) * 51;
            return ((byte)r, (byte)g, (byte)b);
        }
        
        // Grayscale (24 shades)
        int gray = (index - 232) * 10 + 8;
        return ((byte)gray, (byte)gray, (byte)gray);
    }

    public bool Equals(TerminalColor other) => _value == other._value;
    public override bool Equals(object? obj) => obj is TerminalColor c && Equals(c);
    public override int GetHashCode() => _value;
    public static bool operator ==(TerminalColor left, TerminalColor right) => left.Equals(right);
    public static bool operator !=(TerminalColor left, TerminalColor right) => !left.Equals(right);
}