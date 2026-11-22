using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Input;
using Avalonia.Threading;
using T.Models;
using T.Services;
using T.UI.ViewModels;
using T.UI.Views.Components;
using T.VT;

namespace T.UI.Controls;

/// <summary>
/// Command suggestions at the shell prompt, like fish / VS Code:
/// <list type="bullet">
/// <item>the most recent matching history entry is shown dimmed after the cursor (→ or End accepts it,
/// Ctrl+→ accepts one word),</item>
/// <item>a popup lists further history entries and commands; it opens without a selection, so Enter,
/// Tab and ↑ keep working for the shell until ↓ moves into the list.</item>
/// </list>
/// Suggestions need the OSC 133 prompt markers (to know where the command line starts and that no
/// program runs). For bash the controller loads <see cref="ShellIntegration"/> at the first prompt.
/// </summary>
public sealed class CommandSuggestionController : IDisposable
{
    private const int MaxPopupItems = 8;
    private const int MinAutoPopupLength = 2;
    private static readonly TimeSpan IntegrationIdle = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan IntegrationWindow = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan TypingQuietTime = TimeSpan.FromSeconds(1);

    private readonly TerminalControl _terminal;
    private readonly CommandSuggestionPopup _popup;
    private readonly DispatcherTimer _integrationTimer;
    private SessionViewModel? _session;

    // Current command line (valid while _hasLine).
    private bool _hasLine;
    private ShellCommandLine _line;
    private string? _lastText;

    private string? _inline;                     // full suggested command line for the ghost text
    private List<CommandSuggestionItem> _items = [];
    private int _selected = -1;
    private bool _popupForced;                   // Ctrl+Space
    private bool _popupSuppressed;               // after ↑/↓ history navigation or accepting, until the user types
    private string? _popupDismissedFor;          // Esc: stays closed while the line is unchanged

    // Loading the bash integration at the first prompt.
    private bool _integrationPending;
    private DateTime _integrationDeadline;
    private DateTime _lastKeyTime;

    public CommandSuggestionController(TerminalControl terminal, CommandSuggestionPopup popup)
    {
        _terminal = terminal;
        _popup = popup;
        _popup.IsVisible = false;

        _terminal.ScreenUpdated += OnScreenUpdated;
        _terminal.KeyIntercept += OnKeyIntercept;
        _terminal.LostFocus += OnTerminalLostFocus;
        _popup.ItemClicked += Accept;

        _integrationTimer = new DispatcherTimer { Interval = IntegrationIdle };
        _integrationTimer.Tick += (_, _) =>
        {
            _integrationTimer.Stop();
            TryLoadShellIntegration();
        };
    }

    public void Attach(SessionViewModel session)
    {
        Detach();
        _session = session;
        session.ShellIntegrationAvailable += OnShellIntegrationAvailable;
        session.PropertyChanged += OnSessionPropertyChanged;
    }

    public void Detach()
    {
        if (_session == null) return;
        _session.ShellIntegrationAvailable -= OnShellIntegrationAvailable;
        _session.PropertyChanged -= OnSessionPropertyChanged;
        _session = null;
        _integrationPending = false;
        _integrationTimer.Stop();
        HideAll();
    }

    public void Dispose()
    {
        Detach();
        _terminal.ScreenUpdated -= OnScreenUpdated;
        _terminal.KeyIntercept -= OnKeyIntercept;
        _terminal.LostFocus -= OnTerminalLostFocus;
        _popup.ItemClicked -= Accept;
    }

    private bool Enabled => _session?.TerminalSettings.CommandSuggestions == true;

    private CommandSuggestionEngine? Engine => _session?.CommandSuggestions;

    // ── Session events ───────────────────────────────────────────────────

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SessionViewModel.ConnectionStatus) || _session == null) return;

        // A new shell starts: its prompt markers have to be seen (or loaded) again.
        if (_session.ConnectionStatus is ConnectionStatus.Connecting or ConnectionStatus.Reconnecting)
        {
            _terminal.Emulator?.ResetShellIntegration();
            _integrationPending = false;
            _integrationTimer.Stop();
            HideAll();
        }
    }

    private void OnShellIntegrationAvailable()
    {
        if (!Enabled) return;
        _integrationPending = true;
        _integrationDeadline = DateTime.UtcNow + IntegrationWindow;
        _integrationTimer.Stop();
        _integrationTimer.Start();
    }

    /// <summary>
    /// Types the loader line once the shell waits at its first prompt: output has been quiet for a
    /// moment, the cursor row looks like a prompt, nothing follows it and the user is not typing.
    /// </summary>
    private void TryLoadShellIntegration()
    {
        if (!_integrationPending) return;
        var vt = _terminal.Emulator;
        if (!Enabled || DateTime.UtcNow > _integrationDeadline || vt is { HasShellIntegration: true })
        {
            _integrationPending = false; // the shell already sends markers, or the chance has passed
            return;
        }

        if (vt == null || vt.IsAlternateScreen || DateTime.UtcNow - _lastKeyTime < TypingQuietTime || !IsAtPrompt(vt))
        {
            _integrationTimer.Start(); // try again
            return;
        }

        _integrationPending = false;
        _terminal.SendText(ShellIntegration.GetLoaderLine(vt.CursorColumn, vt.Width) + "\r");
    }

    private static bool IsAtPrompt(VirtualTerminal vt)
    {
        int row = vt.CursorRow;
        int col = Math.Min(vt.CursorColumn, vt.Width);

        for (int c = col; c < vt.Width; c++)
        {
            if (vt.GetCell(c, row).CodePoint is not (0 or ' ')) return false;
        }

        var sb = new StringBuilder();
        for (int c = 0; c < col; c++)
        {
            var cell = vt.GetCell(c, row);
            if (cell.IsWideContinuation) continue;
            vt.Graphemes.AppendText(sb, cell.CodePoint == 0 ? ' ' : cell.CodePoint);
        }

        var before = sb.ToString();
        var trimmed = before.TrimEnd();
        return trimmed.Length > 0 && before.Length > trimmed.Length && "$#%❯➜λ›»".Contains(trimmed[^1], StringComparison.Ordinal);
    }

    // ── Screen updates ───────────────────────────────────────────────────

    private void OnScreenUpdated()
    {
        if (_integrationPending)
        {
            // Output resets the quiet period.
            _integrationTimer.Stop();
            _integrationTimer.Start();
        }
        Refresh();
    }

    // Checked a moment later: clicking a suggestion briefly moves the focus, then gives it back.
    private void OnTerminalLostFocus(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() => { if (!_terminal.IsFocused) ClosePopup(); }, DispatcherPriority.Background);

    private void Refresh()
    {
        var vt = _terminal.Emulator;
        var engine = Engine;
        if (!Enabled || engine == null || vt == null || !_terminal.IsAtLiveScreen || !vt.TryGetCommandLine(out var line))
        {
            _hasLine = false;
            _lastText = null;
            HideAll();
            return;
        }

        bool changed = !_hasLine || line != _line;
        _hasLine = true;
        _line = line;
        if (!changed) return;

        var text = line.Text;
        if (text != _lastText)
        {
            _lastText = text;
            _selected = -1;
            if (_popupDismissedFor != null && _popupDismissedFor != text) _popupDismissedFor = null;
        }

        if (!line.CursorAtEnd)
        {
            HideAll();
            return;
        }

        _inline = engine.GetInlineSuggestion(text);
        _terminal.InlineSuggestion = _inline?[text.Length..];

        bool autoPopup = !_popupSuppressed && _popupDismissedFor == null && text.Trim().Length >= MinAutoPopupLength;
        if (!_popupForced && !autoPopup)
        {
            ClosePopup();
            return;
        }

        var suggestions = engine.GetSuggestions(text, MaxPopupItems);

        // A single match is already shown as grey text.
        if (suggestions.Count == 0 || (!_popupForced && suggestions.Count < 2))
        {
            ClosePopup();
            return;
        }

        ShowPopup(suggestions, text);
    }

    // ── Keyboard ─────────────────────────────────────────────────────────

    private void OnKeyIntercept(object? sender, KeyEventArgs e)
    {
        _lastKeyTime = DateTime.UtcNow;
        if (!Enabled) return;

        var mods = e.KeyModifiers;
        bool ctrl = mods.HasFlag(KeyModifiers.Control);
        bool noMods = mods == KeyModifiers.None;

        // Ctrl+Space opens the list (also for an empty line: recent history).
        if (ctrl && !mods.HasFlag(KeyModifiers.Alt) && !mods.HasFlag(KeyModifiers.Shift) && e.Key == Key.Space)
        {
            if (_hasLine && _line.CursorAtEnd)
            {
                _popupForced = true;
                _popupSuppressed = false;
                _popupDismissedFor = null;
                _line = default; // force a refresh
                _hasLine = false;
                Refresh();
                if (_popup.IsVisible) Select(0);
                e.Handled = true;
            }
            return;
        }

        if (!_hasLine) return;

        if (_popup.IsVisible)
        {
            switch (e.Key)
            {
                case Key.Down when noMods:
                    Select(Math.Min(_selected + 1, _items.Count - 1));
                    e.Handled = true;
                    return;
                case Key.Up when noMods && _selected >= 0:
                    Select(_selected - 1); // from the first item back to "no selection"
                    e.Handled = true;
                    return;
                case Key.Tab or Key.Enter when noMods && _selected >= 0:
                    Accept(_items[_selected]);
                    e.Handled = true;
                    return;
                case Key.Escape when noMods:
                    _popupDismissedFor = _lastText;
                    _popupForced = false;
                    ClosePopup();
                    e.Handled = true;
                    return;
            }
        }

        if (_inline != null && _line.CursorAtEnd && _lastText != null && _inline.Length > _lastText.Length)
        {
            var rest = _inline[_lastText.Length..];
            if (noMods && e.Key is Key.Right or Key.End)
            {
                AcceptText(rest);
                e.Handled = true;
                return;
            }
            if (ctrl && mods == KeyModifiers.Control && e.Key == Key.Right)
            {
                AcceptText(NextWord(rest));
                e.Handled = true;
                return;
            }
        }

        switch (e.Key)
        {
            case Key.Up or Key.Down or Key.PageUp or Key.PageDown:
                // The shell's history navigation: the changing line should not pop up the list.
                _popupSuppressed = true;
                _popupForced = false;
                ClosePopup();
                break;
            case Key.Enter:
                if (_lastText != null && _line.CursorAtEnd) Engine?.AddHistory(_lastText);
                _popupSuppressed = false;
                _popupForced = false;
                HideAll();
                break;
            case Key.Tab:
                // The shell completes; its result should not open the list.
                _popupSuppressed = true;
                ClosePopup();
                break;
            case Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt:
                break;
            default:
                if (ctrl && e.Key == Key.C)
                {
                    _popupForced = false;
                    HideAll();
                }
                else if (!ctrl)
                {
                    _popupSuppressed = false; // typing
                }
                break;
        }
    }

    /// <summary>The next word of the suggestion, with the whitespace before it.</summary>
    private static string NextWord(string rest)
    {
        int i = 0;
        while (i < rest.Length && char.IsWhiteSpace(rest[i])) i++;
        while (i < rest.Length && !char.IsWhiteSpace(rest[i])) i++;
        return rest[..i];
    }

    private void Accept(CommandSuggestionItem item)
    {
        if (!_hasLine || _lastText == null || !item.Suggestion.Text.StartsWith(_lastText, StringComparison.Ordinal)) return;
        AcceptText(item.Suggestion.Text[_lastText.Length..]);
        _terminal.Focus();
    }

    private void AcceptText(string text)
    {
        _popupForced = false;
        _popupSuppressed = true; // until the user types again
        HideAll();
        _terminal.SendText(text);
    }

    // ── Popup ────────────────────────────────────────────────────────────

    private void ShowPopup(IReadOnlyList<CommandSuggestion> suggestions, string typed)
    {
        var previous = _selected >= 0 && _selected < _items.Count ? _items[_selected].Suggestion.Text : null;

        _items = suggestions.Select(s => new CommandSuggestionItem(s, typed)).ToList();
        _selected = previous == null ? -1 : _items.FindIndex(i => i.Suggestion.Text == previous);
        if (_selected >= 0) _items[_selected].IsSelected = true;

        _popup.FontFamily = _terminal.FontFamily;
        _popup.FontSize = Math.Max(11, _terminal.FontSize - 1);
        _popup.SetItems(_items);
        _popup.IsVisible = true;
        PositionPopup();
    }

    private void PositionPopup()
    {
        var vt = _terminal.Emulator;
        if (vt == null) return;

        var bounds = _terminal.Bounds;
        _popup.MaxWidth = Math.Max(200, bounds.Width - 16);
        _popup.Measure(new Size(_popup.MaxWidth, double.PositiveInfinity));
        // DesiredSize includes the margin, which holds the previous position: without it the
        // popup "did not fit" below the prompt and was pushed to the top of the terminal.
        var size = _popup.DesiredSize.Deflate(_popup.Margin);

        // The item text starts after marker, icon and spacing: align it with the typed text.
        const double TextOffset = 4 + 1 + 3 + 8 + 12 + 8;
        var start = _terminal.GetCellRect(_line.StartColumn, _line.StartRow);
        var cursor = _terminal.GetCellRect(Math.Min(vt.CursorColumn, vt.Width - 1), vt.CursorRow);

        double x = Math.Clamp(start.X - TextOffset, 4, Math.Max(4, bounds.Width - size.Width - 4));
        double y = cursor.Bottom + 2;
        if (y + size.Height > bounds.Height - 4)
            y = Math.Max(4, start.Y - size.Height - 2); // no room below: above the command line

        _popup.Margin = new Thickness(x, y, 0, 0);
    }

    private void Select(int index)
    {
        if (_selected >= 0 && _selected < _items.Count) _items[_selected].IsSelected = false;
        _selected = index >= 0 && index < _items.Count ? index : -1;
        if (_selected >= 0) _items[_selected].IsSelected = true;
    }

    private void ClosePopup()
    {
        if (!_popup.IsVisible) return;
        _popup.IsVisible = false;
        _popup.SetItems([]);
        _items = [];
        _selected = -1;
    }

    private void HideAll()
    {
        _inline = null;
        _terminal.InlineSuggestion = null;
        ClosePopup();
    }
}
