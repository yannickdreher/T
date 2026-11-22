using System;
using Avalonia;
using Avalonia.Controls;
using T.UI.ViewModels;

namespace T.UI.Views.Components;

public partial class SystemMonitorDashboard : UserControl
{
    private const double MinCardWidth = 210;

    public SystemMonitorDashboard() => InitializeComponent();

    protected override Size MeasureOverride(Size availableSize)
    {
        // The cards share the row: four side by side when there is room, otherwise two or one.
        if (Content is Control { Margin: var margin } && !double.IsInfinity(availableSize.Width))
            LayoutCards(availableSize.Width - margin.Left - margin.Right);
        return base.MeasureOverride(availableSize);
    }

    private void LayoutCards(double width)
    {
        if (width <= 0) return;
        double spacing = Cards.ItemSpacing;
        int columns = width >= 4 * MinCardWidth + 3 * spacing ? 4
            : width >= 2 * MinCardWidth + spacing ? 2
            : 1;
        double itemWidth = Math.Floor((width - (columns - 1) * spacing) / columns);
        if (double.IsNaN(Cards.ItemWidth) || Math.Abs(Cards.ItemWidth - itemWidth) > 0.5)
            Cards.ItemWidth = itemWidth;
    }

    private void OnSort(object? sender, DataGridColumnEventArgs e)
    {
        if (DataContext is not SystemMonitorViewModel monitor || string.IsNullOrEmpty(e.Column.SortMemberPath))
            return;

        e.Handled = true;
        monitor.SortProcesses(e.Column.SortMemberPath);
    }
}
