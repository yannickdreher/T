using CommunityToolkit.Mvvm.ComponentModel;

namespace T.Models;

public partial class SshCredentials : ObservableObject
{
    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _privateKeyPath = "";
    [ObservableProperty] private string _privateKeyPassword = "";
    [ObservableProperty] private bool _saveCredentials;

    public static SshCredentials FromSession(SshSession session) => new()
    {
        Username = session.Username,
        Password = session.Password,
        PrivateKeyPath = session.PrivateKeyPath,
        PrivateKeyPassword = session.PrivateKeyPassword
    };
}
