using System;

namespace T.Models;

public class TransferInfo
{
    public required string FileName { get; init; }
    public required string RemotePath { get; init; }
    public required string LocalPath { get; init; }
    public required TransferDirection Direction { get; init; }
    public long TotalBytes { get; init; }
    public long TransferredBytes { get; set; }
    public double ProgressPercent => TotalBytes > 0 ? (double)TransferredBytes / TotalBytes * 100 : 0;
    public DateTime StartedAt { get; init; } = DateTime.Now;
    
    public string SpeedDisplay
    {
        get
        {
            var elapsed = DateTime.Now - StartedAt;
            if (elapsed.TotalSeconds < 1) return "—";
            var bytesPerSec = TransferredBytes / elapsed.TotalSeconds;
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
            var elapsed = DateTime.Now - StartedAt;
            if (elapsed.TotalSeconds < 1 || TransferredBytes == 0) return "—";
            var remaining = elapsed.TotalSeconds / TransferredBytes * (TotalBytes - TransferredBytes);
            var eta = TimeSpan.FromSeconds(remaining);
            return eta.TotalHours >= 1 ? $"{eta:hh\\:mm\\:ss}" : $"{eta:mm\\:ss}";
        }
    }
}

public enum TransferDirection { Download, Upload }