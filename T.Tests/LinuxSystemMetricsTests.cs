using T.Services;

namespace T.Tests;

public class LinuxSystemMetricsTests
{
    [Fact]
    public void HostMetrics_UseKiBAndOnlyTheFirstEightCpuCounters()
    {
        var data = LinuxSystemMetrics.ParseHost("""
            cpu 10 0 10 80 10 0 0 0 300 400
            MemTotal: 8192 kB
            MemAvailable: 2048 kB
            ==DF==
            Filesystem 1024-blocks Used Available Capacity Mounted on
            /dev/sda 4096 2048 1536 58% /
            """);

        Assert.Equal((110L, 90L), data.Cpu);
        Assert.Equal((6L * 1024 * 1024, 8L * 1024 * 1024), data.Memory);
        Assert.Equal((58d, 1536L * 1024, 4096L * 1024), data.Disk);
        Assert.Null(LinuxSystemMetrics.ParseHost("MemTotal: 8192 kB").Memory);
    }

    [Fact]
    public void CounterDeltas_ExcludeNewRemovedAndResetInterfaces()
    {
        var previous = LinuxSystemMetrics.ParseHost("""
            cpu 20 0 10 80 10 0 0 0
            eth0: 100 0 0 0 0 0 0 0 200 0 0 0 0 0 0 0
            eth1: 1000 0 0 0 0 0 0 0 2000 0 0 0 0 0 0 0
            eth2: 9999 0 0 0 0 0 0 0 9999 0 0 0 0 0 0 0
            """);
        var current = LinuxSystemMetrics.ParseHost("""
            cpu 10 0 10 40 10 0 0 0
            eth0: 180 0 0 0 0 0 0 0 240 0 0 0 0 0 0 0
            eth1: 10 0 0 0 0 0 0 0 20 0 0 0 0 0 0 0
            eth3: 8000 0 0 0 0 0 0 0 4000 0 0 0 0 0 0 0
            """);

        Assert.Null(LinuxSystemMetrics.CpuPercent(previous, current));
        Assert.Equal((40d, 20d), LinuxSystemMetrics.NetworkRate(previous, current, 2));
    }

    [Fact]
    public void ProcessRows_ParseParenAndNewlineNames_AndFlagPartialOutput()
    {
        var sample = LinuxSystemMetrics.ParseProcesses(
            "100 4096\0root\t" + Stat("a)\nb", 930) + "\0!limit\0broken", outputTruncated: true);
        Assert.NotNull(sample);

        var (rows, ticks, incomplete) = sample.Value;
        var process = Assert.Single(rows);
        Assert.Equal(100L, ticks);
        Assert.True(incomplete);
        Assert.Equal(321, process.Pid);
        Assert.Equal(930L, process.StartTime);
        Assert.Equal("a) b", process.Name);
        Assert.Equal("root", process.User);
        Assert.Equal(40L, process.CpuTicks);
        Assert.Equal(8192L, process.ResidentBytes);
    }

    [Fact]
    public void ProcessDetails_DoNotPublishAnExitedOrReusedPid()
    {
        var process = new RemoteProcess(321, 930, "", "", "R", 40, 2);
        string original = Stat("worker", 930);
        string replacement = Stat("worker", 931);
        Assert.Equal("/bin/worker --run",
            LinuxSystemMetrics.ParseCommandLine(original + "\0/bin/worker\0--run\0\0" + original, process));
        Assert.Null(LinuxSystemMetrics.ParseCommandLine(original + "\0/bin/worker\0\0" + replacement, process));
    }

    [Theory]
    [MemberData(nameof(Scripts))]
    public void RemoteScripts_RunInShWhateverTheLoginShellIs(string command)
    {
        // One single-quoted argument for the login shell (bash, fish, tcsh): base64 contains no quotes,
        // backslashes or line breaks.
        Assert.StartsWith("sh -c 'eval \"$(printf %s ", command, StringComparison.Ordinal);
        Assert.EndsWith(" | base64 -d)\"'", command, StringComparison.Ordinal);
        Assert.Equal(2, command.Count(c => c == '\''));

        var encoded = command["sh -c 'eval \"$(printf %s ".Length..command.IndexOf(" | base64", StringComparison.Ordinal)];
        var script = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        Assert.DoesNotContain('\r', script); // a CR would break "fi", "exit 42", ... in sh
        Assert.Contains("/proc", script, StringComparison.Ordinal);
    }

    public static TheoryData<string> Scripts() =>
        [LinuxSystemMetrics.HostCommand, LinuxSystemMetrics.ProcessCommand, LinuxSystemMetrics.ProcessDetailsCommand(1)];

    [Fact]
    public void InPosixShell_NormalizesWindowsLineEndings()
    {
        var command = LinuxSystemMetrics.InPosixShell("echo a\r\necho b\r\n");
        var encoded = command["sh -c 'eval \"$(printf %s ".Length..command.IndexOf(" | base64", StringComparison.Ordinal)];
        Assert.Equal("echo a\necho b\n", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
    }

    private static string Stat(string name, int start) =>
        $"321 ({name}) R 1 2 3 4 5 6 7 8 9 10 30 10 0 0 0 0 1 0 {start} 4096 2";
}
