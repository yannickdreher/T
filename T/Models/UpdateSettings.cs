using CommunityToolkit.Mvvm.ComponentModel;

namespace T.Models;

public partial class UpdateSettings : ObservableObject
{
    [ObservableProperty] private bool _checkForUpdatesOnStartup = true;
    [ObservableProperty] private string _updateChannel = "Stable";
}