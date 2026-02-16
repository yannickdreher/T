using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Text.Json.Serialization;

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
    
    [JsonIgnore]
    [ObservableProperty] private ConnectionStatus _connectionStatus = ConnectionStatus.Disconnected;
    
    [JsonIgnore]
    public bool IsConnected => ConnectionStatus == ConnectionStatus.Connected;
    
    [JsonIgnore]
    public bool IsConnecting => ConnectionStatus == ConnectionStatus.Connecting;
    
    [JsonIgnore]
    public bool IsReconnecting => ConnectionStatus == ConnectionStatus.Reconnecting;
}