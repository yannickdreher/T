using System.Diagnostics;

namespace T.Models;

public class TransferInfo
{
    private readonly long _startTimestamp = Stopwatch.GetTimestamp();

    public required string FileName { get; init; }
    public required string RemotePath { get; init; }
    public required string LocalPath { get; init; }
    public required TransferDirection Direction { get; init; }
    public long TotalBytes { get; init; }
    public long TransferredBytes { get; set; }
    public double ProgressPercent => TotalBytes > 0 ? Math.Min(100, (double)TransferredBytes / TotalBytes * 100) : 0;

    private double ElapsedSeconds => Stopwatch.GetElapsedTime(_startTimestamp).TotalSeconds;

    public string SpeedDisplay
    {
        get
        {
            var elapsed = ElapsedSeconds;
            if (elapsed < 1) return "–";
            var bytesPerSec = TransferredBytes / elapsed;
            return bytesPerSec switch
            {
                >= 1_073_741_824 => $"{bytesPerSec / 1_073_741_824:F1} GB/s",
                >= 1_048_576 => $"{bytesPerSec / 1_048_576:F1} MB/s",
                >= 1024 => $"{bytesPerSec / 1024:F1} KB/s",
                _ => $"{bytesPerSec:F0} B/s"
            };
        }
    }

    public string EtaDisplay
    {
        get
        {
            var elapsed = ElapsedSeconds;
            if (elapsed < 1 || TransferredBytes <= 0) return "–";
            var remaining = elapsed / TransferredBytes * Math.Max(0, TotalBytes - TransferredBytes);
            var eta = TimeSpan.FromSeconds(remaining);
            return eta.TotalHours >= 1 ? $"{eta:hh\\:mm\\:ss}" : $"{eta:mm\\:ss}";
        }
    }
}

public enum TransferDirection { Download, Upload }
