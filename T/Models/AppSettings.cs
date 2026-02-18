using CommunityToolkit.Mvvm.ComponentModel;

namespace T.Models;

public partial class AppSettings : ObservableObject
{
    [ObservableProperty] private GeneralSettings _general = new();
    [ObservableProperty] private UpdateSettings _update = new();
    [ObservableProperty] private ExplorerSettings _explorer = new();
    [ObservableProperty] private TerminalSettings _terminal = new();
}