using CommunityToolkit.Mvvm.ComponentModel;

namespace T.Models;

/// <summary>
/// A step-ca certificate authority that issues short-lived SSH user certificates through the
/// step CLI. Sessions opt in per host with <see cref="SshSession.StepProfileId"/>. The profile
/// holds no secrets; key pair and certificate are managed by the step certificate service.
/// Empty optional fields fall back to the step CLI configuration (step ca bootstrap / context).
/// </summary>
public partial class StepCaProfile : ObservableObject
{
    [ObservableProperty] private string _id = Guid.NewGuid().ToString();
    [ObservableProperty] private string _name = "";

    /// <summary>Certificate identity (key-id), e.g. the e-mail address of the OIDC login.</summary>
    [ObservableProperty] private string _identity = "";

    /// <summary>Optional https URL of the CA.</summary>
    [ObservableProperty] private string _caUrl = "";

    /// <summary>Optional absolute path of the CA root certificate (PEM).</summary>
    [ObservableProperty] private string _rootCertificatePath = "";

    /// <summary>Optional step context name.</summary>
    [ObservableProperty] private string _context = "";

    /// <summary>Optional provisioner name.</summary>
    [ObservableProperty] private string _provisioner = "";

    /// <summary>Optional comma separated principals; empty lets the CA derive them from the identity.</summary>
    [ObservableProperty] private string _principals = "";

    public StepCaProfile Clone() => new()
    {
        Id = Id,
        Name = Name,
        Identity = Identity,
        CaUrl = CaUrl,
        RootCertificatePath = RootCertificatePath,
        Context = Context,
        Provisioner = Provisioner,
        Principals = Principals
    };
}
