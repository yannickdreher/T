using System.Text;
using T.Abstractions;
using T.Services;

namespace T.Tests;

public class LinuxProcessControlTests
{
    private static readonly RemoteProcess Worker = new(321, 930, "worker", "build", "S", 40, 2);

    [Fact]
    public void EndCommand_PassesOnlyThePidTheStartTimeAndTheWaitAsArguments()
    {
        string script = Decode(LinuxProcessControl.EndCommand(321, 930));

        Assert.EndsWith("' end 321 930 2000", script, StringComparison.Ordinal);
        Assert.Contains("exec python3 -I -c '" + LinuxProcessControl.PythonScript.ReplaceLineEndings("\n") + "'", script, StringComparison.Ordinal);
        // The Python text is the only single-quoted argument, so it must not contain a quote itself.
        Assert.Equal(2, script.Count(c => c == '\''));
        Assert.DoesNotContain('\r', script);
        Assert.EndsWith("' end 2147483647 9223372036854775807 2000",
            Decode(LinuxProcessControl.EndCommand(int.MaxValue, long.MaxValue)), StringComparison.Ordinal);
        Assert.EndsWith("' end 1 0 2000", Decode(LinuxProcessControl.EndCommand(1, 0)), StringComparison.Ordinal);
    }

    [Fact]
    public void Commands_RejectValuesThatAreNotAProcess()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LinuxProcessControl.EndCommand(0, 930));
        Assert.Throws<ArgumentOutOfRangeException>(() => LinuxProcessControl.EndCommand(-1, 930));
        Assert.Throws<ArgumentOutOfRangeException>(() => LinuxProcessControl.EndCommand(321, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => LinuxProcessControl.StateCommand(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => LinuxProcessControl.StateCommand(-5));
    }

    [Fact]
    public void ProbeAndStateCommands_SendNoSignalToAnotherProcess()
    {
        string probe = Decode(LinuxProcessControl.ProbeCommand);
        Assert.EndsWith("' probe", probe, StringComparison.Ordinal);
        Assert.StartsWith("if ! command -v python3 >/dev/null 2>&1; then echo \"T-PROC no-python\"; exit 50; fi\n", probe, StringComparison.Ordinal);

        // The probe leaves the script before a target PID is read; the only signal before that is 0 to itself.
        string python = LinuxProcessControl.PythonScript;
        int probeExit = python.IndexOf("if sys.argv[1] == \"probe\":", StringComparison.Ordinal);
        Assert.True(probeExit > 0);
        Assert.Contains("signal.pidfd_send_signal(own, 0)", python[..probeExit], StringComparison.Ordinal);
        Assert.DoesNotContain("SIGTERM", python[..probeExit], StringComparison.Ordinal);
        Assert.DoesNotContain("sys.argv[2]", python[..probeExit], StringComparison.Ordinal);

        Assert.Equal("if [ ! -r /proc/stat ]; then exit 42; fi\ncat /proc/321/stat 2>/dev/null || exit 44",
            Decode(LinuxProcessControl.StateCommand(321)));
    }

    [Fact]
    public void Script_OpensThePidfdBeforeItComparesTheIdentity_AndSignalsOnlyThroughIt()
    {
        string python = LinuxProcessControl.PythonScript;
        int open = python.IndexOf("target = os.pidfd_open(pid)", StringComparison.Ordinal);
        int compare = python.IndexOf("actual = start_time(pid)", StringComparison.Ordinal);
        int signal = python.IndexOf("signal.pidfd_send_signal(target, signal.SIGTERM)", StringComparison.Ordinal);

        Assert.True(open > 0 && open < compare && compare < signal);
        Assert.DoesNotContain("os.kill", python, StringComparison.Ordinal);
        Assert.DoesNotContain("SIGKILL", python, StringComparison.Ordinal);
        Assert.DoesNotContain('\'', python);
    }

    [Theory]
    [InlineData(LinuxProcessControl.ExitPythonTooOld, "old-python")]
    [InlineData(LinuxProcessControl.ExitPidfdUnavailable, "no-pidfd")]
    [InlineData(LinuxProcessControl.ExitGone, "gone")]
    [InlineData(LinuxProcessControl.ExitIdentityChanged, "changed")]
    [InlineData(LinuxProcessControl.ExitPermissionDenied, "denied")]
    [InlineData(LinuxProcessControl.ExitError, "error")]
    [InlineData(0, "supported")]
    public void Script_UsesTheExitStatusesTheParserExpects(int exitStatus, string word)
    {
        Assert.Contains($"done({exitStatus}, \"{word}\")", LinuxProcessControl.PythonScript, StringComparison.Ordinal);
        Assert.Contains($"done(0 if exited else {LinuxProcessControl.ExitStillRunning}, \"ended\" if exited else \"running\")",
            LinuxProcessControl.PythonScript, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "T-PROC ended\n", EndProcessOutcome.Ended)]
    [InlineData(56, "T-PROC running\n", EndProcessOutcome.StillRunning)]
    [InlineData(55, "T-PROC denied\n", EndProcessOutcome.PermissionDenied)]
    [InlineData(53, "T-PROC gone\n", EndProcessOutcome.Gone)]
    [InlineData(54, "T-PROC changed\n", EndProcessOutcome.IdentityChanged)]
    [InlineData(50, "T-PROC no-python\n", EndProcessOutcome.Unsupported)]
    [InlineData(51, "T-PROC old-python\n", EndProcessOutcome.Unsupported)]
    [InlineData(52, "T-PROC no-pidfd\n", EndProcessOutcome.Unsupported)]
    [InlineData(57, "T-PROC error\n", EndProcessOutcome.Failed)]
    // A login shell may print before the script runs, and may use CRLF.
    [InlineData(0, "Welcome to build-01\r\nT-PROC ended\r\n", EndProcessOutcome.Ended)]
    // An exit status without the matching marker did not come from the script.
    [InlineData(0, "", EndProcessOutcome.Failed)]
    [InlineData(0, "T-PROC supported\n", EndProcessOutcome.Failed)]
    [InlineData(0, "T-PROC gone\n", EndProcessOutcome.Failed)]
    [InlineData(55, "", EndProcessOutcome.Failed)]
    [InlineData(1, "Traceback (most recent call last):\n", EndProcessOutcome.Failed)]
    [InlineData(127, "sh: python3: not found\n", EndProcessOutcome.Failed)]
    [InlineData(null, "T-PROC ended\n", EndProcessOutcome.Failed)]
    public void EndResult_NeedsExitStatusAndMarkerToAgree(int? exitStatus, string output, EndProcessOutcome expected) =>
        Assert.Equal(expected, LinuxProcessControl.ParseEnd(new(SshCommandOutcome.Completed, exitStatus, output)));

    [Fact]
    public void EndResult_KeepsTimeoutAndLostChannelApartFromAnAnswer()
    {
        // Output that arrived before the deadline does not prove what happened after it.
        Assert.Equal(EndProcessOutcome.TimedOut,
            LinuxProcessControl.ParseEnd(new(SshCommandOutcome.TimedOut, null, "T-PROC ended\n")));
        Assert.Equal(EndProcessOutcome.Unavailable, LinuxProcessControl.ParseEnd(new(SshCommandOutcome.Unavailable)));
        Assert.Equal(EndProcessOutcome.Failed,
            LinuxProcessControl.ParseEnd(new(SshCommandOutcome.OutputLimitExceeded, null, "T-PROC ended\n")));
    }

    [Theory]
    [InlineData(SshCommandOutcome.Completed, 0, "T-PROC supported\n", ProcessSignalSupport.Supported)]
    [InlineData(SshCommandOutcome.Completed, 0, "motd\nT-PROC supported\n", ProcessSignalSupport.Supported)]
    [InlineData(SshCommandOutcome.Completed, 50, "T-PROC no-python\n", ProcessSignalSupport.NoPython)]
    [InlineData(SshCommandOutcome.Completed, 51, "T-PROC old-python\n", ProcessSignalSupport.PythonTooOld)]
    [InlineData(SshCommandOutcome.Completed, 52, "T-PROC no-pidfd\n", ProcessSignalSupport.PidfdUnavailable)]
    [InlineData(SshCommandOutcome.Completed, 57, "T-PROC error\n", ProcessSignalSupport.Unknown)]
    [InlineData(SshCommandOutcome.Completed, 0, "", ProcessSignalSupport.Unknown)]
    [InlineData(SshCommandOutcome.Completed, 0, "T-PROC ended\n", ProcessSignalSupport.Unknown)]
    [InlineData(SshCommandOutcome.Completed, 127, "", ProcessSignalSupport.Unknown)]
    [InlineData(SshCommandOutcome.TimedOut, null, "T-PROC supported\n", ProcessSignalSupport.Unknown)]
    [InlineData(SshCommandOutcome.Unavailable, null, "", ProcessSignalSupport.Unknown)]
    public void ProbeResult_IsSupportedOnlyWhenTheScriptSaysSo(
        SshCommandOutcome outcome, int? exitStatus, string output, ProcessSignalSupport expected) =>
        Assert.Equal(expected, LinuxProcessControl.ParseProbe(new(outcome, exitStatus, output)));

    [Fact]
    public void State_TellsRunningEndedAndReusedPidApart()
    {
        Assert.Equal(RemoteProcessState.Running, State(0, Stat(321, "worker", "S", 930) + "\n"));
        Assert.Equal(RemoteProcessState.Running, State(0, Stat(321, "a) Z (b", "R", 930)));
        Assert.Equal(RemoteProcessState.Ended, State(0, Stat(321, "worker", "Z", 930)));
        Assert.Equal(RemoteProcessState.Ended, State(LinuxProcessControl.ExitNoProcess, ""));
        Assert.Equal(RemoteProcessState.Replaced, State(0, Stat(321, "worker", "S", 931)));
        Assert.Equal(RemoteProcessState.Unknown, State(0, Stat(322, "worker", "S", 930)));
        Assert.Equal(RemoteProcessState.Unknown, State(0, "garbage"));
        Assert.Equal(RemoteProcessState.Unknown, State(42, ""));
        Assert.Equal(RemoteProcessState.Unknown,
            LinuxProcessControl.ParseState(new(SshCommandOutcome.TimedOut, null, Stat(321, "worker", "S", 930)), Worker));
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public void Commands_RunInShWhateverTheLoginShellIs(string command)
    {
        Assert.StartsWith("sh -c 'eval \"$(printf %s ", command, StringComparison.Ordinal);
        Assert.EndsWith(" | base64 -d)\"'", command, StringComparison.Ordinal);
        Assert.Equal(2, command.Count(c => c == '\''));
    }

    public static TheoryData<string> Commands() =>
        [LinuxProcessControl.ProbeCommand, LinuxProcessControl.EndCommand(321, 930), LinuxProcessControl.StateCommand(321)];

    private static RemoteProcessState State(int exitStatus, string output) =>
        LinuxProcessControl.ParseState(new(SshCommandOutcome.Completed, exitStatus, output), Worker);

    private static string Decode(string command)
    {
        const string prefix = "sh -c 'eval \"$(printf %s ";
        var encoded = command[prefix.Length..command.IndexOf(" | base64", StringComparison.Ordinal)];
        return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
    }

    private static string Stat(int pid, string name, string state, int start) =>
        $"{pid} ({name}) {state} 1 2 3 4 5 6 7 8 9 10 30 10 0 0 0 0 1 0 {start} 4096 2";
}
