using T.VT;

namespace T.Tests;

public class WideCharacterTests
{
    private static string Row(VirtualTerminal vt, int row) => TerminalText.Row(vt, row);

    [Theory]
    [InlineData(0x0061, 1)]   // a
    [InlineData(0x00E9, 1)]   // é (precomposed)
    [InlineData(0x0301, 0)]   // combining acute accent
    [InlineData(0x00AD, 1)]   // soft hyphen (glibc: 1)
    [InlineData(0x2500, 1)]   // ─ box drawing
    [InlineData(0x4E2D, 2)]   // 中
    [InlineData(0x3000, 2)]   // ideographic space
    [InlineData(0xD55C, 2)]   // 한 (Hangul syllable)
    [InlineData(0x1160, 0)]   // Hangul Jungseong filler
    [InlineData(0xFF21, 2)]   // Ａ fullwidth
    [InlineData(0xFF71, 1)]   // ｱ halfwidth katakana
    [InlineData(0x200D, 0)]   // zero width joiner
    [InlineData(0xFE0F, 0)]   // variation selector-16
    [InlineData(0x1F600, 2)]  // 😀
    [InlineData(0x1F680, 2)]  // 🚀
    [InlineData(0x20000, 2)]  // CJK Extension B
    [InlineData(0xE0100, 0)]  // variation selector-17
    public void UnicodeWidth_MatchesWcwidth(int codePoint, int expected)
    {
        Assert.Equal(expected, UnicodeWidth.GetWidth(codePoint));
    }

    [Fact]
    public void Cjk_TakesTwoCells()
    {
        var vt = new VirtualTerminal(20, 5);
        vt.Feed("中文x");

        Assert.Equal(5, vt.CursorColumn);
        Assert.True(vt.GetCell(0, 0).IsWide);
        Assert.Equal(0x4E2D, vt.GetCell(0, 0).CodePoint);
        Assert.True(vt.GetCell(1, 0).IsWideContinuation);
        Assert.Equal(0x6587, vt.GetCell(2, 0).CodePoint);
        Assert.Equal('x', vt.GetCell(4, 0).CodePoint);
        Assert.Equal("中文x", Row(vt, 0));
    }

    [Fact]
    public void Emoji_SurrogatePair_IsOneWideCharacter()
    {
        var vt = new VirtualTerminal(20, 5);
        vt.Feed("😀x");

        Assert.Equal(0x1F600, vt.GetCell(0, 0).CodePoint);
        Assert.True(vt.GetCell(0, 0).IsWide);
        Assert.Equal('x', vt.GetCell(2, 0).CodePoint);
        Assert.Equal(3, vt.CursorColumn);
    }

    [Fact]
    public void SurrogatePair_SplitAcrossFeeds_IsJoined()
    {
        var vt = new VirtualTerminal(20, 5);
        vt.Feed("\uD83D");
        vt.Feed("\uDE00");

        Assert.Equal(0x1F600, vt.GetCell(0, 0).CodePoint);
        Assert.Equal(2, vt.CursorColumn);
    }

    [Fact]
    public void Utf8Bytes_FourByteSequence_IsDecoded()
    {
        var vt = new VirtualTerminal(20, 5);
        var bytes = "🚀!"u8.ToArray();
        vt.Feed(bytes.AsSpan(0, 2));
        vt.Feed(bytes.AsSpan(2));

        Assert.Equal(0x1F680, vt.GetCell(0, 0).CodePoint);
        Assert.Equal('!', vt.GetCell(2, 0).CodePoint);
    }

    [Fact]
    public void LoneSurrogate_BecomesReplacementCharacter()
    {
        var vt = new VirtualTerminal(20, 5);
        vt.Feed("\uD83Dx\uDE00");

        Assert.Equal("\uFFFDx\uFFFD", Row(vt, 0));
    }

    [Fact]
    public void CombiningMark_AttachesToPreviousCell()
    {
        var vt = new VirtualTerminal(20, 5);
        vt.Feed("e\u0301x");

        Assert.Equal(2, vt.CursorColumn);
        Assert.True(GraphemeTable.IsCluster(vt.GetCell(0, 0).CodePoint));
        Assert.Equal("e\u0301", vt.Graphemes.GetText(vt.GetCell(0, 0).CodePoint));
        Assert.Equal("e\u0301x", Row(vt, 0));
    }

    [Fact]
    public void CombiningMark_AfterWideCharacter_AttachesToLeftHalf()
    {
        var vt = new VirtualTerminal(20, 5);
        vt.Feed("中\u0301");

        Assert.Equal("中\u0301", vt.Graphemes.GetText(vt.GetCell(0, 0).CodePoint));
        Assert.True(vt.GetCell(0, 0).IsWide);
        Assert.Equal(2, vt.CursorColumn);
    }

    [Fact]
    public void CombiningMark_AtLineStart_IsDropped()
    {
        var vt = new VirtualTerminal(20, 5);
        vt.Feed("\u0301a");

        Assert.Equal("a", Row(vt, 0));
        Assert.Equal(1, vt.CursorColumn);
    }

    [Fact]
    public void CombiningMark_WithPendingWrap_AttachesToLastColumn()
    {
        var vt = new VirtualTerminal(3, 5);
        vt.Feed("abc\u0308");

        Assert.Equal("abc\u0308", Row(vt, 0));
        Assert.Equal(0, vt.CursorRow);
    }

    [Fact]
    public void EmojiWithVariationSelector_StaysSingleWidth()
    {
        var vt = new VirtualTerminal(20, 5);
        vt.Feed("❤\uFE0Fx"); // heart + VS16: the base is narrow, so wcwidth counts 1

        Assert.Equal("❤\uFE0F", vt.Graphemes.GetText(vt.GetCell(0, 0).CodePoint));
        Assert.Equal('x', vt.GetCell(1, 0).CodePoint);
    }

    [Fact]
    public void Wide_AtLastColumn_WrapsEarly()
    {
        var vt = new VirtualTerminal(5, 5);
        vt.Feed("abcd中");

        Assert.Equal("abcd", Row(vt, 0));
        Assert.Equal("中", Row(vt, 1));
        Assert.Equal(1, vt.CursorRow);
        Assert.Equal(2, vt.CursorColumn);
    }

    [Fact]
    public void Wide_FillingLastTwoColumns_LeavesPendingWrap()
    {
        var vt = new VirtualTerminal(4, 5);
        vt.Feed("ab中");
        Assert.Equal(0, vt.CursorRow);

        vt.Feed("x");

        Assert.Equal("ab中", Row(vt, 0));
        Assert.Equal("x", Row(vt, 1));
    }

    [Fact]
    public void Wide_WithoutAutoWrap_OverwritesLastTwoColumns()
    {
        var vt = new VirtualTerminal(4, 5);
        vt.Feed("\x1b[?7labc中");

        Assert.Equal("ab中", Row(vt, 0));
        Assert.Equal(0, vt.CursorRow);
    }

    [Fact]
    public void OverwritingRightHalf_ErasesLeftHalf()
    {
        var vt = new VirtualTerminal(20, 5);
        vt.Feed("中文\x1b[1;2Hx");

        Assert.False(vt.GetCell(0, 0).IsWide);
        Assert.Equal(' ', vt.GetCell(0, 0).CodePoint);
        Assert.Equal(" x文", Row(vt, 0));
    }

    [Fact]
    public void OverwritingLeftHalf_ErasesRightHalf()
    {
        var vt = new VirtualTerminal(20, 5);
        vt.Feed("中文\rx");

        Assert.False(vt.GetCell(1, 0).IsWideContinuation);
        Assert.Equal("x 文", Row(vt, 0));
    }

    [Fact]
    public void FastPathRun_OverRightHalf_ErasesLeftHalf()
    {
        var vt = new VirtualTerminal(20, 5);
        vt.Feed("中文\x1b[1;4Habc");

        Assert.Equal("中 abc", Row(vt, 0));
    }

    [Fact]
    public void EraseCharacters_OnRightHalf_ErasesWholeCharacter()
    {
        var vt = new VirtualTerminal(20, 5);
        vt.Feed("中文\x1b[1;2H\x1b[1X");

        Assert.Equal("  文", Row(vt, 0));
    }

    [Fact]
    public void EraseLine_FromRightHalf_ErasesWholeCharacter()
    {
        var vt = new VirtualTerminal(20, 5);
        vt.Feed("a中b\x1b[1;3H\x1b[K");

        Assert.Equal("a", Row(vt, 0));
        Assert.False(vt.GetCell(1, 0).IsWide);
    }

    [Fact]
    public void InsertCharacters_PushingWideOffTheLine_ErasesIt()
    {
        var vt = new VirtualTerminal(4, 5);
        vt.Feed("ab中\x1b[1G\x1b[1@");

        Assert.Equal(" ab", Row(vt, 0));
        Assert.False(vt.GetCell(3, 0).IsWide);
    }

    [Fact]
    public void InsertCharacters_BetweenHalves_ErasesCharacter()
    {
        var vt = new VirtualTerminal(10, 5);
        vt.Feed("中x\x1b[1;2H\x1b[1@");

        Assert.Equal("   x", Row(vt, 0));
    }

    [Fact]
    public void DeleteCharacters_OnLeftHalf_RemovesWholeCharacter()
    {
        var vt = new VirtualTerminal(20, 5);
        vt.Feed("中x\x1b[1G\x1b[1P");

        Assert.Equal(" x", Row(vt, 0));
        Assert.False(vt.GetCell(0, 0).IsWideContinuation);
    }

    [Fact]
    public void InsertMode_ShiftsLineByTwoCells()
    {
        var vt = new VirtualTerminal(20, 5);
        vt.Feed("ab\r\x1b[4h中");

        Assert.Equal("中ab", Row(vt, 0));
        Assert.Equal(2, vt.CursorColumn);
    }

    [Fact]
    public void Repeat_RepeatsWideCharacter()
    {
        var vt = new VirtualTerminal(20, 5);
        vt.Feed("中\x1b[2b");

        Assert.Equal("中中中", Row(vt, 0));
        Assert.Equal(6, vt.CursorColumn);
    }

    [Fact]
    public void CursorPositionReport_CountsWideCharactersAsTwoColumns()
    {
        var vt = new VirtualTerminal(20, 5);
        string? sent = null;
        vt.SendData += s => sent = s;
        vt.Feed("中文\x1b[6n");

        Assert.Equal("\x1b[1;5R", sent);
    }

    [Fact]
    public void Resize_CuttingWideCharacter_ErasesIt()
    {
        var vt = new VirtualTerminal(5, 5);
        vt.Feed("abc中\r\n");

        vt.Resize(4, 5);

        Assert.Equal("abc", Row(vt, 0));
        Assert.False(vt.GetCell(3, 0).IsWide);
    }

    [Fact]
    public void C1Controls_AreNotPrinted()
    {
        var vt = new VirtualTerminal(20, 5);
        vt.Feed("a\u0085b\u009Bc");

        Assert.Equal("abc", Row(vt, 0));
    }

    [Fact]
    public void ManyCombiningMarks_AreBounded()
    {
        var vt = new VirtualTerminal(20, 5);
        vt.Feed("e" + new string('\u0301', 500) + "x");

        var text = vt.Graphemes.GetText(vt.GetCell(0, 0).CodePoint);
        Assert.True(text.Length <= GraphemeTable.MaxLength);
        Assert.Equal('x', vt.GetCell(1, 0).CodePoint);
        Assert.True(vt.Graphemes.Count <= GraphemeTable.MaxLength);
    }

    [Fact]
    public void SameCluster_IsInterned()
    {
        var vt = new VirtualTerminal(20, 5);
        vt.Feed("e\u0301e\u0301");

        Assert.Equal(vt.GetCell(0, 0).CodePoint, vt.GetCell(1, 0).CodePoint);
        Assert.Equal(1, vt.Graphemes.Count);
    }
}
