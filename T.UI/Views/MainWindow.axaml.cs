using FluentAvalonia.UI.Controls;
using FluentAvalonia.UI.Windowing;
using T.UI.ViewModels;

namespace T.UI.Views;

public partial class MainWindow : FAAppWindow
{
    public MainWindow()
    {
        InitializeComponent();

        TitleBar.ExtendsContentIntoTitleBar = true;
        TitleBar.Height = 32;
    }

    public MainWindow(MainWindowViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private void OnTabCloseRequested(FATabView sender, FATabViewTabCloseRequestedEventArgs args)
    {
        if (DataContext is MainWindowViewModel vm && args.Item is SessionViewModel session)
            vm.SessionsTree.CloseSessionCommand.Execute(session);
    }
}