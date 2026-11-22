using T.VT;
using static T.VT.KeyboardTranslations;

namespace T.Tests;

/// <summary>
/// The inputs mirror what Avalonia reports on Windows for real keyboard layouts
/// (recorded with SendInput against loaded layouts).
/// </summary>
public class KeyboardTranslationsTests
{
    private static string? Translate(ConsoleKey? key, string? symbol, bool ctrl = false, bool alt = false, bool shift = false,
        ConsoleKey? physical = null, bool mac = false, bool appCursor = false) =>
        KeyboardTranslations.Translate(new KeyPress(key, physical ?? key, symbol, ctrl, alt, shift), appCursor, mac);

    [Theory]
    [InlineData(ConsoleKey.Q, "@")]        // German AltGr+Q
    [InlineData(ConsoleKey.D7, "{")]       // German AltGr+7
    [InlineData(ConsoleKey.Oem4, "\\")]    // German AltGr+ß
    [InlineData(ConsoleKey.E, "€")]        // AltGr+E
    [InlineData(ConsoleKey.D0, "@")]       // French AltGr+à
    [InlineData(ConsoleKey.A, "ą")]        // Polish (Programmers) AltGr+A
    [InlineData(ConsoleKey.D2, "@")]       // Spanish / Swedish AltGr+2
    public void AltGr_LeavesCharacterToTextInput(ConsoleKey key, string symbol) =>
        Assert.Null(Translate(key, symbol, ctrl: true, alt: true));

    [Fact]
    public void CtrlAlt_WithoutLayoutCharacter_IsAShortcut() =>
        Assert.Equal("\x1b\x11", Translate(ConsoleKey.Q, null, ctrl: true, alt: true));

    [Theory]
    [InlineData(ConsoleKey.C, "\x03")]
    [InlineData(ConsoleKey.D, "\x04")]
    [InlineData(ConsoleKey.Z, "\x1a")]
    public void CtrlLetter_SendsControlCharacter(ConsoleKey key, string expected) =>
        Assert.Equal(expected, Translate(key, null, ctrl: true));

    [Fact]
    public void CtrlLetter_OnNonLatinLayoutWithoutLatinKey_UsesPhysicalPosition() =>
        Assert.Equal("\x03", Translate(null, "с", ctrl: true, physical: ConsoleKey.C));

    [Theory]
    [InlineData(ConsoleKey.OemPeriod, ".", "\x1b.")]   // readline: insert last argument
    [InlineData(ConsoleKey.OemPeriod, "ю", "\x1bю")]   // Russian layout, same key
    [InlineData(ConsoleKey.OemMinus, "_", "\x1b_")]
    [InlineData(ConsoleKey.B, "b", "\u001bb")]
    public void Alt_SendsEscapePrefix(ConsoleKey key, string symbol, string expected) =>
        Assert.Equal(expected, Translate(key, symbol, alt: true));

    [Fact]
    public void AltLetter_OnRussianLayout_UsesTheLatinKeySoShortcutsWork() =>
        Assert.Equal("\u001bb", Translate(ConsoleKey.B, "и", alt: true));

    [Fact]
    public void MacOption_TypesLayoutCharacter() =>
        Assert.Null(Translate(ConsoleKey.L, "@", alt: true, mac: true));

    [Theory]
    [InlineData(ConsoleKey.A, "a")]
    [InlineData(ConsoleKey.Q, "й")]
    [InlineData(ConsoleKey.NumPad7, "7")]
    [InlineData(ConsoleKey.Oem5, "^")] // German dead key: composed by the platform
    public void PlainKeys_AreLeftToTextInput(ConsoleKey key, string symbol) =>
        Assert.Null(Translate(key, symbol));

    [Theory]
    [InlineData(ConsoleKey.Enter, false, false, "\r")]
    [InlineData(ConsoleKey.Backspace, false, false, "\x7f")]
    [InlineData(ConsoleKey.Backspace, false, true, "\x1b\x7f")]
    [InlineData(ConsoleKey.Tab, true, false, "\x1b[Z")]
    [InlineData(ConsoleKey.UpArrow, false, false, "\x1b[A")]
    [InlineData(ConsoleKey.F5, false, false, "\x1b[15~")]
    public void SpecialKeys(ConsoleKey key, bool shift, bool alt, string expected) =>
        Assert.Equal(expected, Translate(key, null, shift: shift, alt: alt));

    [Fact]
    public void CursorKeys_InApplicationMode_UseSs3() =>
        Assert.Equal("\x1bOA", Translate(ConsoleKey.UpArrow, null, appCursor: true));

    [Fact]
    public void CtrlArrow_UsesModifierParameter() =>
        Assert.Equal("\x1b[1;5D", Translate(ConsoleKey.LeftArrow, null, ctrl: true));

    [Fact]
    public void CtrlSpace_SendsNul() =>
        Assert.Equal("\0", Translate(ConsoleKey.Spacebar, " ", ctrl: true));
}
