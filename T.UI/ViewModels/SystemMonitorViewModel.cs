using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentAvalonia.UI.Controls;
using T.Abstractions;
using T.Services;

namespace T.UI.ViewModels;

public readonly record struct MonitorPoint(DateTimeOffset Time, double? Value, double? Other = null);
public sealed record MonitorMetric(string Display, IReadOnlyList<MonitorPoint> Points);
public sealed record MonitorProcessRow(RemoteProcess Process, double? Cpu)
{
    public int Pid => Process.Pid;
    public long StartTime => Process.StartTime;
    public string Name => Process.Name;
    public string User => Process.User;
    public string State => Process.State;
    public long? ResidentBytes => Process.ResidentBytes;
    public string CpuDisplay => Cpu is double percent ? $"{percent:0.0}%" : "—";
    public string MemoryDisplay => ResidentBytes is long bytes ? SystemMonitorViewModel.FormatBytes(bytes) : "—";
}

/// <summary>Shares host metrics between the status bar and the live Linux dashboard.</summary>
public partial class SystemMonitorViewModel : ViewModelBase, IDisposable
{
    private const string Unknown = "—";
    private const int DefaultSortDirection = -1;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SampleTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HistoryLength = TimeSpan.FromMinutes(5);
    // The remote script waits up to LinuxProcessControl.ExitWaitMilliseconds for the process to exit.
    private static readonly TimeSpan EndProcessTimeout = TimeSpan.FromSeconds(8);
    private const int MaxProbeAttempts = 3;
    private static readonly MonitorMetric EmptyMetric = new(Unknown, []);

    private readonly SemaphoreSlim _commands = new(1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly List<MonitorPoint> _cpuHistory = [];
    private readonly List<MonitorPoint> _ramHistory = [];
    private readonly List<MonitorPoint> _diskHistory = [];
    private readonly List<MonitorPoint> _networkHistory = [];
    private CancellationTokenSource? _connectionCts;
    private CancellationTokenSource? _dashboardCts;
    private CancellationTokenSource? _detailsCts;
    private IReadOnlyList<MonitorProcessRow> _rows = [];
    private Dictionary<(int, long), long> _processTicks = [];
    private HostCounters? _previousHost;
    private long _hostTimestamp;
    private long _processTimestamp;
    private bool _activeTab;
    private bool _activeDashboard;
    private bool _refreshRequested;
    private bool _publishingRows;
    private string _sortBy = nameof(MonitorProcessRow.Cpu);
    private int _sortDirection = DefaultSortDirection;

    [ObservableProperty] private bool _isAvailable;
    [ObservableProperty] private double _cpuPercent;
    [ObservableProperty] private double _ramPercent;
    [ObservableProperty] private string _ramDisplay = Unknown;
    [ObservableProperty] private string _networkDownDisplay = Unknown;
    [ObservableProperty] private string _networkUpDisplay = Unknown;
    [ObservableProperty] private double _diskPercent;
    [ObservableProperty] private string _diskDisplay = Unknown;
    [ObservableProperty] private string _cpuDisplay = Unknown;
    [ObservableProperty] private string _diskPercentDisplay = Unknown;
    [ObservableProperty] private string _ramPercentDisplay = Unknown;

    [ObservableProperty] private MonitorMetric _cpu = EmptyMetric;
    [ObservableProperty] private MonitorMetric _memory = EmptyMetric;
    [ObservableProperty] private MonitorMetric _disk = EmptyMetric;
    [ObservableProperty] private MonitorMetric _network = EmptyMetric;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private string _targetHost = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLive), nameof(IsStatusBusy), nameof(IsStatusWarning))]
    private string _status = "Not connected";

    // Classification of Status for the dashboard indicator (colour plus the text itself).
    public bool IsLive => Status == "Live";
    public bool IsStatusBusy => Status.EndsWith('…');
    public bool IsStatusWarning => !IsLive && !IsStatusBusy && !Status.StartsWith("Paused", StringComparison.Ordinal);
    [ObservableProperty] private string _lastSample = Unknown;
    [ObservableProperty] private string _processFilter = "";
    [ObservableProperty] private string _processSummary = "No process data";
    [ObservableProperty] private IReadOnlyList<MonitorProcessRow> _processes = [];
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EndProcessCommand))]
    private MonitorProcessRow? _selectedProcess;
    [ObservableProperty] private string _commandLine = "";

    // Null until the host has answered the capability probe of this connection.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EndProcessToolTip))]
    [NotifyCanExecuteChangedFor(nameof(EndProcessCommand))]
    private ProcessSignalSupport? _signalSupport;
    [ObservableProperty] private bool _isEndProcessNoticeOpen;
    [ObservableProperty] private string _endProcessNotice = "";
    [ObservableProperty] private FAInfoBarSeverity _endProcessSeverity;

    private ISshService? _sshService;
    private int _probeAttempts;

    /// <summary>Asks the user (title, message) before a signal is sent. Without it nothing is sent.</summary>
    public Func<string, string, Task<bool>>? ConfirmEndProcess { get; set; }

    /// <summary>Names the action, or says why it is not offered on this host.</summary>
    public string EndProcessToolTip => SignalSupport switch
    {
        ProcessSignalSupport.Supported => "End process (SIGTERM)…",
        ProcessSignalSupport.NoPython => "End process is unavailable: python3 was not found on this host",
        ProcessSignalSupport.PythonTooOld => "End process is unavailable: the host's python3 has no pidfd support (Python 3.9 or newer is required)",
        ProcessSignalSupport.PidfdUnavailable => "End process is unavailable: the host's kernel or security policy does not allow pidfd signals (Linux 5.3 or newer is required)",
        ProcessSignalSupport.Unknown => "End process is unavailable: the host did not answer the capability check. Refresh to try again",
        _ => "End process is unavailable until the host has answered the capability check"
    };

    public void LoadDesignTimeData()
    {
        IsAvailable = true;
        CpuPercent = 37;
        CpuDisplay = "37%";
        RamPercent = 62;
        RamPercentDisplay = "62%";
        RamDisplay = "4.9 / 7.8 GiB";
        NetworkDownDisplay = "1.2 MiB/s";
        NetworkUpDisplay = "240 KiB/s";
        DiskPercent = 71;
        DiskPercentDisplay = "71%";
        DiskDisplay = "71%";

        // Dashboard: five minutes of history and a few processes.
        var now = DateTimeOffset.Now;
        MonitorPoint[] Series(Func<int, double> value, Func<int, double>? other = null) =>
            Enumerable.Range(0, 150).Select(i => new MonitorPoint(now.AddSeconds((i - 149) * 2), value(i), other?.Invoke(i))).ToArray();
        Cpu = new MonitorMetric("37%", Series(i => 30 + 15 * Math.Sin(i / 9.0) + (i % 7)));
        Memory = new MonitorMetric("4.9 GiB used · 2.9 GiB available", Series(i => 60 + 4 * Math.Sin(i / 30.0)));
        Disk = new MonitorMetric("71% · 28.4 GiB free", Series(_ => 71));
        Network = new MonitorMetric("↓ 1.2 MiB/s  ↑ 240 KiB/s", Series(i => 900_000 + 500_000 * Math.Sin(i / 6.0), i => 240_000 + 80_000 * Math.Cos(i / 5.0)));
        TargetHost = "admin@build-01.example.com";
        Status = "Live";
        LastSample = now.ToString("T", CultureInfo.CurrentCulture);
        Processes =
        [
            new(new RemoteProcess(1834, 1, "dotnet", "build", "R", 0, 812_000_000), 142.5),
            new(new RemoteProcess(921, 1, "postgres", "postgres", "S", 0, 256_000_000), 12.0),
            new(new RemoteProcess(1, 1, "systemd", "root", "S", 0, 12_000_000), 0.1),
            new(new RemoteProcess(733, 1, "sshd", "root", "S", 0, 7_400_000), null),
        ];
        ProcessSummary = "4 processes";
        SignalSupport = ProcessSignalSupport.Supported;
    }

    public void SetVisibility(bool activeTab, bool activeDashboard)
    {
        if (_activeTab == activeTab && _activeDashboard == activeDashboard) return;
        bool tabChanged = _activeTab != activeTab;
        _activeTab = activeTab;
        _activeDashboard = activeDashboard;
        _refreshRequested = false;
        if (tabChanged)
        {
            _previousHost = null;
            if (!activeTab) IsAvailable = false;
        }
        ResetDashboardSampling();
        if (_activeDashboard && !IsPaused && Status == "Paused")
            Status = "Refreshing host data…";
        Wake();
    }

    public void Start(ISshService sshService, string targetHost)
    {
        Stop();
        _sshService = sshService;
        TargetHost = targetHost;
        _previousHost = null;
        _cpuHistory.Clear();
        _ramHistory.Clear();
        _diskHistory.Clear();
        _networkHistory.Clear();
        Cpu = Memory = Disk = Network = EmptyMetric;
        LastSample = Unknown;
        Status = IsPaused ? "Paused" : "Waiting for host data…";
        IsEndProcessNoticeOpen = false;
        _connectionCts = new CancellationTokenSource();
        ResetDashboardSampling();
        _ = PollLoopAsync(sshService, _connectionCts.Token);
    }

    public void Stop()
    {
        _sshService = null;
        // The next connection may reach another host or another Python: probe again.
        SignalSupport = null;
        _probeAttempts = 0;
        Cancel(ref _connectionCts);
        Cancel(ref _dashboardCts);
        Cancel(ref _detailsCts);
        _previousHost = null;
        _refreshRequested = false;
        _rows = [];
        _processTicks.Clear();
        ApplyProcessFilter();
        IsAvailable = false;
        CommandLine = "";
        ProcessSummary = "No process data";
        Status = "Not connected";
    }

    partial void OnIsPausedChanged(bool value)
    {
        ResetDashboardSampling();
        if (_sshService is not null)
            Status = value ? "Paused" : "Refreshing host data…";
        Wake();
    }

    partial void OnProcessFilterChanged(string value) => ApplyProcessFilter();

    partial void OnSelectedProcessChanged(MonitorProcessRow? oldValue, MonitorProcessRow? newValue)
    {
        if (_publishingRows || SameProcess(oldValue, newValue)) return;
        Cancel(ref _detailsCts);
        CommandLine = "";
        if (newValue is null || !_activeDashboard || _sshService is not { } service || _dashboardCts is null) return;
        _detailsCts = CancellationTokenSource.CreateLinkedTokenSource(_dashboardCts.Token);
        CommandLine = "Loading command line…";
        _ = LoadDetailsAsync(service, newValue.Process, _detailsCts.Token);
    }

    [RelayCommand]
    private void Refresh()
    {
        if (!_activeDashboard || _sshService is null) return;
        if (SignalSupport == ProcessSignalSupport.Unknown)
        {
            SignalSupport = null;
            _probeAttempts = 0;
        }
        _refreshRequested = true;
        Wake();
    }

    private bool CanEndProcess() =>
        SelectedProcess is not null && SignalSupport == ProcessSignalSupport.Supported && _sshService is not null;

    // The command is disabled while it runs, so a second click cannot send a second signal.
    [RelayCommand(CanExecute = nameof(CanEndProcess))]
    private async Task EndProcessAsync()
    {
        if (SelectedProcess?.Process is not { } process || _sshService is not { } service ||
            _connectionCts is not { } connection || ConfirmEndProcess is not { } confirm)
            return;

        var token = connection.Token;
        string label = $"\"{process.Name}\" (PID {process.Pid})";
        if (!await confirm("End process",
                $"Send SIGTERM to {label} on {TargetHost}?\n\n" +
                "The process is asked to exit and may discard unsaved work. " +
                "The signal is only sent if the PID still belongs to this process."))
            return;

        ShowEndProcessNotice(FAInfoBarSeverity.Informational, $"Sending SIGTERM to {label}…");
        try
        {
            // The process that was confirmed is signalled, even if the selection moved on meanwhile.
            var result = await ReadAsync(service, LinuxProcessControl.EndCommand(process.Pid, process.StartTime),
                4096, token, EndProcessTimeout);
            var outcome = LinuxProcessControl.ParseEnd(result);
            // After a timeout the signal may have been sent: look once, never send again.
            RemoteProcessState? state = outcome == EndProcessOutcome.TimedOut
                ? LinuxProcessControl.ParseState(
                    await ReadAsync(service, LinuxProcessControl.StateCommand(process.Pid), 4096, token), process)
                : null;
            var (severity, message) = DescribeEndProcess(outcome, state, label, process.Pid);
            ShowEndProcessNotice(severity, message);
            if (outcome == EndProcessOutcome.Unsupported && ReferenceEquals(service, _sshService))
            {
                SignalSupport = null;
                _probeAttempts = 0;
            }
        }
        catch (OperationCanceledException)
        {
            ShowEndProcessNotice(FAInfoBarSeverity.Warning,
                $"The connection closed before the host answered. SIGTERM may or may not have reached {label}.");
        }

        if (!_activeDashboard || !ReferenceEquals(service, _sshService)) return;
        _refreshRequested = true;
        Wake();
    }

    private void ShowEndProcessNotice(FAInfoBarSeverity severity, string message)
    {
        EndProcessSeverity = severity;
        EndProcessNotice = message;
        IsEndProcessNoticeOpen = true;
    }

    private static (FAInfoBarSeverity Severity, string Message) DescribeEndProcess(
        EndProcessOutcome outcome, RemoteProcessState? state, string label, int pid) => outcome switch
        {
            EndProcessOutcome.Ended => (FAInfoBarSeverity.Success, $"{label} has ended."),
            EndProcessOutcome.StillRunning => (FAInfoBarSeverity.Warning,
                $"SIGTERM was sent to {label}, but the process is still running. It may need more time or ignore the signal."),
            EndProcessOutcome.PermissionDenied => (FAInfoBarSeverity.Error,
                $"Permission denied: the SSH user is not allowed to signal {label}. Nothing was sent."),
            EndProcessOutcome.Gone => (FAInfoBarSeverity.Informational,
                $"{label} had already exited. Nothing was sent."),
            EndProcessOutcome.IdentityChanged => (FAInfoBarSeverity.Warning,
                $"The selection has expired: PID {pid} now belongs to another process. Nothing was sent."),
            EndProcessOutcome.Unsupported => (FAInfoBarSeverity.Error,
                "This host cannot end processes safely (python3 3.9 or newer with pidfd support on Linux 5.3 or newer is required). Nothing was sent."),
            EndProcessOutcome.TimedOut => state switch
            {
                RemoteProcessState.Ended => (FAInfoBarSeverity.Informational,
                    $"The host did not answer in time. {label} is no longer running."),
                RemoteProcessState.Replaced => (FAInfoBarSeverity.Informational,
                    $"The host did not answer in time. {label} is no longer running; PID {pid} belongs to another process now."),
                RemoteProcessState.Running => (FAInfoBarSeverity.Warning,
                    $"The host did not answer in time and {label} is still running. SIGTERM may not have been sent; it was not sent again."),
                _ => (FAInfoBarSeverity.Warning,
                    $"The host did not answer in time and the state of {label} could not be read. SIGTERM was not sent again.")
            },
            EndProcessOutcome.Unavailable => (FAInfoBarSeverity.Error,
                $"The SSH command channel is unavailable. SIGTERM may not have reached {label}."),
            _ => (FAInfoBarSeverity.Error,
                $"The host gave an unexpected answer. It is unknown whether SIGTERM reached {label}; check the process list.")
        };

    public void SortProcesses(string property)
    {
        if (_sortBy == property) _sortDirection *= -1;
        else
        {
            _sortBy = property;
            _sortDirection = property is nameof(MonitorProcessRow.Cpu) or nameof(MonitorProcessRow.ResidentBytes) ? -1 : 1;
        }
        ApplyProcessFilter();
    }

    private async Task PollLoopAsync(ISshService service, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (!_activeTab)
                {
                    await _wake.WaitAsync(token);
                    continue;
                }

                var dashboard = _dashboardCts;
                bool manual = _refreshRequested;
                _refreshRequested = false;
                bool showSample = _activeDashboard && (!IsPaused || manual);
                var host = await ReadAsync(service, LinuxSystemMetrics.HostCommand, 256 * 1024, token);
                if (token.IsCancellationRequested) return;
                if (_activeTab)
                    PublishHost(host, showSample && dashboard == _dashboardCts && _activeDashboard);

                if (showSample && dashboard is not null && dashboard == _dashboardCts && IsAvailable)
                    await SampleProcessesAsync(service, dashboard.Token);
                if (_activeDashboard && IsAvailable)
                    await ProbeSignalSupportAsync(service, token);

                await _wake.WaitAsync(PollInterval, token);
            }
        }
        catch (OperationCanceledException) { }
    }

    private void PublishHost(SshCommandResult result, bool updateDashboard)
    {
        var now = DateTimeOffset.UtcNow;
        if (result.Outcome != SshCommandOutcome.Completed || result.ExitStatus != 0)
        {
            IsAvailable = false;
            _previousHost = null;
            _processTicks.Clear();
            _processTimestamp = 0;
            if (!updateDashboard) return;
            Status = result.ExitStatus == 42
                ? "Linux /proc metrics are unavailable on this host"
                : $"{DescribeFailure(result)}. Displayed data may be out of date.";
            AppendHistory(now, null, null, null, null);
            return;
        }

        var counters = LinuxSystemMetrics.ParseHost(result.Output);
        long stamp = Stopwatch.GetTimestamp();
        var cpu = LinuxSystemMetrics.CpuPercent(_previousHost, counters);
        var rate = LinuxSystemMetrics.NetworkRate(_previousHost, counters, Stopwatch.GetElapsedTime(_hostTimestamp, stamp).TotalSeconds);
        var memory = counters.Memory;
        var disk = counters.Disk;
        _previousHost = counters;
        _hostTimestamp = stamp;
        IsAvailable = counters.Cpu is not null || memory is not null || disk is not null || counters.Network.Count != 0;
        CpuPercent = cpu ?? 0;
        CpuDisplay = DisplayPercent(cpu);
        RamPercent = memory is { } mem ? 100d * mem.Used / mem.Total : 0;
        RamPercentDisplay = DisplayPercent(memory is not null ? RamPercent : null);
        RamDisplay = memory is { } available ? $"{FormatBytes(available.Used)} / {FormatBytes(available.Total)}" : Unknown;
        DiskPercent = disk?.Percent ?? 0;
        DiskPercentDisplay = DisplayPercent(disk?.Percent);
        DiskDisplay = disk is { } fs ? $"{FormatBytes(fs.Available)} available / {FormatBytes(fs.Total)}" : Unknown;
        NetworkDownDisplay = rate is { } speeds ? $"{FormatBytes(speeds.Received)}/s" : Unknown;
        NetworkUpDisplay = rate is { } upload ? $"{FormatBytes(upload.Sent)}/s" : Unknown;
        if (!updateDashboard) return;

        LastSample = now.ToLocalTime().ToString("T", CultureInfo.CurrentCulture);
        Status = IsPaused ? "Paused · refreshed" : IsAvailable ? "Live" : "Host metrics unavailable";
        AppendHistory(now, cpu, memory is null ? null : RamPercent, disk?.Percent, rate);
    }

    private void AppendHistory(DateTimeOffset now, double? cpu, double? memory, double? disk, (double Received, double Sent)? network)
    {
        Cpu = UpdateMetric(_cpuHistory, new(now, cpu), cpu is null ? Unknown : CpuDisplay);
        Memory = UpdateMetric(_ramHistory, new(now, memory), memory is null ? Unknown : RamDisplay);
        Disk = UpdateMetric(_diskHistory, new(now, disk), disk is null ? Unknown : DiskDisplay);
        Network = UpdateMetric(_networkHistory, new(now, network?.Received, network?.Sent),
            network is null ? Unknown : $"↓ {NetworkDownDisplay}   ↑ {NetworkUpDisplay}");
    }

    private static MonitorMetric UpdateMetric(List<MonitorPoint> history, MonitorPoint point, string display)
    {
        history.RemoveAll(value => value.Time < point.Time - HistoryLength);
        if (history.Count > 0 && point.Time - history[^1].Time > 3 * PollInterval)
            history.Add(new(point.Time - PollInterval, null));
        history.Add(point);
        return new MonitorMetric(display, history.ToArray());
    }

    private async Task SampleProcessesAsync(ISshService service, CancellationToken token)
    {
        try
        {
            var result = await ReadAsync(service, LinuxSystemMetrics.ProcessCommand, 4 * 1024 * 1024, token);
            token.ThrowIfCancellationRequested();
            if (result.Outcome != SshCommandOutcome.OutputLimitExceeded &&
                (result.Outcome != SshCommandOutcome.Completed || result.ExitStatus != 0))
            {
                ResetProcessCounters();
                ProcessSummary = $"{DescribeFailure(result)}. Process data may be out of date.";
                return;
            }
            var sample = LinuxSystemMetrics.ParseProcesses(result.Output, result.Outcome == SshCommandOutcome.OutputLimitExceeded);
            if (sample is not { } data)
            {
                ResetProcessCounters();
                ProcessSummary = "Could not read process counters";
                return;
            }

            long now = Stopwatch.GetTimestamp();
            double elapsed = Stopwatch.GetElapsedTime(_processTimestamp, now).TotalSeconds;
            var counters = new Dictionary<(int, long), long>(data.Rows.Count);
            var rows = new List<MonitorProcessRow>(data.Rows.Count);
            foreach (var process in data.Rows)
            {
                var id = (process.Pid, process.StartTime);
                if (!counters.TryAdd(id, process.CpuTicks)) continue;
                double? cpu = _processTimestamp != 0 && elapsed > 0 &&
                    _processTicks.TryGetValue(id, out long previous) && process.CpuTicks >= previous
                    ? 100d * (process.CpuTicks - previous) / data.ClockTicks / elapsed
                    : null;
                rows.Add(new MonitorProcessRow(process, cpu));
            }
            _processTicks = counters;
            _processTimestamp = now;
            _rows = rows;
            ProcessSummary = rows.Count > 0
                ? $"{rows.Count:N0} processes{(data.IsIncomplete ? " · incomplete snapshot" : "")}"
                : "No readable processes";
            ApplyProcessFilter();
        }
        catch (OperationCanceledException) { }
    }

    private async Task LoadDetailsAsync(ISshService service, RemoteProcess process, CancellationToken token)
    {
        try
        {
            var result = await ReadAsync(service, LinuxSystemMetrics.ProcessDetailsCommand(process.Pid), 24 * 1024, token);
            token.ThrowIfCancellationRequested();
            string? text = result.Outcome == SshCommandOutcome.Completed && result.ExitStatus == 0
                ? LinuxSystemMetrics.ParseCommandLine(result.Output, process)
                : null;
            CommandLine = string.IsNullOrEmpty(text) ? "Command line unavailable" : text;
        }
        catch (OperationCanceledException) { }
    }

    // Once per connection, and only for the dashboard: the status bar does not need it.
    private async Task ProbeSignalSupportAsync(ISshService service, CancellationToken token)
    {
        if (SignalSupport is not (null or ProcessSignalSupport.Unknown) || _probeAttempts >= MaxProbeAttempts) return;
        _probeAttempts++;
        var result = await ReadAsync(service, LinuxProcessControl.ProbeCommand, 4096, token);
        if (!token.IsCancellationRequested && ReferenceEquals(service, _sshService))
            SignalSupport = LinuxProcessControl.ParseProbe(result);
    }

    private async Task<SshCommandResult> ReadAsync(
        ISshService service, string command, int limit, CancellationToken token, TimeSpan? timeout = null)
    {
        await _commands.WaitAsync(token);
        try { return await service.RunBoundedCommandAsync(command, limit, timeout ?? SampleTimeout, token); }
        finally { _commands.Release(); }
    }

    private void ApplyProcessFilter()
    {
        var previousSelection = SelectedProcess;
        var query = ProcessFilter?.Trim() ?? "";
        IEnumerable<MonitorProcessRow> matching = _rows;
        if (query.Length > 0)
        {
            matching = matching.Where(row =>
                row.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                row.User.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                row.Pid.ToString(CultureInfo.InvariantCulture).Contains(query, StringComparison.Ordinal));
        }

        var rows = matching.ToList();
        rows.Sort(CompareRows);
        _publishingRows = true;
        try
        {
            Processes = rows;
            SelectedProcess = rows.FirstOrDefault(row => SameProcess(previousSelection, row));
        }
        finally { _publishingRows = false; }

        if (previousSelection is not null && SelectedProcess is null)
        {
            Cancel(ref _detailsCts);
            CommandLine = "";
        }
    }

    private int CompareRows(MonitorProcessRow first, MonitorProcessRow second)
    {
        int primary = _sortBy switch
        {
            nameof(MonitorProcessRow.Pid) => first.Pid.CompareTo(second.Pid),
            nameof(MonitorProcessRow.Name) => StringComparer.OrdinalIgnoreCase.Compare(first.Name, second.Name),
            nameof(MonitorProcessRow.User) => StringComparer.OrdinalIgnoreCase.Compare(first.User, second.User),
            nameof(MonitorProcessRow.State) => StringComparer.Ordinal.Compare(first.State, second.State),
            nameof(MonitorProcessRow.ResidentBytes) => Nullable.Compare(first.ResidentBytes, second.ResidentBytes),
            _ => Nullable.Compare(first.Cpu, second.Cpu)
        };
        return primary == 0 ? first.Pid.CompareTo(second.Pid) : _sortDirection * Math.Sign(primary);
    }

    private static bool SameProcess(MonitorProcessRow? first, MonitorProcessRow? second) =>
        first is not null && second is not null && first.Pid == second.Pid && first.StartTime == second.StartTime;

    private void ResetDashboardSampling()
    {
        Cancel(ref _detailsCts);
        Cancel(ref _dashboardCts);
        if (_connectionCts is not null)
            _dashboardCts = CancellationTokenSource.CreateLinkedTokenSource(_connectionCts.Token);
        ResetProcessCounters();
        if (CommandLine == "Loading command line…") CommandLine = "";
    }

    private void ResetProcessCounters()
    {
        _processTicks.Clear();
        _processTimestamp = 0;
    }

    private void Wake()
    {
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    private static void Cancel(ref CancellationTokenSource? source)
    {
        if (source is null) return;
        var current = source;
        source = null;
        // SSH.NET's cancellation callback can send a signal and briefly wait on the server.
        _ = CancelAsync(current);
    }

    private static async Task CancelAsync(CancellationTokenSource source)
    {
        try { await source.CancelAsync().ConfigureAwait(false); }
        finally { source.Dispose(); }
    }

    private static string DescribeFailure(SshCommandResult result) => result.Outcome switch
    {
        SshCommandOutcome.TimedOut => "Host query timed out",
        SshCommandOutcome.OutputLimitExceeded => "Host query exceeded the output limit",
        SshCommandOutcome.Unavailable => "SSH command channel unavailable",
        _ when result.ExitStatus == 43 => "Required Linux process counters are unavailable",
        _ => "Host query failed"
    };

    private static string DisplayPercent(double? value) => value is double percent ? $"{percent:0}%" : Unknown;

    public static string FormatBytes(double bytes)
    {
        const double KiB = 1024;
        const double MiB = KiB * 1024;
        const double GiB = MiB * 1024;
        const double TiB = GiB * 1024;
        return bytes switch
        {
            < KiB => $"{bytes:0} B",
            < MiB => $"{bytes / KiB:0.0} KiB",
            < GiB => $"{bytes / MiB:0.0} MiB",
            < TiB => $"{bytes / GiB:0.0} GiB",
            _ => $"{bytes / TiB:0.00} TiB"
        };
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
