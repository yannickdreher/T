using System.Text;

namespace T.Services;

/// <summary>
/// Shell integration for command suggestions: a small bash script that wraps the prompt in
/// OSC 133 markers, plus the commands that read the history and the available commands
/// from the server. All commands run with <c>sh -c '...'</c> so they work whatever the
/// login shell is (bash, zsh, fish, csh), and contain no single quotes or "!" themselves.
/// </summary>
public static class ShellIntegration
{
    /// <summary>Where the script is stored on the server (in the user's cache folder).</summary>
    public const string ScriptPath = "~/.cache/t/shell-integration.bash";

    private const string ShellPrefix = "T-SHELL:";
    private const string SectionPrefix = "==> T:";
    private const string SectionSuffix = " <==";

    /// <summary>
    /// The bash script. Only loaded for the current session (nothing is added to startup files).
    /// It removes the loader line from the history, erases its echo, and appends a prompt hook
    /// that keeps PS1 wrapped in "133;A" (prompt starts) and "133;B" (input starts). PS0 adds
    /// "133;C" (command runs) on bash 4.4+.
    /// </summary>
    internal static readonly string BashScript = """
        # Shell integration of the T SSH client. T loads this file into the current session
        # only: it marks prompt and command input (OSC 133) for T's command suggestions.
        if [[ -n "${BASH_VERSION:-}" && -z "${__t_si:-}" && $- == *i* ]]; then
        __t_si=1

        # The line that loaded this file does not belong in the history.
        if [[ -o history ]]; then
          case ":${HISTCONTROL:-}:" in
            *:ignorespace:*|*:ignoreboth:*) ;;
            *) read -r __t_n __t_x <<<"$(HISTTIMEFORMAT= builtin history 1)"
               [[ $__t_n =~ ^[0-9]+$ ]] && builtin history -d "$__t_n" ;;
          esac
        fi

        # Erase the echo of the loader line and the old prompt; the new prompt replaces them.
        __t_rows=${1:-0}
        [[ $__t_rows =~ ^[0-9]+$ ]] || __t_rows=0
        __t_nl=$'\n'
        __t_p=
        eval '__t_p=${PS1@P}' 2>/dev/null
        __t_p=${__t_p//[!$__t_nl]/}
        __t_rows=$(( __t_rows + ${#__t_p} ))
        (( __t_rows > 0 )) && printf '\e[%dF\e[J' "$__t_rows"

        __t_precmd() {
          local s=$? a='\[\e]133;A\a\]' b='\[\e]133;B\a\]' p
          if [[ $PS1 != "$a"*"$b" ]]; then
            p=${PS1//"$a"/}
            p=${p//"$b"/}
            PS1=$a$p$b
          fi
          return $s
        }

        # Run last, after prompt themes have built PS1.
        if [[ "$(declare -p PROMPT_COMMAND 2>/dev/null)" == "declare -a"* ]]; then
          PROMPT_COMMAND+=(__t_precmd)
        else
          PROMPT_COMMAND=${PROMPT_COMMAND:+$PROMPT_COMMAND$'\n'}__t_precmd
        fi

        if (( BASH_VERSINFO[0] > 4 || (BASH_VERSINFO[0] == 4 && BASH_VERSINFO[1] >= 4) )); then
          [[ ${PS0:-} == *'133;C'* ]] || PS0=${PS0:-}'\e]133;C\a'
        fi

        unset __t_n __t_x __t_rows __t_nl __t_p
        fi
        """.ReplaceLineEndings("\n") + "\n";

    /// <summary>
    /// Writes the script to <see cref="ScriptPath"/> (readable only by the user) and prints
    /// the name of the login shell; see <see cref="IsBashLoginShell"/>.
    /// </summary>
    public static string InstallCommand { get; } =
        "sh -c 'umask 077 && mkdir -p \"$HOME/.cache/t\" && printf %s " +
        Convert.ToBase64String(Encoding.UTF8.GetBytes(BashScript)) +
        " | base64 -d > \"$HOME/.cache/t/shell-integration.bash\" && echo \"" + ShellPrefix + "${SHELL##*/}\"'";

    /// <summary>The install command succeeded and the login shell is bash.</summary>
    public static bool IsBashLoginShell(string? installOutput)
    {
        if (installOutput == null) return false;
        foreach (var line in installOutput.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(ShellPrefix, StringComparison.Ordinal))
                return trimmed[ShellPrefix.Length..] == "bash";
        }
        return false;
    }

    /// <summary>
    /// The line typed into the shell to load the script (without Enter). The leading space keeps
    /// it out of the history where HISTCONTROL allows; the script removes it otherwise. The
    /// argument is the number of screen rows the prompt line and this echo occupy, so the
    /// script can erase them: <paramref name="promptColumn"/> is the cursor column at the prompt.
    /// </summary>
    public static string GetLoaderLine(int promptColumn, int terminalColumns)
    {
        terminalColumns = Math.Max(1, terminalColumns);
        promptColumn = Math.Clamp(promptColumn, 0, terminalColumns);

        // The row count is part of the line, so its digits change the length; settle it.
        int rows = 1;
        string line = "";
        for (int i = 0; i < 4; i++)
        {
            line = $" [ -n \"$BASH_VERSION\" ] && . {ScriptPath} {rows}";
            int needed = (promptColumn + line.Length) / terminalColumns + 1;
            if (needed == rows) break;
            rows = needed;
        }
        return line;
    }

    /// <summary>
    /// Prints the last lines of the bash, zsh and fish history files, each after a section header
    /// (see <see cref="ParseHistory"/>).
    /// </summary>
    public static string HistoryCommand { get; } =
        "sh -c '" +
        "f=\"$HOME/.bash_history\"; [ -r \"$f\" ] && { echo \"" + SectionPrefix + "bash" + SectionSuffix + "\"; tail -n 3000 \"$f\"; }; " +
        "f=\"$HOME/.zsh_history\"; [ -r \"$f\" ] && { echo \"" + SectionPrefix + "zsh" + SectionSuffix + "\"; tail -n 3000 \"$f\"; }; " +
        "f=\"${XDG_DATA_HOME:-$HOME/.local/share}/fish/fish_history\"; [ -r \"$f\" ] && { echo \"" + SectionPrefix + "fish" + SectionSuffix + "\"; tail -n 9000 \"$f\"; }; " +
        "exit 0'";

    /// <summary>Lists the commands bash knows (PATH, builtins, keywords); without bash the PATH folders.</summary>
    public static string CommandsCommand { get; } =
        "sh -c 'if command -v bash >/dev/null 2>&1; then bash -c \"compgen -c\"; " +
        "else IFS=:; for d in $PATH; do ls -1 \"$d\" 2>/dev/null; done; fi | sort -u | head -n 20000; exit 0'";

    /// <summary>Parses the output of <see cref="HistoryCommand"/> into commands, oldest first per file.</summary>
    public static List<string> ParseHistory(string? output)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(output)) return result;

        string format = "bash";
        var continued = false; // zsh: an entry ending in "\" goes on in the next line
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            if (line.StartsWith(SectionPrefix, StringComparison.Ordinal) && line.EndsWith(SectionSuffix, StringComparison.Ordinal))
            {
                format = line[SectionPrefix.Length..^SectionSuffix.Length];
                continued = false;
                continue;
            }

            switch (format)
            {
                case "zsh":
                    {
                        if (continued)
                        {
                            // Multi-line entries cannot be suggested; skip the rest of it.
                            continued = line.EndsWith('\\');
                            continue;
                        }
                        var entry = line;
                        // Extended history: ": 1700000000:0;command"
                        if (entry.StartsWith(": ", StringComparison.Ordinal))
                        {
                            int semicolon = entry.IndexOf(';', StringComparison.Ordinal);
                            if (semicolon > 0) entry = entry[(semicolon + 1)..];
                        }
                        if (entry.EndsWith('\\'))
                        {
                            continued = true;
                            continue;
                        }
                        result.Add(entry);
                        break;
                    }
                case "fish":
                    // "- cmd: command" followed by indented "when:" / "paths:" lines
                    if (line.StartsWith("- cmd: ", StringComparison.Ordinal))
                        result.Add(UnescapeFish(line["- cmd: ".Length..]));
                    break;
                default:
                    // HISTTIMEFORMAT timestamps are stored as "#1700000000" lines.
                    if (line.Length > 1 && line[0] == '#' && line.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0)
                        continue;
                    result.Add(line);
                    break;
            }
        }

        result.RemoveAll(string.IsNullOrWhiteSpace);
        return result;
    }

    /// <summary>Splits the output of <see cref="CommandsCommand"/>.</summary>
    public static List<string> ParseCommands(string? output) =>
        string.IsNullOrEmpty(output)
            ? []
            : output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();

    private static string UnescapeFish(string value) =>
        value.Contains('\\', StringComparison.Ordinal)
            ? value.Replace("\\n", "\n", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal)
            : value;
}
