using Avalonia.Media;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace T.UI.Services;

public partial class AnsiParser
{
    private static readonly Dictionary<int, IBrush> ForegroundColors = new()
    {
        { 30, new SolidColorBrush(Color.Parse("#000000")) },
        { 31, new SolidColorBrush(Color.Parse("#CD3131")) },
        { 32, new SolidColorBrush(Color.Parse("#0DBC79")) },
        { 33, new SolidColorBrush(Color.Parse("#E5E510")) },
        { 34, new SolidColorBrush(Color.Parse("#2472C8")) },
        { 35, new SolidColorBrush(Color.Parse("#BC3FBC")) },
        { 36, new SolidColorBrush(Color.Parse("#11A8CD")) },
        { 37, new SolidColorBrush(Color.Parse("#E5E5E5")) },
        { 90, new SolidColorBrush(Color.Parse("#666666")) },
        { 91, new SolidColorBrush(Color.Parse("#F14C4C")) },
        { 92, new SolidColorBrush(Color.Parse("#23D18B")) },
        { 93, new SolidColorBrush(Color.Parse("#F5F543")) },
        { 94, new SolidColorBrush(Color.Parse("#3B8EEA")) },
        { 95, new SolidColorBrush(Color.Parse("#D670D6")) },
        { 96, new SolidColorBrush(Color.Parse("#29B8DB")) },
        { 97, new SolidColorBrush(Color.Parse("#FFFFFF")) },
    };

    private static readonly Dictionary<int, IBrush> BackgroundColors = new()
    {
        { 40, new SolidColorBrush(Color.Parse("#000000")) },
        { 41, new SolidColorBrush(Color.Parse("#CD3131")) },
        { 42, new SolidColorBrush(Color.Parse("#0DBC79")) },
        { 43, new SolidColorBrush(Color.Parse("#E5E510")) },
        { 44, new SolidColorBrush(Color.Parse("#2472C8")) },
        { 45, new SolidColorBrush(Color.Parse("#BC3FBC")) },
        { 46, new SolidColorBrush(Color.Parse("#11A8CD")) },
        { 47, new SolidColorBrush(Color.Parse("#E5E5E5")) },
        { 100, new SolidColorBrush(Color.Parse("#666666")) },
        { 101, new SolidColorBrush(Color.Parse("#F14C4C")) },
        { 102, new SolidColorBrush(Color.Parse("#23D18B")) },
        { 103, new SolidColorBrush(Color.Parse("#F5F543")) },
        { 104, new SolidColorBrush(Color.Parse("#3B8EEA")) },
        { 105, new SolidColorBrush(Color.Parse("#D670D6")) },
        { 106, new SolidColorBrush(Color.Parse("#29B8DB")) },
        { 107, new SolidColorBrush(Color.Parse("#FFFFFF")) },
    };

    // Alle ANSI/VT100 Escape-Sequenzen (umfassend)
    [GeneratedRegex(@"\x1B\[[0-9;]*m")]
    private static partial Regex AnsiColorRegex();
    
    // Alle anderen Escape-Sequenzen entfernen
    [GeneratedRegex(@"\x1B(?:\[[\x30-\x3F]*[\x20-\x2F]*[\x40-\x7E]|\][^\x07\x1B]*(?:\x07|\x1B\\)?|[\x40-\x5F]|\([A-Z0-9])")]
    private static partial Regex AllEscapeRegex();
    
    // OSC Sequenzen (Window Title etc.)
    [GeneratedRegex(@"\x1B\][^\x07]*\x07")]
    private static partial Regex OscRegex();
    
    // Bracketed Paste Mode und andere private Sequenzen
    [GeneratedRegex(@"\x1B\[\?[0-9;]*[a-zA-Z]")]
    private static partial Regex PrivateModeRegex();
    
    // CSI Sequenzen (Cursor etc.)
    [GeneratedRegex(@"\x1B\[[0-9;]*[A-HJKSTfhilmnprsu]")]
    private static partial Regex CsiRegex();

    public record TextSegment(string Text, IBrush? Foreground, IBrush? Background, bool IsBold, bool IsUnderline);

    public static string StripAllEscapeCodes(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        // Alle bekannten Escape-Sequenzen entfernen
        var result = OscRegex().Replace(input, string.Empty);
        result = PrivateModeRegex().Replace(result, string.Empty);
        result = CsiRegex().Replace(result, string.Empty);
        result = AnsiColorRegex().Replace(result, string.Empty);
        result = AllEscapeRegex().Replace(result, string.Empty);
        
        // Verbleibende ESC-Zeichen und Steuerzeichen entfernen
        var sb = new StringBuilder(result.Length);
        foreach (var c in result)
        {
            // Nur druckbare Zeichen, Tab, CR, LF behalten
            if (c >= 32 || c == '\t' || c == '\r' || c == '\n')
            {
                sb.Append(c);
            }
        }
        
        return sb.ToString();
    }

    public static List<TextSegment> Parse(string input, bool enableColors)
    {
        var segments = new List<TextSegment>();
        
        if (string.IsNullOrEmpty(input))
            return segments;

        // Erst alle nicht-Farb-Escape-Sequenzen entfernen
        input = OscRegex().Replace(input, string.Empty);
        input = PrivateModeRegex().Replace(input, string.Empty);
        input = CsiRegex().Replace(input, string.Empty);
        
        // Steuerzeichen entfernen (außer Tab, CR, LF)
        var cleaned = new StringBuilder(input.Length);
        bool lastWasEscape = false;
        foreach (var c in input)
        {
            if (c == '\x1B')
            {
                lastWasEscape = true;
                cleaned.Append(c);
            }
            else if (lastWasEscape && c == '[')
            {
                lastWasEscape = false;
                cleaned.Append(c);
            }
            else if (c >= 32 || c == '\t' || c == '\r' || c == '\n' || c == '\x1B')
            {
                lastWasEscape = false;
                cleaned.Append(c);
            }
        }
        input = cleaned.ToString();

        if (!enableColors)
        {
            input = AnsiColorRegex().Replace(input, string.Empty);
            if (!string.IsNullOrEmpty(input))
                segments.Add(new TextSegment(input, null, null, false, false));
            return segments;
        }

        // Farben parsen
        IBrush? currentFg = null;
        IBrush? currentBg = null;
        bool isBold = false;
        bool isUnderline = false;
        
        int lastIndex = 0;
        var matches = AnsiColorRegex().Matches(input);

        foreach (Match match in matches)
        {
            if (match.Index > lastIndex)
            {
                var text = input[lastIndex..match.Index];
                if (!string.IsNullOrEmpty(text))
                    segments.Add(new TextSegment(text, currentFg, currentBg, isBold, isUnderline));
            }

            // SGR Parameter parsen
            var paramStr = match.Value[2..^1]; // Zwischen \x1B[ und m
            if (string.IsNullOrEmpty(paramStr))
            {
                // \x1B[m = Reset
                currentFg = null;
                currentBg = null;
                isBold = false;
                isUnderline = false;
            }
            else
            {
                var codes = paramStr.Split(';');
                for (int i = 0; i < codes.Length; i++)
                {
                    if (!int.TryParse(codes[i], out int code))
                        continue;

                    switch (code)
                    {
                        case 0:
                            currentFg = null;
                            currentBg = null;
                            isBold = false;
                            isUnderline = false;
                            break;
                        case 1: isBold = true; break;
                        case 4: isUnderline = true; break;
                        case 22: isBold = false; break;
                        case 24: isUnderline = false; break;
                        case 39: currentFg = null; break;
                        case 49: currentBg = null; break;
                        case 38: // 256-color oder RGB Vordergrund
                            if (i + 1 < codes.Length && codes[i + 1] == "5" && i + 2 < codes.Length)
                            {
                                // 256-color: \x1B[38;5;{n}m
                                if (int.TryParse(codes[i + 2], out int colorIndex))
                                    currentFg = Get256Color(colorIndex);
                                i += 2;
                            }
                            break;
                        case 48: // 256-color oder RGB Hintergrund
                            if (i + 1 < codes.Length && codes[i + 1] == "5" && i + 2 < codes.Length)
                            {
                                if (int.TryParse(codes[i + 2], out int colorIndex))
                                    currentBg = Get256Color(colorIndex);
                                i += 2;
                            }
                            break;
                        default:
                            if (ForegroundColors.TryGetValue(code, out var fg))
                                currentFg = fg;
                            else if (BackgroundColors.TryGetValue(code, out var bg))
                                currentBg = bg;
                            break;
                    }
                }
            }

            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < input.Length)
        {
            var text = input[lastIndex..];
            if (!string.IsNullOrEmpty(text))
                segments.Add(new TextSegment(text, currentFg, currentBg, isBold, isUnderline));
        }

        return segments;
    }

    private static SolidColorBrush Get256Color(int index)
    {
        if (index < 16)
        {
            // Standard colors
            return index switch
            {
                0 => new SolidColorBrush(Color.Parse("#000000")),
                1 => new SolidColorBrush(Color.Parse("#CD3131")),
                2 => new SolidColorBrush(Color.Parse("#0DBC79")),
                3 => new SolidColorBrush(Color.Parse("#E5E510")),
                4 => new SolidColorBrush(Color.Parse("#2472C8")),
                5 => new SolidColorBrush(Color.Parse("#BC3FBC")),
                6 => new SolidColorBrush(Color.Parse("#11A8CD")),
                7 => new SolidColorBrush(Color.Parse("#E5E5E5")),
                8 => new SolidColorBrush(Color.Parse("#666666")),
                9 => new SolidColorBrush(Color.Parse("#F14C4C")),
                10 => new SolidColorBrush(Color.Parse("#23D18B")),
                11 => new SolidColorBrush(Color.Parse("#F5F543")),
                12 => new SolidColorBrush(Color.Parse("#3B8EEA")),
                13 => new SolidColorBrush(Color.Parse("#D670D6")),
                14 => new SolidColorBrush(Color.Parse("#29B8DB")),
                15 => new SolidColorBrush(Color.Parse("#FFFFFF")),
                _ => new SolidColorBrush(Colors.White)
            };
        }
        else if (index < 232)
        {
            // 216 colors (6x6x6 cube)
            int i = index - 16;
            int r = (i / 36) * 51;
            int g = ((i / 6) % 6) * 51;
            int b = (i % 6) * 51;
            return new SolidColorBrush(Color.FromRgb((byte)r, (byte)g, (byte)b));
        }
        else
        {
            // Grayscale
            int gray = (index - 232) * 10 + 8;
            return new SolidColorBrush(Color.FromRgb((byte)gray, (byte)gray, (byte)gray));
        }
    }
}