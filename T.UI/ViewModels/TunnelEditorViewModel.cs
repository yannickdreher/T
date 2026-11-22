using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using T.Models;

namespace T.UI.ViewModels;

/// <summary>Fields of the tunnel editor dialog (new tunnel or a stopped existing one).</summary>
public partial class TunnelEditorViewModel : ViewModelBase
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private SshSession? _profile;
    [ObservableProperty] private int _localPort = 8080;
    [ObservableProperty] private string _targetHost = "localhost";
    [ObservableProperty] private int _targetPort = 80;

    /// <summary>Design-time constructor.</summary>
    public TunnelEditorViewModel() : this(
        [new SshSession { Name = "bastion", Host = "bastion.example.com", Username = "admin" }], null, null)
    {
    }

    /// <param name="profiles">Terminal profiles to choose from.</param>
    /// <param name="rule">The edited rule, or null for a new tunnel.</param>
    /// <param name="preselected">Profile for a new tunnel (e.g. the one selected in the terminals pane).</param>
    public TunnelEditorViewModel(IEnumerable<SshSession> profiles, TunnelRule? rule, SshSession? preselected)
    {
        Profiles = profiles.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        if (rule is null)
        {
            Profile = preselected ?? (Profiles.Count == 1 ? Profiles[0] : null);
            return;
        }

        Name = rule.Name;
        Profile = Profiles.FirstOrDefault(p => p.Id == rule.SessionId);
        LocalPort = rule.LocalPort;
        TargetHost = rule.TargetHost;
        TargetPort = rule.TargetPort;
    }

    /// <summary>Terminal profiles the tunnel can connect through.</summary>
    public IReadOnlyList<SshSession> Profiles { get; }

    public bool HasProfiles => Profiles.Count > 0;

    /// <summary>Returns an error message for the first invalid field, or null.</summary>
    public string? Validate()
    {
        if (Profile is null)
            return HasProfiles
                ? "Choose the terminal profile the tunnel connects through."
                : "Create a terminal profile for the SSH server first.";
        if (LocalPort is < 1 or > 65535)
            return "The local port must be between 1 and 65535.";
        if (TargetPort is < 1 or > 65535)
            return "The target port must be between 1 and 65535.";
        if (!CreateRule("", "").HasValidEndpoints)
            return "Enter the target as a host name or IP address, as seen from the SSH server.";
        return null;
    }

    /// <summary>The rule to save; the name defaults to the target.</summary>
    public TunnelRule CreateRule(string id, string sessionId)
    {
        var rule = new TunnelRule(id, sessionId, Name.Trim(), LocalPort, TargetHost.Trim(), TargetPort, false);
        return rule.Name.Length > 0 ? rule : rule with { Name = rule.TargetEndpoint };
    }
}
