using CommunityToolkit.Mvvm.ComponentModel;
using T.Models;

namespace T.UI.ViewModels;

public partial class HostKeyDialogViewModel : ObservableObject
{
    [ObservableProperty]
    private string _host = string.Empty;

    [ObservableProperty]
    private int _port;

    [ObservableProperty]
    private string _keyType = string.Empty;

    [ObservableProperty]
    private string _fingerprint = string.Empty;

    [ObservableProperty]
    private string _fingerprintMD5 = string.Empty;

    public HostKeyDialogViewModel()
    {
    }

    public HostKeyDialogViewModel(HostKeyInfo hostKeyInfo)
    {
        Host = hostKeyInfo.Host;
        Port = hostKeyInfo.Port;
        KeyType = hostKeyInfo.KeyType;
        Fingerprint = hostKeyInfo.Fingerprint;
        FingerprintMD5 = hostKeyInfo.FingerprintMD5;
    }
}