using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using T.Models;

namespace T.UI.ViewModels;

/// <summary>A tunnel type in the editor's list.</summary>
public sealed record TunnelKindOption(TunnelKind Kind, string Title);

/// <summary>Fields of the tunnel editor dialog (new tunnel or a stopped existing one).</summary>
public partial class TunnelEditorViewModel : ViewModelBase
{
    private const int DefaultForwardingPort = 8080;
    private const int DefaultSocksPort = 1080;

    private readonly bool _isNew;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private SshSession? _profile;
    [ObservableProperty] private int _listenPort = DefaultForwardingPort;
    [ObservableProperty] private string _targetHost = "localhost";
    [ObservableProperty] private int _targetPort = 80;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Kind), nameof(HasTarget), nameof(IsRemote), nameof(NamePlaceholder),
        nameof(ListenPortLabel), nameof(ListenPortHint), nameof(TargetHint))]
    private TunnelKindOption _selectedKind;

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
        _selectedKind = Kinds[0];
        _isNew = rule is null;
        if (rule is null)
        {
            Profile = preselected ?? (Profiles.Count == 1 ? Profiles[0] : null);
            return;
        }

        Name = rule.Name;
        Profile = Profiles.FirstOrDefault(p => p.Id == rule.SessionId);
        SelectedKind = Kinds.First(option => option.Kind == rule.Kind);
        ListenPort = rule.ListenPort;
        if (rule.HasTarget)
        {
            TargetHost = rule.TargetHost;
            TargetPort = rule.TargetPort;
        }
    }

    /// <summary>Terminal profiles the tunnel can connect through.</summary>
    public IReadOnlyList<SshSession> Profiles { get; }

    public bool HasProfiles => Profiles.Count > 0;

    public IReadOnlyList<TunnelKindOption> Kinds { get; } =
    [
        new(TunnelKind.Local, "Local: a port of this computer leads to a target behind the server"),
        new(TunnelKind.Remote, "Remote: a port of the server leads to a target behind this computer"),
        new(TunnelKind.Dynamic, "Dynamic: SOCKS proxy on this computer")
    ];

    public TunnelKind Kind => SelectedKind.Kind;

    /// <summary>Dynamic tunnels have no fixed target: the target fields are hidden.</summary>
    public bool HasTarget => Kind != TunnelKind.Dynamic;

    public bool IsRemote => Kind == TunnelKind.Remote;

    public string NamePlaceholder => HasTarget ? "Name (defaults to the target)" : "Name (defaults to the SOCKS port)";

    public string ListenPortLabel => IsRemote ? "Server port" : "Local port";

    public string ListenPortHint => IsRemote
        ? $"requested on the server's loopback address ({TunnelRule.BindAddress})"
        : $"on this computer only ({TunnelRule.BindAddress})";

    public string TargetHint => Kind switch
    {
        TunnelKind.Remote => "The target is resolved by this computer: localhost is this computer.",
        TunnelKind.Dynamic => "Programs that use the port as a SOCKS5 proxy name the target of each connection themselves.",
        _ => "The target is resolved by the SSH server: localhost is the server itself."
    };

    partial void OnSelectedKindChanged(TunnelKindOption? oldValue, TunnelKindOption newValue)
    {
        // A new tunnel follows the usual port of its type until the user chooses one.
        if (!_isNew || oldValue is null || ListenPort != DefaultPort(oldValue.Kind)) return;
        ListenPort = DefaultPort(newValue.Kind);
    }

    private static int DefaultPort(TunnelKind kind) => kind == TunnelKind.Dynamic ? DefaultSocksPort : DefaultForwardingPort;

    /// <summary>Returns an error message for the first invalid field, or null.</summary>
    public string? Validate()
    {
        if (Profile is null)
            return HasProfiles
                ? "Choose the terminal profile the tunnel connects through."
                : "Create a terminal profile for the SSH server first.";
        return CreateRule("", "").EndpointError;
    }

    /// <summary>The rule to save; the name defaults to the target (or the SOCKS port).</summary>
    public TunnelRule CreateRule(string id, string sessionId)
    {
        var rule = HasTarget
            ? new TunnelRule(id, sessionId, Name.Trim(), Kind, ListenPort, TargetHost.Trim(), TargetPort, false)
            : new TunnelRule(id, sessionId, Name.Trim(), Kind, ListenPort, "", 0, false);
        if (rule.Name.Length > 0) return rule;
        return rule with { Name = rule.HasTarget ? rule.TargetEndpoint : $"SOCKS {rule.ListenPort}" };
    }
}
