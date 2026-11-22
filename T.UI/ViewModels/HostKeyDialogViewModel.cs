using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using T.Models;

namespace T.UI.ViewModels;

public partial class HostKeyDialogViewModel : ObservableObject
{
    [ObservableProperty] private string _host = string.Empty;
    [ObservableProperty] private int _port;
    [ObservableProperty] private string _keyType = string.Empty;
    [ObservableProperty] private string _fingerprint = string.Empty;
    [ObservableProperty] private string _fingerprintMD5 = string.Empty;
    [ObservableProperty] private string _sessionName = string.Empty;
    [ObservableProperty] private bool _isKeyChanged;
    [ObservableProperty] private string _knownFingerprints = string.Empty;

    public string WarningTitle => IsKeyChanged ? "WARNING: REMOTE HOST IDENTIFICATION HAS CHANGED!" : "Unknown host key";

    public string WarningMessage => IsKeyChanged
        ? "The server presented a different key than the one stored in known_hosts. Someone could be eavesdropping on you right now (man-in-the-middle attack), or the host key has just been changed. Only accept if the administrator confirmed the new key."
        : "The authenticity of this host cannot be established because it is not in known_hosts yet. This is expected on the first connection.";

    public HostKeyDialogViewModel()
    {
    }

    public HostKeyDialogViewModel(HostKeyInfo hostKeyInfo)
    {
        Host = hostKeyInfo.Host;
        Port = hostKeyInfo.Port;
        KeyType = hostKeyInfo.KeyType;
        Fingerprint = $"SHA256:{hostKeyInfo.Fingerprint}";
        FingerprintMD5 = $"MD5:{hostKeyInfo.FingerprintMD5}";
        SessionName = hostKeyInfo.SessionName ?? "";
        IsKeyChanged = hostKeyInfo.Status == HostKeyStatus.Changed;
        KnownFingerprints = string.Join("\n", hostKeyInfo.KnownFingerprints.Select(f => $"SHA256:{f}"));
    }
}
