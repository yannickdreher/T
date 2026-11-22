using T.Models;

namespace T.Tests;

public class PaneSetTests
{
    private static readonly string[] Open = ["a", "b", "c", "d", "e"];

    [Fact]
    public void SingleLayout_ShowsTheActiveItem()
    {
        var panes = new PaneSet<string>();

        panes.Update(1, Open, "c");
        Assert.Equal(["c"], panes.Panes);

        panes.Update(1, Open, "a");
        Assert.Equal(["a"], panes.Panes);
        Assert.Equal(0, panes.ActiveIndex);
    }

    [Fact]
    public void Splitting_KeepsTheActiveItemAndFillsTheOtherPanes()
    {
        var panes = new PaneSet<string>();
        panes.Update(1, Open, "c");

        panes.Update(2, Open, "c");

        Assert.Equal(["c", "a"], panes.Panes);
        Assert.Equal(0, panes.ActiveIndex);

        panes.Update(4, Open, "c");
        Assert.Equal(["c", "a", "b", "d"], panes.Panes);
    }

    [Fact]
    public void SelectingAShownItem_OnlyMovesTheActivePane()
    {
        var panes = new PaneSet<string>();
        panes.Update(4, Open, "a");

        Assert.False(panes.Update(4, Open, "c"));

        Assert.Equal(["a", "b", "c", "d"], panes.Panes);
        Assert.Equal(2, panes.ActiveIndex);
    }

    [Fact]
    public void SelectingAHiddenItem_ReplacesTheActivePaneOnly()
    {
        var panes = new PaneSet<string>();
        panes.Update(4, Open, "a");
        panes.Update(4, Open, "c");

        Assert.True(panes.Update(4, Open, "e"));

        Assert.Equal(["a", "b", "e", "d"], panes.Panes);
        Assert.Equal(2, panes.ActiveIndex);
    }

    [Fact]
    public void ClosingAnItem_FreesItsPaneForAHiddenOne()
    {
        var panes = new PaneSet<string>();
        panes.Update(2, Open, "a");

        panes.Update(2, ["a", "c", "d"], "a");

        Assert.Equal(["a", "c"], panes.Panes);
    }

    [Fact]
    public void FewerItemsThanPanes_LeavesPanesEmpty()
    {
        var panes = new PaneSet<string>();

        panes.Update(4, ["a", "b"], "b");

        Assert.Equal(["b", "a", null, null], panes.Panes);
        Assert.Equal(0, panes.ActiveIndex);

        panes.Update(4, [], null);
        Assert.Equal([null, null, null, null], panes.Panes);
    }

    [Fact]
    public void ReducingThePanes_KeepsTheActiveItemVisible()
    {
        var panes = new PaneSet<string>();
        panes.Update(4, Open, "a");
        panes.Update(4, Open, "d");

        panes.Update(2, Open, "d");

        Assert.Contains("d", panes.Panes);
        Assert.Equal("d", panes.Panes[panes.ActiveIndex]);

        panes.Update(1, Open, "d");
        Assert.Equal(["d"], panes.Panes);
    }

    [Theory]
    [InlineData(TerminalLayout.One, 1)]
    [InlineData(TerminalLayout.SideBySide, 2)]
    [InlineData(TerminalLayout.Stacked, 2)]
    [InlineData(TerminalLayout.Grid, 4)]
    public void PaneCount_FollowsTheLayout(TerminalLayout layout, int expected)
    {
        Assert.Equal(expected, layout.PaneCount());
    }
}
