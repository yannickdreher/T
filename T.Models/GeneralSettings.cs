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

    /// <summary>Offer ~/.ssh/id_ed25519, id_ecdsa and id_rsa when a session has no explicit key (OpenSSH behavior).</summary>
    [ObservableProperty] private bool _useDefaultIdentityFiles = true;

    /// <summary>Sessions that were open when the app was closed (used by <see cref="ReconnectOnStartup"/>).</summary>
    [ObservableProperty] private List<string> _lastOpenSessionIds = [];
}
