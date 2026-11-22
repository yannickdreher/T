using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using T.Models;

namespace T.UI.ViewModels;

/// <summary>One saved tunnel in the tunnels view with its runtime state and traffic.</summary>
/// <param name="rule">The saved rule.</param>
/// <param name="profile">The terminal profile the tunnel connects through (live object of the terminals pane).</param>
public partial class TunnelRow(TunnelRule rule, SshSession profile) : ObservableObject
{
    private const string None = "—";
    private static readonly TimeSpan HistoryLength = TimeSpan.FromMinutes(5);

    private readonly List<MonitorPoint> _samples = [];
    private TunnelStatistics _previous;
    private DateTimeOffset? _startedAt;

    public TunnelRule Rule { get; } = rule;
    public SshSession Profile { get; } = profile;

    /// <summary>
    /// What the user has to know before a remote tunnel starts: the client can only request the
    /// server's loopback address.
    /// </summary>
    public const string RemoteBindNotice =
        "The tunnel asks the server to listen on its loopback address only. A server configured with " +
        "\"GatewayPorts yes\" may listen on all its addresses instead, which makes the port reachable " +
        "from the server's network.";

    /// <summary>"127.0.0.1:8080 → db.internal:5432"; see <see cref="TunnelRule.Route"/>.</summary>
    public string Route => Rule.Route;

    /// <summary>The type in the list: "Local", "Remote" or "SOCKS".</summary>
    public string KindText => Rule.Kind switch
    {
        TunnelKind.Remote => "Remote",
        TunnelKind.Dynamic => "SOCKS",
        _ => "Local"
    };

    /// <summary>The type with its direction, as tooltip and in the details.</summary>
    public string KindDescription => Rule.Kind switch
    {
        TunnelKind.Remote => "Remote forwarding: the server listens, the target is reached from this computer",
        TunnelKind.Dynamic => "Dynamic forwarding: SOCKS proxy on this computer, targets are reached from the server",
        _ => "Local forwarding: this computer listens, the target is reached from the server"
    };

    public bool IsRemote => Rule.Kind == TunnelKind.Remote;

    public TunnelState State => Status.State;
    public bool IsRunning => State is TunnelState.Starting or TunnelState.Active or TunnelState.Stopping;
    public bool IsActive => State == TunnelState.Active;
    public bool IsBusy => State is TunnelState.Starting or TunnelState.Stopping;
    public bool IsFailed => State == TunnelState.Failed;
    public string? Error => Status.Error;
    public bool HasError => !string.IsNullOrEmpty(Status.Error);

    public string StateText => State switch
    {
        TunnelState.Starting => "Connecting…",
        TunnelState.Active => "Active",
        TunnelState.Stopping => "Disconnecting…",
        TunnelState.Failed => "Failed",
        _ => "Inactive"
    };

    /// <summary>Text of the connect/disconnect button.</summary>
    public string ToggleText => IsRunning ? "Disconnect" : "Connect";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(State), nameof(IsRunning), nameof(IsActive), nameof(IsBusy), nameof(IsFailed),
        nameof(Error), nameof(HasError), nameof(StateText), nameof(ToggleText))]
    private TunnelStatus _status;

    // ── Traffic (updated about once per second while the tunnel runs) ───

    [ObservableProperty] private string _downRate = None;
    [ObservableProperty] private string _upRate = None;
    [ObservableProperty] private string _receivedTotal = None;
    [ObservableProperty] private string _sentTotal = None;
    [ObservableProperty] private int _openConnections;
    [ObservableProperty] private long _totalConnections;
    [ObservableProperty] private string _uptime = None;

    /// <summary>Received (Value) and sent (Other) bytes per second over the last five minutes.</summary>
    [ObservableProperty] private IReadOnlyList<MonitorPoint> _history = [];

    /// <summary>Top of the chart scale: the highest rate of the history ("peak 1.2 MiB/s").</summary>
    [ObservableProperty] private string _peakRate = None;

    /// <summary>A new connection starts: counters, rates and history start from zero.</summary>
    public void BeginTraffic(DateTimeOffset now)
    {
        _previous = default;
        _startedAt = now;
        _samples.Clear();
        History = [];
        PeakRate = None;
        DownRate = UpRate = None;
        ReceivedTotal = SentTotal = None;
        OpenConnections = 0;
        TotalConnections = 0;
        Uptime = None;
    }

    /// <summary>Applies the counters of the running tunnel; <paramref name="seconds"/> is the time since the last sample.</summary>
    public void ApplySample(TunnelStatistics stats, DateTimeOffset now, double seconds)
    {
        double received = Math.Max(0, stats.BytesReceived - _previous.BytesReceived) / Math.Max(0.1, seconds);
        double sent = Math.Max(0, stats.BytesSent - _previous.BytesSent) / Math.Max(0.1, seconds);
        _previous = stats;

        DownRate = Rate(received);
        UpRate = Rate(sent);
        ReceivedTotal = SystemMonitorViewModel.FormatBytes(stats.BytesReceived);
        SentTotal = SystemMonitorViewModel.FormatBytes(stats.BytesSent);
        OpenConnections = stats.OpenConnections;
        TotalConnections = stats.TotalConnections;
        Uptime = _startedAt is { } started ? FormatDuration(now - started) : None;

        _samples.Add(new MonitorPoint(now, received, sent));
        _samples.RemoveAll(point => point.Time < now - HistoryLength);
        History = _samples.ToArray();
        PeakRate = Rate(_samples.Max(point => Math.Max(point.Value ?? 0, point.Other ?? 0)));
    }

    /// <summary>The tunnel stopped: the totals stay, rates and uptime end; the history gets a gap.</summary>
    public void EndTraffic(DateTimeOffset now)
    {
        DownRate = UpRate = None;
        OpenConnections = 0;
        Uptime = None;
        if (_samples.Count > 0)
        {
            _samples.Add(new MonitorPoint(now, null, null));
            History = _samples.ToArray();
        }
    }

    private static string Rate(double bytesPerSecond) => $"{SystemMonitorViewModel.FormatBytes(bytesPerSecond)}/s";

    private static string FormatDuration(TimeSpan duration) => duration.TotalHours >= 1
        ? $"{(int)duration.TotalHours} h {duration.Minutes:00} min"
        : duration.TotalMinutes >= 1 ? $"{(int)duration.TotalMinutes} min" : $"{duration.Seconds} s";
}
