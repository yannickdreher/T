using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using T.UI.ViewModels;

namespace T.UI.Views.Components;

/// <summary>
/// List of command suggestions shown at the shell prompt. It never takes the keyboard focus:
/// the terminal keeps it and <see cref="T.UI.Controls.CommandSuggestionController"/> drives the selection.
/// </summary>
public partial class CommandSuggestionPopup : UserControl
{
    /// <summary>The user clicked a suggestion.</summary>
    public event Action<CommandSuggestionItem>? ItemClicked;

    public CommandSuggestionPopup()
    {
        InitializeComponent();
        List.AddHandler(PointerPressedEvent, OnItemPointerPressed, RoutingStrategies.Tunnel);
    }

    public void SetItems(IReadOnlyList<CommandSuggestionItem> items) => List.ItemsSource = items;

    private void OnItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        for (var visual = e.Source as Avalonia.Visual; visual != null && visual != List; visual = visual.GetVisualParent())
        {
            if (visual is Control { DataContext: CommandSuggestionItem item })
            {
                ItemClicked?.Invoke(item);
                e.Handled = true;
                return;
            }
        }
    }
}
