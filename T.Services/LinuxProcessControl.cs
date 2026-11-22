using System.Globalization;
using T.Abstractions;

namespace T.Services;

/// <summary>Whether a host can signal a process through a pidfd, and if not, why.</summary>
public enum ProcessSignalSupport
{
    Supported,
    NoPython,
    PythonTooOld,
    PidfdUnavailable,
    Unknown
}

/// <summary>What an attempt to end a process led to.</summary>
public enum EndProcessOutcome
{
    /// <summary>SIGTERM was delivered and the process exited while the script waited.</summary>
    Ended,
    /// <summary>SIGTERM was delivered; the process was still running when the script stopped waiting.</summary>
    StillRunning,
    PermissionDenied,
    /// <summary>The process had exited before the signal; nothing was sent.</summary>
    Gone,
    /// <summary>The PID belongs to another process now; nothing was sent.</summary>
    IdentityChanged,
    Unsupported,
    /// <summary>No answer in time: the signal may or may not have been sent.</summary>
    TimedOut,
    Unavailable,
    Failed
}

public enum RemoteProcessState
{
    Running,
    Ended,
    /// <summary>The PID is in use by a process with another start time.</summary>
    Replaced,
    Unknown
}

/// <summary>
/// Ends a remote process with SIGTERM without a window for PID reuse: one remote call opens a pidfd,
/// compares the start time of the process behind it and signals only through that pidfd.
/// Needs python3 3.9 or newer on Linux 5.3 or newer; nothing is uploaded to the host.
/// </summary>
public static class LinuxProcessControl
{
    public const int ExitNoProcess = 44;
    public const int ExitNoPython = 50;
    public const int ExitPythonTooOld = 51;
    public const int ExitPidfdUnavailable = 52;
    public const int ExitGone = 53;
    public const int ExitIdentityChanged = 54;
    public const int ExitPermissionDenied = 55;
    public const int ExitStillRunning = 56;
    public const int ExitError = 57;

    /// <summary>Starts the line that carries the script's own verdict, apart from anything a login shell prints.</summary>
    public const string Marker = "T-PROC ";

    /// <summary>How long the script waits for the signalled process to exit before it reports "still running".</summary>
    public const int ExitWaitMilliseconds = 2000;

    // The script must not contain a single quote: it travels as one single-quoted sh argument.
    // The PID and the start time arrive as arguments, so the script text is the same for every process.
    internal const string PythonScript = """
        import os, select, signal, sys

        def done(code, word):
            sys.stdout.write("T-PROC " + word + "\n")
            sys.stdout.flush()
            sys.exit(code)

        def start_time(pid):
            with open("/proc/%d/stat" % pid, "rb") as stat:
                data = stat.read()
            # Field 22; the fields after the process name start behind the last parenthesis.
            return int(data[data.rindex(b")") + 1:].split()[19])

        def main():
            if not (hasattr(os, "pidfd_open") and hasattr(signal, "pidfd_send_signal")):
                done(51, "old-python")
            try:
                # Signal 0 to this script itself: exercises both system calls and delivers nothing.
                own = os.pidfd_open(os.getpid())
                signal.pidfd_send_signal(own, 0)
                os.close(own)
            except OSError:
                done(52, "no-pidfd")
            if sys.argv[1] == "probe":
                done(0, "supported")

            pid, expected = int(sys.argv[2]), int(sys.argv[3])
            try:
                target = os.pidfd_open(pid)
            except ProcessLookupError:
                done(53, "gone")
            # The pidfd is open first: a start time read now that equals the one seen earlier can
            # only belong to the process the pidfd refers to, even if the PID is reused later.
            try:
                actual = start_time(pid)
            except (FileNotFoundError, ProcessLookupError):
                done(53, "gone")
            if actual != expected:
                done(54, "changed")
            try:
                signal.pidfd_send_signal(target, signal.SIGTERM)
            except ProcessLookupError:
                done(53, "gone")
            except PermissionError:
                done(55, "denied")
            try:
                waiter = select.poll()
                waiter.register(target, select.POLLIN)
                exited = bool(waiter.poll(int(sys.argv[4])))
            except OSError:
                exited = False
            done(0 if exited else 56, "ended" if exited else "running")

        try:
            main()
        except Exception:
            done(57, "error")
        """;

    /// <summary>Checks python3, both pidfd functions and the system calls. Sends no signal to any other process.</summary>
    public static string ProbeCommand { get; } = Command("probe");

    /// <summary>
    /// Sends SIGTERM to the process with this PID if its start time (clock ticks since boot, field 22
    /// of /proc/PID/stat) still matches, then waits briefly for it to exit.
    /// </summary>
    public static string EndCommand(int pid, long startTime)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pid);
        // Zero is a real start time: processes started in the first clock tick after boot have it.
        ArgumentOutOfRangeException.ThrowIfNegative(startTime);
        return Command(string.Create(CultureInfo.InvariantCulture, $"end {pid} {startTime} {ExitWaitMilliseconds}"));
    }

    /// <summary>Reads /proc/PID/stat once (exit 44: no such process). Sends nothing.</summary>
    public static string StateCommand(int pid)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pid);
        return LinuxSystemMetrics.InPosixShell(string.Create(CultureInfo.InvariantCulture, $"""
            if [ ! -r /proc/stat ]; then exit 42; fi
            cat /proc/{pid}/stat 2>/dev/null || exit {ExitNoProcess}
            """));
    }

    public static ProcessSignalSupport ParseProbe(SshCommandResult result)
    {
        if (result.Outcome != SshCommandOutcome.Completed) return ProcessSignalSupport.Unknown;
        return (result.ExitStatus, ReadMarker(result.Output)) switch
        {
            (0, "supported") => ProcessSignalSupport.Supported,
            (ExitNoPython, "no-python") => ProcessSignalSupport.NoPython,
            (ExitPythonTooOld, "old-python") => ProcessSignalSupport.PythonTooOld,
            (ExitPidfdUnavailable, "no-pidfd") => ProcessSignalSupport.PidfdUnavailable,
            _ => ProcessSignalSupport.Unknown
        };
    }

    /// <summary>
    /// Maps the script's exit status to an outcome. The marker line has to agree with it, so an exit
    /// status that came from the shell or from Python itself never counts as an answer.
    /// </summary>
    public static EndProcessOutcome ParseEnd(SshCommandResult result)
    {
        switch (result.Outcome)
        {
            case SshCommandOutcome.TimedOut: return EndProcessOutcome.TimedOut;
            case SshCommandOutcome.Unavailable: return EndProcessOutcome.Unavailable;
            case SshCommandOutcome.OutputLimitExceeded: return EndProcessOutcome.Failed;
        }
        return (result.ExitStatus, ReadMarker(result.Output)) switch
        {
            (0, "ended") => EndProcessOutcome.Ended,
            (ExitStillRunning, "running") => EndProcessOutcome.StillRunning,
            (ExitPermissionDenied, "denied") => EndProcessOutcome.PermissionDenied,
            (ExitGone, "gone") => EndProcessOutcome.Gone,
            (ExitIdentityChanged, "changed") => EndProcessOutcome.IdentityChanged,
            (ExitNoPython, "no-python") or (ExitPythonTooOld, "old-python") or (ExitPidfdUnavailable, "no-pidfd") =>
                EndProcessOutcome.Unsupported,
            _ => EndProcessOutcome.Failed
        };
    }

    /// <summary>Compares the answer of <see cref="StateCommand"/> with the process that was selected.</summary>
    public static RemoteProcessState ParseState(SshCommandResult result, RemoteProcess expected)
    {
        if (result.Outcome != SshCommandOutcome.Completed) return RemoteProcessState.Unknown;
        if (result.ExitStatus == ExitNoProcess) return RemoteProcessState.Ended;
        if (result.ExitStatus != 0 || LinuxSystemMetrics.ParseStat(result.Output.Trim(), "", 1) is not { } actual ||
            actual.Pid != expected.Pid)
            return RemoteProcessState.Unknown;
        if (actual.StartTime != expected.StartTime) return RemoteProcessState.Replaced;
        // A zombie (Z) or dead (X) process has exited and only waits for its parent to collect it.
        return actual.State is "Z" or "X" ? RemoteProcessState.Ended : RemoteProcessState.Running;
    }

    private static string Command(string arguments) => LinuxSystemMetrics.InPosixShell($"""
        if ! command -v python3 >/dev/null 2>&1; then echo "T-PROC no-python"; exit {ExitNoPython}; fi
        exec python3 -I -c '{PythonScript}' {arguments}
        """);

    private static string? ReadMarker(string output)
    {
        string? word = null;
        foreach (var line in output.Split('\n'))
        {
            var text = line.TrimEnd('\r');
            if (text.StartsWith(Marker, StringComparison.Ordinal)) word = text[Marker.Length..];
        }
        return word;
    }
}
