using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace T.UI.Views.Components;

public partial class SessionStatusBarView : UserControl
{
    public event Action? CopyRequested;
    public event Action? ClearRequested;

    public SessionStatusBarView()
    {
        InitializeComponent();
        this.FindControl<ComboBox>("FontSizeSelector")!.ItemsSource = Enumerable.Range(6, 67).ToArray();
    }

    private void OnCopy(object? sender, RoutedEventArgs e) => CopyRequested?.Invoke();
    private void OnClear(object? sender, RoutedEventArgs e) => ClearRequested?.Invoke();
}
