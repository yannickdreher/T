using Avalonia.Controls;
using Avalonia.Input;
using T.UI.ViewModels;

namespace T.UI.Views.Components;

public partial class TunnelsWorkspaceView : UserControl
{
    public TunnelsWorkspaceView() => InitializeComponent();

    /// <summary>Double-click connects or disconnects the tunnel (like opening a terminal profile).</summary>
    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: TunnelRow row } &&
            DataContext is TunnelsWorkspaceViewModel vm &&
            vm.ToggleCommand.CanExecute(row))
        {
            vm.ToggleCommand.Execute(row);
            e.Handled = true;
        }
    }
}
