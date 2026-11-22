namespace T.Services;

public enum CommandSuggestionKind { History, Command }

/// <summary>A complete command line the user can take over; it always starts with the typed text.</summary>
public sealed record CommandSuggestion(string Text, CommandSuggestionKind Kind);

/// <summary>
/// Suggests command lines from the shell history and the commands available on the server.
/// Only prefix matches are offered, so accepting a suggestion only has to type the missing
/// rest - the line the user already typed is never rewritten. Not thread-safe.
/// </summary>
public sealed class CommandSuggestionEngine
{
    public const int MaxHistoryEntries = 5000;
    private const int MaxEntryLength = 1000;
    private const int MaxCommandLength = 100;

    // Oldest first, without duplicates (a repeated command moves to the end).
    private readonly List<string> _history = [];
    private string[] _commands = [];

    public int HistoryCount => _history.Count;
    public int CommandCount => _commands.Length;

    /// <summary>
    /// Replaces the loaded history (oldest first). Commands run in this session so far stay
    /// the most recent ones.
    /// </summary>
    public void SetHistory(IEnumerable<string> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var combined = entries.Concat(_history).ToList();
        _history.Clear();

        // Keep the last occurrence of every entry.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var newestFirst = new List<string>();
        for (int i = combined.Count - 1; i >= 0 && newestFirst.Count < MaxHistoryEntries; i--)
        {
            var entry = Normalize(combined[i]);
            if (entry != null && seen.Add(entry)) newestFirst.Add(entry);
        }
        newestFirst.Reverse();
        _history.AddRange(newestFirst);
    }

    /// <summary>Records a command the user just ran; it becomes the most recent suggestion.</summary>
    public void AddHistory(string command)
    {
        var entry = Normalize(command);
        if (entry == null) return;

        _history.Remove(entry);
        _history.Add(entry);
        if (_history.Count > MaxHistoryEntries) _history.RemoveAt(0);
    }

    /// <summary>Replaces the known command names (executables, builtins, aliases, ...).</summary>
    public void SetCommands(IEnumerable<string> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        _commands = commands
            .Select(c => c.Trim())
            .Where(IsCommandName)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// The most recent history entry that continues <paramref name="line"/>, or null.
    /// Used for the inline (ghost text) suggestion.
    /// </summary>
    public string? GetInlineSuggestion(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        for (int i = _history.Count - 1; i >= 0; i--)
        {
            var entry = _history[i];
            if (entry.Length > line.Length && entry.StartsWith(line, StringComparison.Ordinal))
                return entry;
        }
        return null;
    }

    /// <summary>
    /// Suggestions for the popup: matching history entries (most recent first), then - while the
    /// first word is typed - matching commands (shortest first). An empty line lists the recent history.
    /// </summary>
    public IReadOnlyList<CommandSuggestion> GetSuggestions(string line, int maxCount)
    {
        ArgumentNullException.ThrowIfNull(line);
        var result = new List<CommandSuggestion>();
        if (maxCount <= 0) return result;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = _history.Count - 1; i >= 0 && result.Count < maxCount; i--)
        {
            var entry = _history[i];
            if (entry.Length > line.Length && entry.StartsWith(line, StringComparison.Ordinal) && seen.Add(entry))
                result.Add(new CommandSuggestion(entry, CommandSuggestionKind.History));
        }

        // Command names only complete the first word.
        bool firstWord = line.Length > 0 && !line.Any(char.IsWhiteSpace);
        if (firstWord && result.Count < maxCount)
        {
            foreach (var command in CommandsStartingWith(line))
            {
                if (result.Count >= maxCount) break;
                if (command.Length > line.Length && seen.Add(command))
                    result.Add(new CommandSuggestion(command, CommandSuggestionKind.Command));
            }
        }

        return result;
    }

    private IEnumerable<string> CommandsStartingWith(string prefix)
    {
        int index = Array.BinarySearch(_commands, prefix, StringComparer.Ordinal);
        if (index < 0) index = ~index;

        var matches = new List<string>();
        for (int i = index; i < _commands.Length && matches.Count < 500; i++)
        {
            if (!_commands[i].StartsWith(prefix, StringComparison.Ordinal)) break;
            matches.Add(_commands[i]);
        }

        // "gi" should offer "git" before "git-receive-pack".
        return matches.OrderBy(c => c.Length).ThenBy(c => c, StringComparer.Ordinal);
    }

    private static string? Normalize(string? entry)
    {
        if (entry == null) return null;
        entry = entry.TrimEnd();
        if (entry.Length == 0 || entry.Length > MaxEntryLength || string.IsNullOrWhiteSpace(entry)) return null;

        // Multi-line entries and control characters cannot be typed back safely.
        foreach (char c in entry)
        {
            if (char.IsControl(c) || c == '�') return null;
        }
        return entry;
    }

    private static bool IsCommandName(string name)
    {
        if (name.Length == 0 || name.Length > MaxCommandLength) return false;
        foreach (char c in name)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c) || c is '/' or '�') return false;
        }
        return true;
    }
}
