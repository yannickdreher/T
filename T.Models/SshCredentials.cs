using CommunityToolkit.Mvvm.ComponentModel;

namespace T.Models;

public partial class SshCredentials : ObservableObject
{
    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _privateKeyPath = "";
    [ObservableProperty] private string _privateKeyPassword = "";
    [ObservableProperty] private bool _saveCredentials;

    /// <summary>step-ca profile of the session (see <see cref="SshSession.StepProfileId"/>); never entered in a dialog.</summary>
    [ObservableProperty] private string? _stepProfileId;

    public static SshCredentials FromSession(SshSession session) => new()
    {
        Username = session.Username,
        Password = session.Password,
        PrivateKeyPath = session.PrivateKeyPath,
        PrivateKeyPassword = session.PrivateKeyPassword,
        StepProfileId = session.StepProfileId
    };
}
