using System.Globalization;
using System.Text;

namespace T.Services;

public sealed record HostCounters(
    (long Total, long Idle)? Cpu,
    (long Used, long Total)? Memory,
    (double Percent, long Available, long Total)? Disk,
    IReadOnlyDictionary<string, (long Received, long Sent)> Network,
    string? Hostname = null,
    string? Version = null);

public sealed record RemoteProcess(int Pid, long StartTime, string Name, string User, string State, long CpuTicks, long? ResidentBytes);

public static class LinuxSystemMetrics
{
    /// <summary>
    /// Runs a POSIX sh script whatever the login shell is (bash, fish, tcsh, ...). SSH exec hands the
    /// command to the login shell; the script travels base64 encoded inside one single-quoted
    /// argument, so neither its quotes and line breaks nor the shell's syntax matter.
    /// </summary>
    public static string InPosixShell(string script)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(script.ReplaceLineEndings("\n")));
        return $"sh -c 'eval \"$(printf %s {encoded} | base64 -d)\"'";
    }

    /// <summary>CPU, memory, network and root file system counters (exit 42: no /proc).</summary>
    public static string HostCommand { get; } = InPosixShell(HostScript);

    /// <summary>All processes as NUL-framed /proc/PID/stat records with the user name (exit 43: no getconf).</summary>
    public static string ProcessCommand { get; } = InPosixShell(ProcessScript);

    private const string HostScript = """
        export LC_ALL=C
        if [ ! -r /proc/stat ]; then exit 42; fi
        cat /proc/stat /proc/meminfo /proc/net/dev 2>/dev/null
        printf '\n==DF==\n'
        df -kP / 2>/dev/null || true
        printf '\n==HOSTNAME==\n'
        cat /proc/sys/kernel/hostname 2>/dev/null || true
        printf '\n==VERSION==\n'
        cat /proc/version 2>/dev/null || true
        """;

    private const string ProcessScript = """
        export LC_ALL=C
        ticks=$(getconf CLK_TCK) && pages=$(getconf PAGESIZE) || exit 43
        printf '%s %s\000' "$ticks" "$pages"
        printf '%s\n' /proc/[0-9]* | awk '
            BEGIN {
                while ((getline line < "/etc/passwd") > 0) {
                    split(line, fields, ":")
                    users[fields[3]] = fields[1]
                }
                close("/etc/passwd")
            }
            {
                if (NR > 10000) { printf "!limit%c", 0; exit }
                path = $0 "/status"
                uid = "-"
                while ((getline line < path) > 0) {
                    if (line ~ /^Uid:/) {
                        split(line, fields)
                        uid = (fields[3] in users) ? users[fields[3]] : fields[3]
                        break
                    }
                }
                close(path)
                path = $0 "/stat"
                if ((getline record < path) > 0) {
                    while ((getline line < path) > 0) record = record "\n" line
                    printf "%s\t%s%c", uid, record, 0
                }
                close(path)
            }'
        """;

    public static string ProcessDetailsCommand(int pid)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pid);
        return InPosixShell(string.Create(CultureInfo.InvariantCulture, $"""
            path=/proc/{pid}
            cat "$path/stat" 2>/dev/null || exit 44
            printf '\000'
            head -c 16384 "$path/cmdline" 2>/dev/null || exit 44
            printf '\000'
            cat "$path/stat" 2>/dev/null || exit 44
            """));
    }

    public static HostCounters ParseHost(string output)
    {
        (long Total, long Idle)? cpu = null;
        (double Percent, long Available, long Total)? disk = null;
        var network = new Dictionary<string, (long, long)>(StringComparer.Ordinal);
        long memoryTotal = 0;
        long memoryAvailable = -1;
        bool inDisk = false;
        string? hostname = null, version = null;
        string? section = null;

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line == "==HOSTNAME==") { section = "hostname"; continue; }
            if (line == "==VERSION==") { section = "version"; continue; }
            if (section is not null)
            {
                if (line.Length > 0)
                {
                    if (section == "hostname") hostname ??= line;
                    else version ??= line;
                }
                continue;
            }
            if (line == "==DF==")
            {
                inDisk = true;
                continue;
            }
            var fields = SplitFields(line);
            if (inDisk)
            {
                if (fields.Length >= 6 && fields[4].EndsWith('%') &&
                    double.TryParse(fields[4].AsSpan(0, fields[4].Length - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out double percent) &&
                    TryReadBytes(fields[3], 1024, out var available) && TryReadBytes(fields[1], 1024, out var total))
                    disk = (percent, available, total);
            }
            else if (fields.Length >= 9 && fields[0] == "cpu")
            {
                cpu = ParseCpu(fields);
            }
            else if (fields.Length >= 2 && fields[0] is "MemTotal:" or "MemAvailable:")
            {
                if (TryReadBytes(fields[1], 1024, out var value))
                {
                    if (fields[0] == "MemTotal:") memoryTotal = value;
                    else memoryAvailable = value;
                }
            }
            else if (line.IndexOf(':') is int colon && colon > 0)
            {
                var name = line[..colon].Trim();
                var counters = SplitFields(line[(colon + 1)..]);
                if (name != "lo" && counters.Length >= 16 &&
                    TryReadLong(counters[0], out var received) && TryReadLong(counters[8], out var sent))
                    network[name] = (received, sent);
            }
        }

        return new HostCounters(cpu,
            memoryTotal > 0 && memoryAvailable >= 0 ? (Math.Max(0, memoryTotal - memoryAvailable), memoryTotal) : null,
            disk, network, hostname, version);
    }

    public static (IReadOnlyList<RemoteProcess> Rows, long ClockTicks, bool IsIncomplete)? ParseProcesses(
        string output, bool outputTruncated)
    {
        var records = output.Split('\0');
        var header = SplitFields(records[0]);
        if (header.Length != 2 || !TryReadLong(header[0], out long ticks) || ticks == 0 ||
            !TryReadLong(header[1], out long pageSize) || pageSize == 0)
            return null;

        var rows = new List<RemoteProcess>();
        bool incomplete = outputTruncated;
        for (int index = 1; index < records.Length - 1; index++)
        {
            string record = records[index];
            int separator = record.IndexOf('\t');
            if (separator < 0)
            {
                incomplete = true;
                continue;
            }
            var process = ParseStat(record[(separator + 1)..], Sanitize(record[..separator]), pageSize);
            if (process is not null) rows.Add(process);
            else incomplete = true;
        }

        return (rows, ticks, incomplete);
    }

    public static string? ParseCommandLine(string output, RemoteProcess expected)
    {
        int first = output.IndexOf('\0');
        int last = output.LastIndexOf('\0');
        if (first < 0 || last <= first) return null;
        var before = ParseStat(output[..first], "", 1);
        var after = ParseStat(output[(last + 1)..], "", 1);
        if (before?.Pid != expected.Pid || before.StartTime != expected.StartTime ||
            after?.Pid != expected.Pid || after.StartTime != expected.StartTime)
            return null;

        return Sanitize(output[(first + 1)..last].TrimEnd('\0').Replace('\0', ' '));
    }

    public static double? CpuPercent(HostCounters? previous, HostCounters current)
    {
        if (previous?.Cpu is not { } old || current.Cpu is not { } value) return null;
        long total = value.Total - old.Total;
        long idle = value.Idle - old.Idle;
        return total > 0 && idle >= 0 && idle <= total ? 100d * (total - idle) / total : null;
    }

    public static (double Received, double Sent)? NetworkRate(HostCounters? previous, HostCounters current, double seconds)
    {
        if (previous is null || seconds <= 0) return null;
        double received = 0, sent = 0;
        bool hasSample = false;
        foreach (var (name, counters) in current.Network)
        {
            if (!previous.Network.TryGetValue(name, out var old) ||
                counters.Received < old.Received || counters.Sent < old.Sent) continue;
            received += (counters.Received - old.Received) / seconds;
            sent += (counters.Sent - old.Sent) / seconds;
            hasSample = true;
        }
        return hasSample ? (received, sent) : null;
    }

    internal static RemoteProcess? ParseStat(string text, string user, long pageSize)
    {
        int open = text.IndexOf('(');
        int close = text.LastIndexOf(')');
        if (open < 2 || close <= open || !int.TryParse(text.AsSpan(0, open - 1), NumberStyles.None, CultureInfo.InvariantCulture, out int pid) ||
            pid <= 0) return null;
        // Field 3 (state) starts after the closing parenthesis, regardless of the process name.
        var fields = SplitFields(text[(close + 1)..].Trim());
        if (fields.Length < 22 || !TryReadLong(fields[11], out long userTicks) ||
            !TryReadLong(fields[12], out long systemTicks) || systemTicks > long.MaxValue - userTicks ||
            !TryReadLong(fields[19], out long startTime)) return null;

        long? rss = TryReadBytes(fields[21], pageSize, out var bytes) ? bytes : null;
        return new RemoteProcess(pid, startTime, Sanitize(text[(open + 1)..close]), user, fields[0], userTicks + systemTicks, rss);
    }

    private static (long Total, long Idle)? ParseCpu(string[] fields)
    {
        Span<long> values = stackalloc long[8];
        long total = 0;
        for (int i = 0; i < values.Length; i++)
        {
            if (!TryReadLong(fields[i + 1], out var value) || value > long.MaxValue - total) return null;
            values[i] = value;
            total += value;
        }
        return (total, values[3] + values[4]);
    }

    private static string Sanitize(string text)
    {
        var result = new StringBuilder(text.Length);
        foreach (char character in text)
            result.Append(char.IsControl(character) ? ' ' : character);
        return result.ToString();
    }

    private static string[] SplitFields(string line) => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static bool TryReadLong(string text, out long value) =>
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    private static bool TryReadBytes(string text, long multiplier, out long bytes)
    {
        bytes = 0;
        if (!TryReadLong(text, out long value) || value > long.MaxValue / multiplier) return false;
        bytes = value * multiplier;
        return true;
    }
}
