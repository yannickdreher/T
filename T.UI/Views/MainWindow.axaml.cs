using Avalonia.Interactivity;
using FluentAvalonia.UI.Controls;
using FluentAvalonia.UI.Windowing;
using T.UI.ViewModels;

namespace T.UI.Views;

public partial class MainWindow : AppWindow
{
    public MainWindow()
    {
        InitializeComponent();

        TitleBar.ExtendsContentIntoTitleBar = true;
        TitleBar.TitleBarHitTestType = TitleBarHitTestType.Complex;
        TitleBar.Height = 32;
    }

    public MainWindow(MainWindowViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private void OnTabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (DataContext is MainWindowViewModel vm && args.Item is SessionViewModel session)
        {
            vm.SessionsTree.CloseSessionCommand.Execute(session);
        }
    }

    private void OnAboutClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.ShowAboutCommand.Execute(null);
        }
    }
}