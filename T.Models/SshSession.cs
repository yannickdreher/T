using CommunityToolkit.Mvvm.ComponentModel;

namespace T.Models;

public partial class SshSession : ObservableObject
{
    [ObservableProperty] private string _id = Guid.NewGuid().ToString();
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _host = "";
    [ObservableProperty] private int _port = 22;
    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _privateKeyPath = "";
    [ObservableProperty] private string _privateKeyPassword = "";
    [ObservableProperty] private string? _folderId;
    [ObservableProperty] private string _description = "";

    /// <summary>
    /// Optional Id of another saved <see cref="SshSession"/> that acts as an SSH
    /// jump host (ProxyJump). When set, the connection is tunneled through that
    /// session instead of connecting to <see cref="Host"/> directly.
    /// </summary>
    [ObservableProperty] private string? _proxyJumpSessionId;

    [ObservableProperty] private ConnectionStatus _connectionStatus = ConnectionStatus.Disconnected;

    public bool IsConnected => ConnectionStatus == ConnectionStatus.Connected;
    public bool IsConnecting => ConnectionStatus == ConnectionStatus.Connecting;
    public bool IsReconnecting => ConnectionStatus == ConnectionStatus.Reconnecting;

    partial void OnConnectionStatusChanged(ConnectionStatus value)
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsConnecting));
        OnPropertyChanged(nameof(IsReconnecting));
    }
}