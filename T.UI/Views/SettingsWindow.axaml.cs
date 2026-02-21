using Avalonia.Interactivity;
using FluentAvalonia.UI.Windowing;

namespace T.UI.Views;

public partial class SettingsWindow : AppWindow
{
    public SettingsWindow()
    {
        InitializeComponent();
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}