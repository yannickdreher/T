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
    /// session instead of connecting to <see cref="Host"/> directly. A jump host
    /// may itself use a jump host (multi-hop chain).
    /// </summary>
    [ObservableProperty] private string? _proxyJumpSessionId;

    /// <summary>"user@host:port" for tooltips (port only when it is not 22).</summary>
    public string Address =>
        (string.IsNullOrEmpty(Username) ? "" : Username + "@") + Host + (Port == 22 ? "" : ":" + Port);

    partial void OnUsernameChanged(string value) => OnPropertyChanged(nameof(Address));
    partial void OnHostChanged(string value) => OnPropertyChanged(nameof(Address));
    partial void OnPortChanged(int value) => OnPropertyChanged(nameof(Address));

    /// <summary>Creates a detached copy of all persisted fields (used by edit dialogs).</summary>
    public SshSession Clone()
    {
        var copy = new SshSession { Id = Id };
        copy.CopyFrom(this);
        return copy;
    }

    /// <summary>Copies all persisted fields from <paramref name="other"/>; identity and runtime state are kept.</summary>
    public void CopyFrom(SshSession other)
    {
        Name = other.Name;
        Host = other.Host;
        Port = other.Port;
        Username = other.Username;
        Password = other.Password;
        PrivateKeyPath = other.PrivateKeyPath;
        PrivateKeyPassword = other.PrivateKeyPassword;
        FolderId = other.FolderId;
        Description = other.Description;
        ProxyJumpSessionId = other.ProxyJumpSessionId;
    }
}
