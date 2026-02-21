using CommunityToolkit.Mvvm.ComponentModel;

namespace T.Models;

public partial class GeneralSettings : ObservableObject
{
    [ObservableProperty] private string _language = "System";
    [ObservableProperty] private string _theme = "System";
    [ObservableProperty] private bool _confirmOnClose = true;
    [ObservableProperty] private bool _reconnectOnStartup;
    [ObservableProperty] private int _connectionTimeout = 15;
    [ObservableProperty] private int _keepAliveInterval = 30;
}