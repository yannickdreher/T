namespace T.VT;

/// <summary>
/// Keyboard translation record for a single key
/// </summary>
public record KeyboardTranslation(
    string? Normal = null,
    string? Shift = null,
    string? Control = null,
    bool ShiftOnApplication = false
);

/// <summary>
/// Translates keyboard input to VT100/XTerm escape sequences
/// Based on VtNetCore by Darren Starr
/// </summary>
public static class KeyboardTranslations
{
    private static readonly Dictionary<string, KeyboardTranslation> KeyTranslations = new()
    {
        // Function keys
        // | Key | Normal    | Shift     | Control   |
        // |-----|-----------|-----------|-----------|
        // | F1  | CSI 11~   | CSI 23~   | CSI 11~   |
        // | F2  | CSI 12~   | CSI 24~   | CSI 12~   |
        // ...
        { "F1", new("\x1b[11~", "\x1b[23~", "\x1b[11~") },
        { "F2", new("\x1b[12~", "\x1b[24~", "\x1b[12~") },
        { "F3", new("\x1b[13~", "\x1b[25~", "\x1b[13~") },
        { "F4", new("\x1b[14~", "\x1b[26~", "\x1b[14~") },
        { "F5", new("\x1b[15~", "\x1b[28~", "\x1b[15~") },
        { "F6", new("\x1b[17~", "\x1b[29~", "\x1b[17~") },
        { "F7", new("\x1b[18~", "\x1b[31~", "\x1b[18~") },
        { "F8", new("\x1b[19~", "\x1b[32~", "\x1b[19~") },
        { "F9", new("\x1b[20~", "\x1b[33~", "\x1b[20~") },
        { "F10", new("\x1b[21~", "\x1b[34~", "\x1b[21~") },
        { "F11", new("\x1b[23~", "\x1b[23~", "\x1b[23~") },
        { "F12", new("\x1b[24~", "\x1b[24~", "\x1b[24~") },
        
        // Arrow keys
        // | Key   | Normal | Shift   | Control | Application |
        // |-------|--------|---------|---------|-------------|
        // | Up    | CSI A  | Esc OA  | Esc OA  | Esc OA      |
        // | Down  | CSI B  | Esc OB  | Esc OB  | Esc OB      |
        // | Right | CSI C  | Esc OC  | Esc OC  | Esc OC      |
        // | Left  | CSI D  | Esc OD  | Esc OD  | Esc OD      |
        { "Up", new("\x1b[A", "\x1bOA", "\x1bOA", ShiftOnApplication: true) },
        { "Down", new("\x1b[B", "\x1bOB", "\x1bOB", ShiftOnApplication: true) },
        { "Right", new("\x1b[C", "\x1bOC", "\x1bOC", ShiftOnApplication: true) },
        { "Left", new("\x1b[D", "\x1bOD", "\x1bOD", ShiftOnApplication: true) },
        
        // Navigation keys
        { "Home", new("\x1b[1~", "\x1b[1~") },
        { "Insert", new("\x1b[2~") },
        { "Delete", new("\x1b[3~", "\x1b[3~") },
        { "End", new("\x1b[4~", "\x1b[4~") },
        { "PageUp", new("\x1b[5~", "\x1b[5~") },
        { "PageDown", new("\x1b[6~", "\x1b[6~") },
        
        // Special keys
        { "Back", new("\x7f", "\b", "\x7f") },
        { "Tab", new("\t", "\x1b[Z") },
        { "Enter", new("\r", "\r", "\r") },
        { "Return", new("\r", "\r", "\r") },
        { "Escape", new("\x1b\x1b", "\x1b\x1b", "\x1b\x1b") },
        { "Space", new(" ", " ", "\x00") },
        
        // Ctrl+Letter (A-Z)
        { "A", new(Control: "\x01") },
        { "B", new(Control: "\x02") },
        { "C", new(Control: "\x03") },
        { "D", new(Control: "\x04") },
        { "E", new(Control: "\x05") },
        { "F", new(Control: "\x06") },
        { "G", new(Control: "\x07") },
        { "H", new(Control: "\x08") },
        { "I", new(Control: "\x09") },
        { "J", new(Control: "\x0a") },
        { "K", new(Control: "\x0b") },
        { "L", new(Control: "\x0c") },
        { "M", new(Control: "\x0d") },
        { "N", new(Control: "\x0e") },
        { "O", new(Control: "\x0f") },
        { "P", new(Control: "\x10") },
        { "Q", new(Control: "\x11") },
        { "R", new(Control: "\x12") },
        { "S", new(Control: "\x13") },
        { "T", new(Control: "\x14") },
        { "U", new(Control: "\x15") },
        { "V", new(Control: "\x16") },
        { "W", new(Control: "\x17") },
        { "X", new(Control: "\x18") },
        { "Y", new(Control: "\x19") },
        { "Z", new(Control: "\x1a") },
    };

    /// <summary>
    /// Translates a key to the appropriate escape sequence
    /// </summary>
    public static byte[]? GetKeySequence(string key, bool control, bool shift, bool applicationMode)
    {
        if (KeyTranslations.TryGetValue(key, out var translation))
        {
            string? result = null;
            
            if (applicationMode && translation.ShiftOnApplication && !string.IsNullOrEmpty(translation.Shift))
                result = translation.Shift;
            else if (shift && !string.IsNullOrEmpty(translation.Shift))
                result = translation.Shift;
            else if (control && !string.IsNullOrEmpty(translation.Control))
                result = translation.Control;
            else if (!string.IsNullOrEmpty(translation.Normal))
                result = translation.Normal;
            
            return result?.Select(c => (byte)c).ToArray();
        }
        
        return null;
    }

    /// <summary>
    /// Translates a ConsoleKey to the appropriate escape sequence string
    /// </summary>
    public static string? TranslateKey(
        ConsoleKey key,
        bool ctrl = false,
        bool alt = false,
        bool shift = false,
        bool applicationMode = false)
    {
        var keyName = GetKeyName(key);
        if (keyName == null) return null;
        
        var bytes = GetKeySequence(keyName, ctrl, shift, applicationMode);
        if (bytes == null) return null;
        
        var result = new string(bytes.Select(b => (char)b).ToArray());
        
        // Prefix with ESC for Alt combinations
        if (alt && !string.IsNullOrEmpty(result))
            result = "\x1b" + result;
        
        return result;
    }

    private static string? GetKeyName(ConsoleKey key)
    {
        return key switch
        {
            ConsoleKey.F1 => "F1",
            ConsoleKey.F2 => "F2",
            ConsoleKey.F3 => "F3",
            ConsoleKey.F4 => "F4",
            ConsoleKey.F5 => "F5",
            ConsoleKey.F6 => "F6",
            ConsoleKey.F7 => "F7",
            ConsoleKey.F8 => "F8",
            ConsoleKey.F9 => "F9",
            ConsoleKey.F10 => "F10",
            ConsoleKey.F11 => "F11",
            ConsoleKey.F12 => "F12",
            ConsoleKey.UpArrow => "Up",
            ConsoleKey.DownArrow => "Down",
            ConsoleKey.LeftArrow => "Left",
            ConsoleKey.RightArrow => "Right",
            ConsoleKey.Home => "Home",
            ConsoleKey.End => "End",
            ConsoleKey.Insert => "Insert",
            ConsoleKey.Delete => "Delete",
            ConsoleKey.PageUp => "PageUp",
            ConsoleKey.PageDown => "PageDown",
            ConsoleKey.Backspace => "Back",
            ConsoleKey.Tab => "Tab",
            ConsoleKey.Enter => "Enter",
            ConsoleKey.Escape => "Escape",
            ConsoleKey.Spacebar => "Space",
            >= ConsoleKey.A and <= ConsoleKey.Z => ((char)('A' + key - ConsoleKey.A)).ToString(),
            _ => null
        };
    }

    /// <summary>
    /// Wraps text for bracketed paste mode
    /// </summary>
    public static string WrapForBracketedPaste(string text, bool bracketedPasteMode)
    {
        if (!bracketedPasteMode)
            return text;
        return $"\x1b[200~{text}\x1b[201~";
    }
}