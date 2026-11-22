using T.Abstractions;

namespace T.UI.ViewModels;

public partial class SessionViewModel
{
    internal ISshService CreateTunnelTransport()
    {
        if (_sshService is not null) return _sshService;
        var service = _sshManager!.CreateTunnel(Session);
        AttachAuthenticationHandlers(service);
        return _sshService = service;
    }

    private void AttachAuthenticationHandlers(ISshService service)
    {
        service.CredentialsRequired += () => ShowCredentialsDialogAsync(null);
        service.AuthenticationFailed += ShowCredentialsDialogAsync;
        service.HostKeyVerificationRequired += ShowHostKeyDialogAsync;
        service.AuthPromptRequired += ShowAuthPromptAsync;
    }
}
