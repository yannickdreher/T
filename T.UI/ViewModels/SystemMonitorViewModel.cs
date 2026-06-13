using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using T.Abstractions;

namespace T.UI.ViewModels;

/// <summary>
/// Polls the remote host (Linux-style /proc + df) over an SSH exec channel
/// and exposes CPU, RAM, network and disk usage. Hides itself when the
/// remote host does not provide the expected interfaces.
/// </summary>
public partial class SystemMonitorViewModel : ViewModelBase, IDisposable
{
    private const string StatCommand =
        "cat /proc/stat /proc/meminfo /proc/net/dev 2>/dev/null; echo '==DF=='; df -kP / 2>/dev/null";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private const int MaxConsecutiveFailures = 3;

    private ISshService? _sshService;
    private CancellationTokenSource? _pollCts;
    private int _failureCount;

    // CPU delta state
    private long _prevCpuTotal;
    private long _prevCpuIdle;

    // Network delta state
    private long _prevNetRx = -1;
    private long _prevNetTx = -1;
    private DateTime _prevNetSample;

    [ObservableProperty] private bool _isAvailable;
    [ObservableProperty] private double _cpuPercent;
    [ObservableProperty] private double _ramPercent;
    [ObservableProperty] private string _ramDisplay = "—";
    [ObservableProperty] private string _networkDownDisplay = "—";
    [ObservableProperty] private string _networkUpDisplay = "—";
    [ObservableProperty] private double _diskPercent;
    [ObservableProperty] private string _diskDisplay = "—";

    public SystemMonitorViewModel() { }

    public void LoadDesignTimeData()
    {
        IsAvailable = true;
        CpuPercent = 37;
        RamPercent = 62;
        RamDisplay = "4.9 / 7.8 GB";
        NetworkDownDisplay = "1.2 MB/s";
        NetworkUpDisplay = "240 KB/s";
        DiskPercent = 71;
        DiskDisplay = "71%";
    }

    public void Start(ISshService sshService)
    {
        Stop();
        _sshService = sshService;
        _failureCount = 0;
        _prevCpuTotal = _prevCpuIdle = 0;
        _prevNetRx = _prevNetTx = -1;

        _pollCts = new CancellationTokenSource();
        _ = PollLoopAsync(_pollCts.Token);
    }

    public void Stop()
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts = null;
        _sshService = null;
        Dispatcher.UIThread.Post(() => IsAvailable = false);
    }

    private async Task PollLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var service = _sshService;
            if (service is null || !service.IsConnected)
            {
                Dispatcher.UIThread.Post(() => IsAvailable = false);
                return;
            }

            string? output = null;
            try
            {
                output = await service.RunCommandAsync(StatCommand, token);
            }
            catch
            {
                // treated as failure below
            }

            if (token.IsCancellationRequested) return;

            if (string.IsNullOrWhiteSpace(output) || !output.Contains("cpu "))
            {
                if (++_failureCount >= MaxConsecutiveFailures)
                {
                    Dispatcher.UIThread.Post(() => IsAvailable = false);
                    return; // host has no /proc – give up silently
                }
            }
            else
            {
                _failureCount = 0;
                ParseAndPublish(output);
            }

            try { await Task.Delay(PollInterval, token); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void ParseAndPublish(string output)
    {
        double? cpu = null, ram = null, disk = null;
        string ramText = "—", netDown = "—", netUp = "—", diskText = "—";

        long memTotal = 0, memAvailable = -1;
        long netRx = 0, netTx = 0;
        bool netFound = false;
        bool inDf = false;

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("==DF==", StringComparison.Ordinal)) { inDf = true; continue; }

            if (inDf)
            {
                // Filesystem 1024-blocks Used Available Capacity Mounted-on
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 5 && parts[4].EndsWith('%') &&
                    double.TryParse(parts[4].TrimEnd('%'), NumberStyles.Any, CultureInfo.InvariantCulture, out var pct))
                {
                    disk = pct;
                    if (long.TryParse(parts[1], out var totalKb) && long.TryParse(parts[2], out var usedKb))
                        diskText = $"{FormatBytes(usedKb * 1024L)} / {FormatBytes(totalKb * 1024L)}";
                    else
                        diskText = $"{pct:0}%";
                }
                continue;
            }

            if (line.StartsWith("cpu ", StringComparison.Ordinal))
            {
                // cpu user nice system idle iowait irq softirq steal ...
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                long total = 0, idle = 0;
                for (int i = 1; i < parts.Length && i <= 8; i++)
                {
                    if (!long.TryParse(parts[i], out var v)) continue;
                    total += v;
                    if (i == 4 || i == 5) idle += v; // idle + iowait
                }

                if (_prevCpuTotal > 0 && total > _prevCpuTotal)
                {
                    var totalDelta = total - _prevCpuTotal;
                    var idleDelta = idle - _prevCpuIdle;
                    cpu = Math.Clamp(100.0 * (totalDelta - idleDelta) / totalDelta, 0, 100);
                }
                _prevCpuTotal = total;
                _prevCpuIdle = idle;
            }
            else if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
            {
                memTotal = ParseMemInfoKb(line);
            }
            else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
            {
                memAvailable = ParseMemInfoKb(line);
            }
            else if (line.Contains(':') && !line.StartsWith("Inter-", StringComparison.Ordinal) &&
                     !line.StartsWith("face", StringComparison.Ordinal))
            {
                // /proc/net/dev data line: "eth0: rxBytes rxPackets ... txBytes ..."
                var colonIdx = line.IndexOf(':');
                var iface = line[..colonIdx].Trim();
                if (iface == "lo") continue;

                var fields = line[(colonIdx + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length >= 16 &&
                    long.TryParse(fields[0], out var rx) &&
                    long.TryParse(fields[8], out var tx))
                {
                    netRx += rx;
                    netTx += tx;
                    netFound = true;
                }
            }
        }

        // RAM
        if (memTotal > 0 && memAvailable >= 0)
        {
            var usedKb = memTotal - memAvailable;
            ram = Math.Clamp(100.0 * usedKb / memTotal, 0, 100);
            ramText = $"{FormatBytes(usedKb * 1024L)} / {FormatBytes(memTotal * 1024L)}";
        }

        // Network rates from deltas
        var now = DateTime.UtcNow;
        if (netFound && _prevNetRx >= 0)
        {
            var seconds = Math.Max(0.5, (now - _prevNetSample).TotalSeconds);
            var rxRate = Math.Max(0, netRx - _prevNetRx) / seconds;
            var txRate = Math.Max(0, netTx - _prevNetTx) / seconds;
            netDown = $"{FormatBytes((long)rxRate)}/s";
            netUp = $"{FormatBytes((long)txRate)}/s";
        }
        if (netFound)
        {
            _prevNetRx = netRx;
            _prevNetTx = netTx;
            _prevNetSample = now;
        }

        Dispatcher.UIThread.Post(() =>
        {
            IsAvailable = true;
            if (cpu.HasValue) CpuPercent = cpu.Value;
            if (ram.HasValue) { RamPercent = ram.Value; RamDisplay = ramText; }
            if (netFound && _prevNetRx >= 0) { NetworkDownDisplay = netDown; NetworkUpDisplay = netUp; }
            if (disk.HasValue) { DiskPercent = disk.Value; DiskDisplay = diskText; }
        });
    }

    private static long ParseMemInfoKb(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && long.TryParse(parts[1], out var kb) ? kb : 0;
    }

    private static string FormatBytes(long bytes)
    {
        const double kb = 1024, mb = kb * 1024, gb = mb * 1024, tb = gb * 1024;
        return bytes switch
        {
            < 0 => "0 B",
            < 1024 => $"{bytes} B",
            _ when bytes < mb => $"{bytes / kb:0.0} KB",
            _ when bytes < gb => $"{bytes / mb:0.0} MB",
            _ when bytes < tb => $"{bytes / gb:0.0} GB",
            _ => $"{bytes / tb:0.00} TB"
        };
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
