namespace T.VT;

/// <summary>
/// Translates keyboard input to VT100/XTerm escape sequences.
/// Sequences and modifier encoding follow xterm (TERM=xterm-256color):
/// modifier parameter = 1 + Shift(1) + Alt(2) + Ctrl(4).
/// </summary>
public static class KeyboardTranslations
{
    /// <summary>
    /// Computes the xterm modifier parameter (2..8) or 0 when no modifier is active.
    /// </summary>
    private static int ModifierCode(bool shift, bool alt, bool ctrl)
    {
        int code = 0;
        if (shift) code |= 1;
        if (alt) code |= 2;
        if (ctrl) code |= 4;
        return code == 0 ? 0 : code + 1;
    }

    /// <summary>
    /// Cursor keys: CSI A..D normally, SS3 A..D in application cursor key mode (DECCKM),
    /// CSI 1;mA..D with modifiers.
    /// </summary>
    private static string CursorKey(char final, int mod, bool applicationMode) =>
        mod == 0
            ? (applicationMode ? $"\x1bO{final}" : $"\x1b[{final}")
            : $"\x1b[1;{mod}{final}";

    /// <summary>
    /// Editing keys (Insert/Delete/PageUp/...): CSI n~ or CSI n;m~ with modifiers.
    /// </summary>
    private static string TildeKey(int number, int mod) =>
        mod == 0 ? $"\x1b[{number}~" : $"\x1b[{number};{mod}~";

    /// <summary>
    /// F1-F4: SS3 P..S normally, CSI 1;mP..S with modifiers (xterm behavior).
    /// </summary>
    private static string Pf1To4(char final, int mod) =>
        mod == 0 ? $"\x1bO{final}" : $"\x1b[1;{mod}{final}";

    /// <summary>
    /// Translates a ConsoleKey (+modifiers) to the escape sequence expected by the host.
    /// Returns null when the key produces no sequence (printable text arrives via text input).
    /// </summary>
    public static string? TranslateKey(
        ConsoleKey key,
        bool ctrl = false,
        bool alt = false,
        bool shift = false,
        bool applicationMode = false)
    {
        int mod = ModifierCode(shift, alt, ctrl);

        string? result = key switch
        {
            // Cursor keys
            ConsoleKey.UpArrow => CursorKey('A', mod, applicationMode),
            ConsoleKey.DownArrow => CursorKey('B', mod, applicationMode),
            ConsoleKey.RightArrow => CursorKey('C', mod, applicationMode),
            ConsoleKey.LeftArrow => CursorKey('D', mod, applicationMode),

            // Home/End send CSI H / CSI F in xterm (SS3 in application mode)
            ConsoleKey.Home => CursorKey('H', mod, applicationMode),
            ConsoleKey.End => CursorKey('F', mod, applicationMode),

            // Editing keypad
            ConsoleKey.Insert => TildeKey(2, mod),
            ConsoleKey.Delete => TildeKey(3, mod),
            ConsoleKey.PageUp => TildeKey(5, mod),
            ConsoleKey.PageDown => TildeKey(6, mod),

            // Function keys F1-F4 (PF keys)
            ConsoleKey.F1 => Pf1To4('P', mod),
            ConsoleKey.F2 => Pf1To4('Q', mod),
            ConsoleKey.F3 => Pf1To4('R', mod),
            ConsoleKey.F4 => Pf1To4('S', mod),

            // Function keys F5-F12
            ConsoleKey.F5 => TildeKey(15, mod),
            ConsoleKey.F6 => TildeKey(17, mod),
            ConsoleKey.F7 => TildeKey(18, mod),
            ConsoleKey.F8 => TildeKey(19, mod),
            ConsoleKey.F9 => TildeKey(20, mod),
            ConsoleKey.F10 => TildeKey(21, mod),
            ConsoleKey.F11 => TildeKey(23, mod),
            ConsoleKey.F12 => TildeKey(24, mod),

            // Special keys
            ConsoleKey.Enter => alt ? "\x1b\r" : "\r",
            ConsoleKey.Escape => "\x1b",
            ConsoleKey.Tab => shift ? "\x1b[Z" : (alt ? "\x1b\t" : "\t"),
            ConsoleKey.Backspace => TranslateBackspace(ctrl, alt),
            ConsoleKey.Spacebar when ctrl || alt => TranslateSpace(ctrl, alt),

            // Letters: only Ctrl/Alt combinations produce sequences here;
            // plain/shifted letters arrive via OnTextInput.
            >= ConsoleKey.A and <= ConsoleKey.Z when ctrl =>
                WrapAlt(alt, ((char)(key - ConsoleKey.A + 1)).ToString()),
            >= ConsoleKey.A and <= ConsoleKey.Z when alt =>
                "\x1b" + (char)((shift ? 'A' : 'a') + (key - ConsoleKey.A)),

            // Ctrl+digit (xterm): only some digits have control mappings
            ConsoleKey.D2 when ctrl => WrapAlt(alt, "\x00"),
            ConsoleKey.D3 when ctrl => WrapAlt(alt, "\x1b"),
            ConsoleKey.D4 when ctrl => WrapAlt(alt, "\x1c"),
            ConsoleKey.D5 when ctrl => WrapAlt(alt, "\x1d"),
            ConsoleKey.D6 when ctrl => WrapAlt(alt, "\x1e"),
            ConsoleKey.D7 when ctrl => WrapAlt(alt, "\x1f"),
            ConsoleKey.D8 when ctrl => WrapAlt(alt, "\x7f"),

            // Ctrl+OEM keys commonly used in shells
            ConsoleKey.OemMinus when ctrl => "\x1f",     // Ctrl+- (undo in emacs)
            ConsoleKey.Oem4 when ctrl => "\x1b",         // Ctrl+[ == ESC
            ConsoleKey.Oem5 when ctrl => "\x1c",         // Ctrl+\
            ConsoleKey.Oem6 when ctrl => "\x1d",         // Ctrl+]

            _ => null
        };

        return result;
    }

    private static string TranslateBackspace(bool ctrl, bool alt)
    {
        // xterm: Backspace = DEL(0x7f), Ctrl+Backspace = BS(0x08), Alt prefixes ESC
        string s = ctrl ? "\b" : "\x7f";
        return alt ? "\x1b" + s : s;
    }

    private static string TranslateSpace(bool ctrl, bool alt)
    {
        // Ctrl+Space = NUL (set-mark in emacs, alternative tmux prefix)
        string s = ctrl ? "\x00" : " ";
        return alt ? "\x1b" + s : s;
    }

    private static string WrapAlt(bool alt, string s) => alt ? "\x1b" + s : s;

    /// <summary>
    /// Wraps text for bracketed paste mode (DECSET 2004).
    /// </summary>
    public static string WrapForBracketedPaste(string text, bool bracketedPasteMode)
    {
        if (!bracketedPasteMode)
            return text;
        return $"\x1b[200~{text}\x1b[201~";
    }
}
