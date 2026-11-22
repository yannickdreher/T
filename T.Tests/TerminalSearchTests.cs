using T.VT;

namespace T.Tests;

public class TerminalSearchTests
{
    [Fact]
    public void Find_MapsWideAndCombiningCharactersToTheirCells()
    {
        var terminal = new VirtualTerminal(16, 2);
        terminal.Feed("a界e\u0301!");

        var search = new TerminalLineSearch("界e\u0301", matchCase: true);

        Assert.Equal([new TerminalSearchMatch(1, 4)], search.Find(terminal.Buffer[0], terminal.Width, terminal.Graphemes));
    }

    [Fact]
    public void Find_OnlyMatchesWholeCharacters()
    {
        var terminal = new VirtualTerminal(16, 2);
        terminal.Feed("café fe");

        var search = new TerminalLineSearch("fe", matchCase: true);

        // "fe" inside "fé" (e + combining accent) is not a match; the plain "fe" at the end is.
        Assert.Equal([new TerminalSearchMatch(5, 7)], search.Find(terminal.Buffer[0], terminal.Width, terminal.Graphemes));
    }

    [Fact]
    public void Update_WithinItsTimeBudget_NeverCountsStaleScreenMatches()
    {
        var terminal = new VirtualTerminal(20, 3);
        terminal.Feed("hit 1\r\nhit 2\r\nhit 3");
        var index = new TerminalSearchIndex("hit", matchCase: false);
        Assert.True(index.Update(terminal, TimeSpan.FromSeconds(5)));
        Assert.Equal(3, index.Count);

        // The old screen lines scroll into history; an update without time cannot scan them yet.
        terminal.Feed("\r\nx\r\nx\r\nx\r\nx\r\nx");
        index.Update(terminal, TimeSpan.Zero);
        for (int i = 0; i < index.Count; i++)
            Assert.NotNull(index.GetHit(terminal, i));

        Assert.True(index.Update(terminal, TimeSpan.FromSeconds(5)));
        Assert.Equal(3, index.Count);
    }

    [Fact]
    public void Find_DoesNotSearchThroughConcealedTextOrTrailingPadding()
    {
        var terminal = new VirtualTerminal(16, 2);
        terminal.Feed("AB\x1b[8msecret\x1b[0mCD");

        foreach (string query in new[] { "secret", "ABCD", "B ", "CD " })
        {
            var search = new TerminalLineSearch(query, matchCase: false);
            Assert.Empty(search.Find(terminal.Buffer[0], terminal.Width, terminal.Graphemes));
        }

        var visible = new TerminalLineSearch("cd", matchCase: false);
        Assert.Equal([new TerminalSearchMatch(8, 10)], visible.Find(terminal.Buffer[0], terminal.Width, terminal.Graphemes));
    }

    [Fact]
    public void Find_UsesLiteralCaseSensitiveOrInsensitiveNonOverlappingMatches()
    {
        var terminal = new VirtualTerminal(16, 2);
        terminal.Feed("AaAAaA [x]");

        var sensitive = new TerminalLineSearch("aA", matchCase: true);
        var insensitive = new TerminalLineSearch("aA", matchCase: false);
        var literal = new TerminalLineSearch("[x]", matchCase: true);

        Assert.Equal([new TerminalSearchMatch(1, 3), new TerminalSearchMatch(4, 6)],
            sensitive.Find(terminal.Buffer[0], terminal.Width, terminal.Graphemes));
        Assert.Equal([new TerminalSearchMatch(0, 2), new TerminalSearchMatch(2, 4), new TerminalSearchMatch(4, 6)],
            insensitive.Find(terminal.Buffer[0], terminal.Width, terminal.Graphemes));
        Assert.Equal([new TerminalSearchMatch(7, 10)], literal.Find(terminal.Buffer[0], terminal.Width, terminal.Graphemes));
    }

    [Fact]
    public void Update_SearchesHistoryAndScreenAndKeepsSelectedLineWhenItScrolls()
    {
        var terminal = new VirtualTerminal(16, 2);
        terminal.Feed("found 1\r\nfound 2\r\nfound 3");
        var search = new TerminalSearchIndex("found", matchCase: true);

        Assert.True(search.Update(terminal, TimeSpan.FromSeconds(1)));
        Assert.Equal(3, search.Count);
        var first = search.GetHit(terminal, 0)!.Value;
        var second = search.GetHit(terminal, 1)!.Value;
        var last = search.GetHit(terminal, 2)!.Value;
        Assert.Equal(0, first.Row);
        Assert.Equal(2, last.Row);
        Assert.Equal(2, search.IndexAtOrAfter(2));
        Assert.Equal(0, search.IndexAtOrAfter(3));

        terminal.Feed("\r\nfound 4");

        Assert.True(search.Update(terminal, TimeSpan.FromSeconds(1)));
        Assert.Equal(4, search.Count);
        Assert.Equal(1, search.IndexOf(terminal, second));
        Assert.Same(second.Line, search.GetHit(terminal, 1)!.Value.Line);
    }

    [Fact]
    public void Results_InvalidatedOnScreenRewriteAndAlternateScreenSwitch()
    {
        var terminal = new VirtualTerminal(16, 2);
        terminal.Feed("found 1\r\nfound 2\r\nfound 3");
        var search = new TerminalSearchIndex("found", matchCase: true);
        Assert.True(search.Update(terminal, TimeSpan.FromSeconds(1)));

        terminal.Feed("\r\x1b[2Kupdated");
        Assert.Null(search.GetHit(terminal, 2));
        Assert.True(search.GetRowMatches(terminal, 2).IsEmpty);

        terminal.Feed("\x1b[?1049hfound alternate");
        Assert.True(search.GetRowMatches(terminal, 0).IsEmpty);
        Assert.True(search.Update(terminal, TimeSpan.FromSeconds(1)));
        Assert.Single(search.GetRowMatches(terminal, 0).ToArray());
        Assert.Equal(1, search.Count);

        terminal.Feed("\x1b[?1049l");
        Assert.True(search.Update(terminal, TimeSpan.FromSeconds(1)));
        Assert.Equal(2, search.Count);
    }

    [Fact]
    public void Update_DropsEvictedRowsAndHandlesNarrowAndWideHistory()
    {
        var terminal = new VirtualTerminal(7, 2) { MaxScrollback = 1 };
        terminal.Feed("xx hit\r\nblank\r\nblank");
        var search = new TerminalSearchIndex("hit", matchCase: true);
        Assert.True(search.Update(terminal, TimeSpan.FromSeconds(1)));
        Assert.Equal(1, search.Count);

        terminal.Resize(4, 2);
        Assert.True(search.GetRowMatches(terminal, 0).IsEmpty);
        Assert.True(search.Update(terminal, TimeSpan.FromSeconds(1)));
        Assert.Equal(0, search.Count);

        terminal.Resize(20, 2);
        Assert.True(search.Update(terminal, TimeSpan.FromSeconds(1)));
        Assert.Equal(1, search.Count);

        for (int i = 0; i < 105; i++) terminal.Feed("\r\nblank");
        Assert.Null(search.GetHit(terminal, 0));
        Assert.True(search.Update(terminal, TimeSpan.FromSeconds(1)));
        Assert.Equal(0, search.Count);
    }
}
