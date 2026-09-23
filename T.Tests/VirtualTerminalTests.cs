using T.VT;

namespace T.Tests;

public class VirtualTerminalTests
{
    private static string RowText(VirtualTerminal vt, int row) => TerminalText.Row(vt, row);

    [Fact]
    public void DeleteCharacters_WithCountLargerThanLine_DoesNotThrow()
    {
        var vt = new VirtualTerminal(20, 5);
        vt.Feed("hello\x1b[1G\x1b[500P");
        Assert.Equal("", RowText(vt, 0));
    }

    [Fact]
    public void HugeParameters_AreClamped()
    {
        var vt = new VirtualTerminal(20, 5);
        vt.Feed("abc\x1b[99999999999999@x\x1b[99999999999999X");
        Assert.Equal("abcx", RowText(vt, 0));
    }

    [Fact]
    public void Osc_TerminatedByStringTerminator_DoesNotPrintBackslash()
    {
        var vt = new VirtualTerminal(20, 5);
        string? title = null;
        vt.TitleChanged += t => title = t;

        vt.Feed("\x1b]0;my title\x1b\\ok");

        Assert.Equal("my title", title);
        Assert.Equal("ok", RowText(vt, 0));
    }

    [Fact]
    public void Dcs_PayloadIsIgnored()
    {
        var vt = new VirtualTerminal(20, 5);
        vt.Feed("\x1bPtmux;\x1b\x1b[31mX\x1b\\ok");
        Assert.Equal("ok", RowText(vt, 0));
    }

    [Fact]
    public void EscapeWithIntermediate_DoesNotPrintFinalByte()
    {
        var vt = new VirtualTerminal(20, 5);
        vt.Feed("\x1b%Gok");
        Assert.Equal("ok", RowText(vt, 0));
    }

    [Fact]
    public void KittyKeyboardPush_DoesNotRestoreCursor()
    {
        var vt = new VirtualTerminal(20, 5);
        vt.Feed("\x1b[s");            // save at 0,0
        vt.Feed("abc\x1b[>1u");       // kitty keyboard push must be ignored
        Assert.Equal(3, vt.CursorColumn);
    }

    [Fact]
    public void ModifyOtherKeys_DoesNotChangeAttributes()
    {
        var vt = new VirtualTerminal(20, 5);
        vt.Feed("\x1b[>4;1mA");
        Assert.Equal(CellAttributes.None, vt.GetCell(0, 0).Attributes);
    }

    [Fact]
    public void ColonSubParameters_Underline_DoNotSetBackground()
    {
        var vt = new VirtualTerminal(20, 5);
        vt.Feed("\x1b[4:3mA");
        var cell = vt.GetCell(0, 0);
        Assert.True(cell.IsUnderline);
        Assert.True(cell.Background.IsDefault);
    }

    [Fact]
    public void ColonTrueColor_WithColorSpace_IsParsed()
    {
        var vt = new VirtualTerminal(20, 5);
        vt.Feed("\x1b[38:2::10:20:30mA");
        Assert.Equal(((byte)10, (byte)20, (byte)30), vt.GetCell(0, 0).Foreground.ToRgb());
    }

    [Fact]
    public void UnderlineColor_ArgumentsAreNotInterpretedAsAttributes()
    {
        var vt = new VirtualTerminal(20, 5);
        vt.Feed("\x1b[1;58;2;255;0;0mA");
        Assert.True(vt.GetCell(0, 0).IsBold);
    }

    [Fact]
    public void EraseScrollback_KeepsVisibleScreen()
    {
        var vt = new VirtualTerminal(20, 3);
        vt.Feed("1\r\n2\r\n3\r\n4");
        Assert.NotEmpty(vt.Scrollback);

        vt.Feed("\x1b[3J");

        Assert.Empty(vt.Scrollback);
        Assert.Equal("4", RowText(vt, 2));
    }

    [Fact]
    public void Resize_Shrink_KeepsCursorLineVisible()
    {
        var vt = new VirtualTerminal(20, 10);
        for (int i = 0; i < 9; i++) vt.Feed($"line{i}\r\n");
        vt.Feed("prompt$");
        Assert.Equal(9, vt.CursorRow);

        vt.Resize(20, 5);

        Assert.Equal(4, vt.CursorRow);
        Assert.Equal("prompt$", RowText(vt, 4));
        Assert.Equal("line4", RowOf(vt.Scrollback[^1]));
    }

    private static string RowOf(TerminalLine line) => TerminalText.Line(line, new GraphemeTable());

    [Fact]
    public void Resize_ResetsScrollRegion()
    {
        var vt = new VirtualTerminal(20, 5);
        vt.Feed("\x1b[1;3r");
        vt.Resize(20, 10);
        vt.Feed("\x1b[10;1Hx\n");
        // With a stale region (rows 1-3) the line feed would not be able to move past row 3.
        Assert.Equal(9, vt.CursorRow);
    }

    [Fact]
    public void PrivateCursorPositionReport_UsesQuestionMarkFormat()
    {
        var vt = new VirtualTerminal(20, 5);
        string? sent = null;
        vt.SendData += s => sent = s;
        vt.Feed("ab\x1b[?6n");
        Assert.Equal("\x1b[?1;3R", sent);
    }
}
