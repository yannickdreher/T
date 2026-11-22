using System.Text;
using T.Services;
using T.VT;

namespace T.Tests;

public class ShellPromptMarkerTests
{
    private const string Prompt = "\x1b]133;A\auser@host:~$ \x1b]133;B\a";

    [Fact]
    public void CommandLine_IsTextBetweenInputStartAndCursor()
    {
        var vt = new VirtualTerminal(40, 5);
        vt.Feed(Prompt + "git sta");

        Assert.True(vt.HasShellIntegration);
        Assert.True(vt.TryGetCommandLine(out var line));
        Assert.Equal("git sta", line.Text);
        Assert.Equal(0, line.StartRow);
        Assert.Equal("user@host:~$ ".Length, line.StartColumn);
        Assert.True(line.CursorAtEnd);
    }

    [Fact]
    public void CommandLine_WithoutMarkers_IsUnknown()
    {
        var vt = new VirtualTerminal(40, 5);
        vt.Feed("user@host:~$ git");

        Assert.False(vt.HasShellIntegration);
        Assert.False(vt.TryGetCommandLine(out _));
    }

    [Fact]
    public void CommandLine_FollowsWrappedInput()
    {
        var vt = new VirtualTerminal(20, 5);
        vt.Feed(Prompt + "echo 0123456789abcdefghij");

        Assert.True(vt.TryGetCommandLine(out var line));
        Assert.Equal("echo 0123456789abcdefghij", line.Text);
        Assert.True(line.CursorAtEnd);
    }

    [Fact]
    public void CommandLine_CursorInTheMiddle_IsNotAtEnd()
    {
        var vt = new VirtualTerminal(40, 5);
        vt.Feed(Prompt + "git status\x1b[3D");

        Assert.True(vt.TryGetCommandLine(out var line));
        Assert.Equal("git sta", line.Text);
        Assert.False(line.CursorAtEnd);
    }

    [Fact]
    public void CommandLine_EndsWhenTheCommandIsSubmitted()
    {
        var vt = new VirtualTerminal(40, 5);
        vt.Feed(Prompt + "ls");

        vt.NotifyCommandSubmitted();

        Assert.Equal(ShellPromptState.Executing, vt.PromptState);
        Assert.False(vt.TryGetCommandLine(out _));
    }

    [Fact]
    public void CommandLine_EndsWithTheCommandStartedMarker()
    {
        var vt = new VirtualTerminal(40, 5);
        vt.Feed(Prompt + "ls\r\n\x1b]133;C\afile.txt\r\n");

        Assert.False(vt.TryGetCommandLine(out _));

        vt.Feed(Prompt + "c");
        Assert.True(vt.TryGetCommandLine(out var line));
        Assert.Equal("c", line.Text);
        Assert.Equal(2, line.StartRow);
    }

    [Fact]
    public void CommandLine_StaysAnchoredWhileTheScreenScrolls()
    {
        var vt = new VirtualTerminal(40, 3);
        vt.Feed("a\r\nb\r\n" + Prompt + "ab");
        Assert.True(vt.TryGetCommandLine(out var before));
        Assert.Equal(2, before.StartRow);

        // The shell wraps a long line at the bottom of the screen: the prompt row moves up.
        vt.Feed(new string('x', 40));

        Assert.True(vt.TryGetCommandLine(out var after));
        Assert.Equal(1, after.StartRow);
        Assert.StartsWith("ab", after.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void CommandLine_ScrolledOffTheScreen_IsUnknown()
    {
        var vt = new VirtualTerminal(40, 3);
        vt.Feed(Prompt + "x\r\n1\r\n2\r\n3\r\n");

        Assert.False(vt.TryGetCommandLine(out _));
    }

    [Fact]
    public void CommandLine_OnTheAlternateScreen_IsUnknown()
    {
        var vt = new VirtualTerminal(40, 5);
        vt.Feed(Prompt + "vim");
        vt.Feed("\x1b[?1049h");

        Assert.False(vt.TryGetCommandLine(out _));

        vt.Feed("\x1b[?1049l");
        Assert.True(vt.TryGetCommandLine(out var line));
        Assert.Equal("vim", line.Text);
    }

    [Fact]
    public void CommandLine_ReadsWideCharactersOnce()
    {
        var vt = new VirtualTerminal(40, 5);
        vt.Feed(Prompt + "echo 日本");

        Assert.True(vt.TryGetCommandLine(out var line));
        Assert.Equal("echo 日本", line.Text);
    }

    [Fact]
    public void ResetShellIntegration_ForgetsThePreviousShell()
    {
        var vt = new VirtualTerminal(40, 5);
        vt.Feed(Prompt);

        vt.ResetShellIntegration();

        Assert.False(vt.HasShellIntegration);
        Assert.False(vt.TryGetCommandLine(out _));
    }

    [Fact]
    public void PromptMarkers_AreNotPrinted()
    {
        var vt = new VirtualTerminal(40, 5);
        vt.Feed(Prompt + "ls");

        Assert.Equal("user@host:~$ ls", TerminalText.Row(vt, 0));
    }
}

public class CommandSuggestionEngineTests
{
    [Fact]
    public void InlineSuggestion_IsTheMostRecentMatchingEntry()
    {
        var engine = new CommandSuggestionEngine();
        engine.SetHistory(["git status", "git stash", "ls"]);

        Assert.Equal("git stash", engine.GetInlineSuggestion("git st"));
        Assert.Null(engine.GetInlineSuggestion("git stash"));   // nothing to add
        Assert.Null(engine.GetInlineSuggestion("   "));
        Assert.Null(engine.GetInlineSuggestion("Git"));          // case-sensitive like the shell
    }

    [Fact]
    public void AddHistory_MovesARepeatedCommandToTheFront()
    {
        var engine = new CommandSuggestionEngine();
        engine.SetHistory(["git status", "git stash"]);

        engine.AddHistory("git status");

        Assert.Equal("git status", engine.GetInlineSuggestion("git st"));
        Assert.Equal(2, engine.HistoryCount);
    }

    [Fact]
    public void SetHistory_KeepsCommandsOfThisSessionMostRecent()
    {
        var engine = new CommandSuggestionEngine();
        engine.AddHistory("make test");

        engine.SetHistory(["make build", "make test", "make clean"]);

        Assert.Equal("make test", engine.GetInlineSuggestion("make "));
        Assert.Equal(3, engine.HistoryCount);
    }

    [Fact]
    public void History_RejectsEntriesThatCannotBeTypedBack()
    {
        var engine = new CommandSuggestionEngine();
        engine.SetHistory(["echo a\nb", "echo \x1b[31m", "echo ok", "", "   "]);

        Assert.Equal(1, engine.HistoryCount);
    }

    [Fact]
    public void Suggestions_ListHistoryFirstThenCommandsForTheFirstWord()
    {
        var engine = new CommandSuggestionEngine();
        engine.SetHistory(["gitk", "git log", "git pull"]);
        engine.SetCommands(["git-receive-pack", "git", "gimp", "grep", "gitk"]);

        var result = engine.GetSuggestions("gi", 10);

        Assert.Equal(
            ["git pull", "git log", "gitk", "git", "gimp", "git-receive-pack"],
            result.Select(s => s.Text));
        Assert.Equal(CommandSuggestionKind.History, result[0].Kind);
        Assert.Equal(CommandSuggestionKind.Command, result[3].Kind);
    }

    [Fact]
    public void Suggestions_ForArguments_ComeFromTheHistoryOnly()
    {
        var engine = new CommandSuggestionEngine();
        engine.SetHistory(["git log --oneline"]);
        engine.SetCommands(["git", "git-log"]);

        var result = engine.GetSuggestions("git l", 10);

        Assert.Equal(["git log --oneline"], result.Select(s => s.Text));
    }

    [Fact]
    public void Suggestions_ForAnEmptyLine_AreTheRecentHistory()
    {
        var engine = new CommandSuggestionEngine();
        engine.SetHistory(["a", "b", "c"]);

        Assert.Equal(["c", "b"], engine.GetSuggestions("", 2).Select(s => s.Text));
    }

    [Fact]
    public void Commands_SkipInvalidNames()
    {
        var engine = new CommandSuggestionEngine();
        engine.SetCommands(["ls", "ls", "a b", "/bin/ls", "", "lsof"]);

        Assert.Equal(2, engine.CommandCount);
    }
}

public class ShellIntegrationTests
{
    [Fact]
    public void ParseHistory_ReadsBashZshAndFish()
    {
        var output = string.Join('\n',
            "==> T:bash <==",
            "#1700000000",
            "ls -la",
            "git status",
            "==> T:zsh <==",
            ": 1700000001:0;docker ps",
            ": 1700000002:0;echo one\\",
            "two",
            "htop",
            "==> T:fish <==",
            "- cmd: kubectl get pods",
            "  when: 1700000003",
            "  paths:",
            "    - /tmp",
            "- cmd: echo a\\\\b",
            "");

        Assert.Equal(
            ["ls -la", "git status", "docker ps", "htop", "kubectl get pods", "echo a\\b"],
            ShellIntegration.ParseHistory(output));
    }

    [Fact]
    public void ParseHistory_WithoutOutput_IsEmpty()
    {
        Assert.Empty(ShellIntegration.ParseHistory(null));
        Assert.Empty(ShellIntegration.ParseHistory(""));
    }

    [Theory]
    [InlineData("T-SHELL:bash\n", true)]
    [InlineData("motd\nT-SHELL:bash\r\n", true)]
    [InlineData("T-SHELL:zsh\n", false)]
    [InlineData("T-SHELL:\n", false)]
    [InlineData(null, false)]
    public void IsBashLoginShell_ReadsTheInstallOutput(string? output, bool expected)
    {
        Assert.Equal(expected, ShellIntegration.IsBashLoginShell(output));
    }

    [Theory]
    [InlineData(10, 80)]
    [InlineData(70, 80)]
    [InlineData(0, 20)]
    [InlineData(79, 80)]
    public void LoaderLine_CountsTheRowsOfPromptAndEcho(int promptColumn, int columns)
    {
        var line = ShellIntegration.GetLoaderLine(promptColumn, columns);

        int rows = int.Parse(line[(line.LastIndexOf(' ') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal((promptColumn + line.Length) / columns + 1, rows);
        Assert.StartsWith(" ", line, StringComparison.Ordinal); // kept out of the history where possible
        Assert.Contains(ShellIntegration.ScriptPath, line, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallCommand_WritesTheScriptWithoutLineEndingProblems()
    {
        var command = ShellIntegration.InstallCommand;
        int start = command.IndexOf("printf %s ", StringComparison.Ordinal) + "printf %s ".Length;
        int end = command.IndexOf(' ', start);
        var script = Encoding.UTF8.GetString(Convert.FromBase64String(command[start..end]));

        Assert.Equal(ShellIntegration.BashScript, script);
        Assert.DoesNotContain('\r', script);
        Assert.Contains("133;A", script, StringComparison.Ordinal);
        Assert.Contains("133;B", script, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(RemoteCommands))]
    public void RemoteCommands_AreOneSingleQuotedShArgument(string command)
    {
        // Run by the login shell, whatever it is: one sh -c '...' without nested single quotes,
        // newlines or "!" (csh history expansion).
        Assert.StartsWith("sh -c '", command, StringComparison.Ordinal);
        Assert.EndsWith("'", command, StringComparison.Ordinal);
        Assert.Equal(2, command.Count(c => c == '\''));
        Assert.DoesNotContain('\n', command);
        Assert.DoesNotContain('!', command);
    }

    public static TheoryData<string> RemoteCommands() =>
        [ShellIntegration.InstallCommand, ShellIntegration.HistoryCommand, ShellIntegration.CommandsCommand];
}
