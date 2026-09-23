using System.Runtime.CompilerServices;
using System.Text;

namespace T.VT;

/// <summary>
/// Main VT100/XTerm terminal controller
/// </summary>
public class VirtualTerminal(int width = 80, int height = 24)
{
    private TerminalBuffer _buffer = new(width, height);
    private TerminalBuffer? _alternateBuffer;
    private readonly List<TerminalLine> _scrollback = [];

    // Damage tracking for the renderer: per-row flags plus the number of full-screen scrolls
    // since the renderer last looked. The renderer shifts its row caches by the scroll count
    // and rebuilds only the flagged rows instead of every row after each line feed.
    private bool[] _rowDamage = new bool[height];
    private int _scrollDamage;
    private bool _fullDamage = true;
    private bool _hasDamage = true;
    private bool _cursorMoved;

    // Cursor & Dimensions
    private int _cursorColumn;
    private int _cursorRow;
    private int _scrollTop;
    private int _scrollBottom = height - 1;
    private int _width = width;
    private int _height = height;

    // Tab stops
    private bool[] _tabStops = InitTabStops(Math.Max(1024, width));

    // Current attributes
    private TerminalColor _foreground = TerminalColor.Default;
    private TerminalColor _background = TerminalColor.Default;
    private CellAttributes _attributes = CellAttributes.None;

    // Cached template for fast writing (avoids struct construction overhead in loops)
    private TerminalCharacter _charTemplate = new() { Foreground = TerminalColor.Default, Background = TerminalColor.Default };

    // Saved cursor states
    private readonly CursorState _savedCursor = new();
    private readonly CursorState _savedCursorAlt = new();

    // Modes
    private bool _originMode;
    private bool _autoWrap = true;
    private bool _insertMode;
    private bool _lineFeedNewLine;
    private bool _cursorVisible = true;
    private bool _applicationCursorKeys;
    private bool _applicationKeypad;
    private bool _bracketedPasteMode;
    private bool _alternateScroll = true;

    // Mouse tracking
    private MouseTrackingMode _mouseTracking = MouseTrackingMode.None;
    private bool _sgrMouseMode;

    // Character sets (G0/G1, GL shifts via SI/SO)
    private enum CharsetMode { Ascii, DecSpecialGraphics }
    private CharsetMode _g0Charset = CharsetMode.Ascii;
    private CharsetMode _g1Charset = CharsetMode.Ascii;
    private bool _glIsG1; // false = G0 active (SI), true = G1 active (SO)
    private char _charsetDesignator;

    // Last printed character (for REP / CSI b)
    private int _lastPrintedCodePoint = ' ';

    // Characters with combining marks; cells refer to them by id.
    private readonly GraphemeTable _graphemes = new();

    // High surrogate waiting for its low half (a pair may be split across two Feed calls).
    private char _pendingHighSurrogate;

    private enum ParserState { Ground, Escape, EscapeIntermediate, CsiEntry, CsiParam, CsiIntermediate, OscString, IgnoreString, Charset }
    private ParserState _state = ParserState.Ground;

    // Parameters are clamped so hostile input cannot overflow counters or allocate unbounded memory.
    private const int MaxParamValue = 65535;
    private const int MaxParamCount = 32;

    private readonly List<int> _parameters = new(16);
    private readonly List<bool> _paramIsSub = new(16); // true for ':' separated sub-parameters (e.g. 38:2::r:g:b)
    private int _currentParam;
    private bool _hasCurrentParam;
    private bool _nextParamIsSub;

    private readonly StringBuilder _oscBuffer = new(256);
    private char _privateMarker; // '?', '>', '<', '=' directly after CSI
    private char _intermediate;  // 0x20-0x2F byte before the final byte (e.g. ' ' in CSI Ps SP q)
    private const char InvalidSequence = '\uFFFF'; // marks a malformed CSI sequence that must be ignored
    private bool _stringEscPending;

    // UTF-8 Handling
    private readonly Decoder _utf8Decoder = Encoding.UTF8.GetDecoder();
    private char[] _charBuffer = new char[8192];

    public int Width => _width;
    public int Height => _height;
    public int CursorColumn => _cursorColumn;
    public int CursorRow => _cursorRow;
    public bool CursorVisible => _cursorVisible;
    public TerminalBuffer Buffer => _buffer;
    public IReadOnlyList<TerminalLine> Scrollback => _scrollback;
    public int MaxScrollback { get; set; } = 10000;

    /// <summary>Resolves cell values of characters with combining marks (see <see cref="TerminalCharacter.CodePoint"/>).</summary>
    public GraphemeTable Graphemes => _graphemes;

    /// <summary>Total number of lines ever moved into the scrollback (monotonic; lets views keep a scrolled-back viewport stable).</summary>
    public long ScrollbackLinesAdded { get; private set; }

    /// <summary>DECCKM: application cursor keys mode (ESC O instead of CSI for arrows).</summary>
    public bool ApplicationCursorKeys => _applicationCursorKeys;
    /// <summary>DECKPAM/DECKPNM: application keypad mode.</summary>
    public bool ApplicationKeypad => _applicationKeypad;
    /// <summary>Mode 2004: bracketed paste.</summary>
    public bool BracketedPasteMode => _bracketedPasteMode;
    /// <summary>True while the alternate screen buffer (vim, htop, tmux, ...) is active.</summary>
    public bool IsAlternateScreen => _alternateBuffer != null;
    /// <summary>Mode 1007: wheel events are translated to arrow keys on the alternate screen.</summary>
    public bool AlternateScrollMode => _alternateScroll;
    /// <summary>Active mouse tracking mode (modes 1000/1002/1003).</summary>
    public MouseTrackingMode MouseTracking => _mouseTracking;
    /// <summary>Mode 1006: SGR extended mouse coordinate reporting.</summary>
    public bool SgrMouseMode => _sgrMouseMode;

    public event Action? ScreenChanged;
    public event Action<string>? TitleChanged;
    public event Action<string>? SendData;
    /// <summary>DECSCUSR: 0/1=blink block, 2=block, 3=blink underline, 4=underline, 5=blink bar, 6=bar.</summary>
    public event Action<int>? CursorStyleChanged;

    private static bool[] InitTabStops(int size)
    {
        var tabs = new bool[size];
        for (int i = 8; i < size; i += 8) tabs[i] = true;
        return tabs;
    }

    private void MarkDirty(int row)
    {
        if ((uint)row < (uint)_rowDamage.Length) _rowDamage[row] = true;
        _hasDamage = true;
    }

    private void MarkDirty(int startRow, int endRow)
    {
        startRow = Math.Max(0, startRow);
        endRow = Math.Min(_rowDamage.Length - 1, endRow);
        for (int row = startRow; row <= endRow; row++) _rowDamage[row] = true;
        _hasDamage = true;
    }

    private void MarkAllDirty()
    {
        _fullDamage = true;
        _hasDamage = true;
    }

    private bool IsFullScreenRegion => _scrollTop == 0 && _scrollBottom == _height - 1;

    /// <summary>The whole screen moved up by <paramref name="lines"/>; shift the row flags along.</summary>
    private void AddScrollDamage(int lines)
    {
        _hasDamage = true;
        if (_fullDamage) return;

        _scrollDamage += lines;
        int rows = _rowDamage.Length;
        if (_scrollDamage >= rows)
        {
            _fullDamage = true;
            return;
        }

        Array.Copy(_rowDamage, lines, _rowDamage, 0, rows - lines);
        Array.Fill(_rowDamage, true, rows - lines, lines);
    }

    /// <summary>
    /// Hands the accumulated damage to the renderer and resets it. <paramref name="rows"/> receives
    /// the dirty flags (in current screen coordinates, i.e. after applying <paramref name="scrolled"/>).
    /// Returns false when nothing changed since the last call.
    /// </summary>
    public bool TakeDamage(bool[] rows, out int scrolled, out bool full)
    {
        full = _fullDamage;
        scrolled = _scrollDamage;
        var any = _hasDamage;

        int n = Math.Min(rows.Length, _rowDamage.Length);
        Array.Copy(_rowDamage, rows, n);
        if (rows.Length > n) Array.Fill(rows, true, n, rows.Length - n);

        Array.Clear(_rowDamage);
        _scrollDamage = 0;
        _fullDamage = false;
        _hasDamage = false;
        return any;
    }

    private void UpdateTemplate()
    {
        _charTemplate.Foreground = _foreground;
        _charTemplate.Background = _background;
        _charTemplate.Attributes = _attributes;
        _charTemplate.CodePoint = ' ';
    }

    public void Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        // Shrinking: keep the cursor line visible by moving the lines above it into the
        // scrollback (main screen) instead of cutting off the bottom where the prompt is.
        if (height < _height && _cursorRow >= height)
        {
            int shift = _cursorRow - height + 1;
            var removed = _buffer.RemoveTopLines(shift);
            if (_alternateBuffer == null)
            {
                _scrollback.AddRange(removed);
                ScrollbackLinesAdded += removed.Count;
                TrimScrollback();
            }
            _cursorRow -= shift;
            _savedCursor.Row = Math.Max(0, _savedCursor.Row - shift);
        }

        _width = width;
        _height = height;
        _buffer.Resize(width, height);
        _alternateBuffer?.Resize(width, height);
        _rowDamage = new bool[height];
        MarkAllDirty();

        // Like xterm: a resize resets the scroll region to the full screen.
        _scrollTop = 0;
        _scrollBottom = height - 1;
        _cursorColumn = Math.Min(_cursorColumn, width - 1);
        _cursorRow = Math.Min(_cursorRow, height - 1);
        ClampSavedCursor(_savedCursor);
        ClampSavedCursor(_savedCursorAlt);

        if (width > _tabStops.Length)
        {
            var newTabs = new bool[width + 128];
            Array.Copy(_tabStops, newTabs, _tabStops.Length);
            for (int i = ((_tabStops.Length / 8) + 1) * 8; i < newTabs.Length; i += 8)
                newTabs[i] = true;
            _tabStops = newTabs;
        }

        ScreenChanged?.Invoke();
    }

    public void Feed(string text)
    {
        Feed(text.AsSpan());
    }

    public void Feed(ReadOnlySpan<char> chars)
    {
        int prevRow = _cursorRow;
        int prevCol = _cursorColumn;

        ProcessTextFast(chars);

        // The cursor is drawn on its own layer: a cursor-only move needs a notification, not a row repaint.
        if (_cursorRow != prevRow || _cursorColumn != prevCol)
            _cursorMoved = true;

        if (_hasDamage || _cursorMoved)
        {
            _cursorMoved = false;
            ScreenChanged?.Invoke();
        }
    }

    public void Feed(ReadOnlySpan<byte> data)
    {
        int charCount = _utf8Decoder.GetCharCount(data, false);
        if (_charBuffer.Length < charCount)
            Array.Resize(ref _charBuffer, Math.Max(charCount, _charBuffer.Length * 2));

        int charsUsed = _utf8Decoder.GetChars(data, _charBuffer, false);
        Feed(_charBuffer.AsSpan(0, charsUsed));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ProcessTextFast(ReadOnlySpan<char> text)
    {
        int currentPos = 0;
        int len = text.Length;

        while (currentPos < len)
        {
            if (_state == ParserState.Ground && _pendingHighSurrogate == '\0')
            {
                int i = currentPos;
                // Fast path for runs of single-width characters without combining marks
                while (i < len && IsSimplePrintable(text[i])) i++;

                int runLength = i - currentPos;
                if (runLength > 0)
                {
                    PutString(text.Slice(currentPos, runLength));
                    currentPos = i;
                    if (currentPos >= len) break;
                }
            }

            if (currentPos < len)
            {
                ProcessChar(text[currentPos++]);
            }
        }
    }

    /// <summary>
    /// Printable ASCII and U+00A0-U+02FF: always one column wide and never combining, so a
    /// run of them can be copied into the line directly.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsSimplePrintable(char c) => (uint)(c - 0x20) < 0x5F || (uint)(c - 0xA0) < 0x260;

    public TerminalCharacter GetCell(int column, int row)
    {
        if (row >= 0 && row < _buffer.Count && column >= 0)
            return _buffer[row][column];
        return TerminalCharacter.Blank;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ProcessChar(char c)
    {
        switch (_state)
        {
            case ParserState.Ground: ProcessGroundState(c); break;
            case ParserState.Escape: ProcessEscapeState(c); break;
            case ParserState.EscapeIntermediate: ProcessEscapeIntermediateState(c); break;
            case ParserState.CsiEntry:
            case ParserState.CsiParam:
            case ParserState.CsiIntermediate: ProcessCsiState(c); break;
            case ParserState.OscString: ProcessOscState(c); break;
            case ParserState.IgnoreString: ProcessIgnoreStringState(c); break;
            case ParserState.Charset: ProcessCharsetState(c); break;
        }
    }

    private void ProcessGroundState(char c)
    {
        if (_pendingHighSurrogate != '\0')
        {
            char high = _pendingHighSurrogate;
            _pendingHighSurrogate = '\0';
            if (char.IsLowSurrogate(c))
            {
                PutCodePoint(char.ConvertToUtf32(high, c));
                return;
            }
            PutCodePoint(0xFFFD); // unpaired high surrogate
        }

        if (c >= 32 && c != 127)
        {
            if (c is >= '\x80' and < '\xA0')
                return; // C1 control characters are not printed
            if (char.IsHighSurrogate(c))
                _pendingHighSurrogate = c;
            else
                PutCodePoint(char.IsLowSurrogate(c) ? 0xFFFD : c);
            return;
        }

        if (c == '\x1B')
            _state = ParserState.Escape;
        else
            ExecuteControl(c);
    }

    /// <summary>C0 control characters (also executed inside escape sequences, like xterm).</summary>
    private void ExecuteControl(char c)
    {
        switch (c)
        {
            case '\r':
                _cursorColumn = 0;
                MarkDirty(_cursorRow);
                break;
            case '\n' or '\x0B' or '\x0C': LineFeed(); break;
            case '\b':
                if (_cursorColumn > 0)
                {
                    _cursorColumn = Math.Min(_cursorColumn, _width) - 1;
                    MarkDirty(_cursorRow);
                }
                break;
            case '\t': Tab(); break;
            case '\a': break; // Bell
            case '\x0E': _glIsG1 = true; break;  // SO: select G1
            case '\x0F': _glIsG1 = false; break; // SI: select G0
        }
    }

    private void ProcessEscapeState(char c)
    {
        switch (c)
        {
            case '[':
                _state = ParserState.CsiEntry;
                _parameters.Clear();
                _paramIsSub.Clear();
                _currentParam = 0;
                _hasCurrentParam = false;
                _nextParamIsSub = false;
                _privateMarker = '\0';
                _intermediate = '\0';
                break;
            case ']':
                _state = ParserState.OscString;
                _oscBuffer.Clear();
                _stringEscPending = false;
                break;
            case 'P' or 'X' or '^' or '_':
                // DCS, SOS, PM, APC: payload is ignored until the string terminator.
                _state = ParserState.IgnoreString;
                _stringEscPending = false;
                break;
            case '(' or ')' or '*' or '+' or '-' or '.' or '/':
                _charsetDesignator = c;
                _state = ParserState.Charset;
                break;
            case >= ' ' and <= '/':
                // Other intermediates (ESC # 8, ESC % G, ESC SP F, ...): swallow the final byte.
                _state = ParserState.EscapeIntermediate;
                break;
            case '\x18' or '\x1A': _state = ParserState.Ground; break; // CAN / SUB abort
            case '\x1B': break; // ESC ESC: restart
            case < ' ': ExecuteControl(c); break;
            case '7': SaveCursor(); _state = ParserState.Ground; break;
            case '8': RestoreCursor(); _state = ParserState.Ground; break;
            case 'D': LineFeed(); _state = ParserState.Ground; break;
            case 'E': _cursorColumn = 0; LineFeed(); _state = ParserState.Ground; break;
            case 'M': ReverseIndex(); _state = ParserState.Ground; break;
            case 'c': FullReset(); _state = ParserState.Ground; break;
            case 'H': SetTabStop(); _state = ParserState.Ground; break;
            case '=': _applicationKeypad = true; _state = ParserState.Ground; break;
            case '>': _applicationKeypad = false; _state = ParserState.Ground; break;
            default: _state = ParserState.Ground; break;
        }
    }

    private void ProcessCharsetState(char c)
    {
        var charset = c switch
        {
            '0' => CharsetMode.DecSpecialGraphics,
            _ => CharsetMode.Ascii
        };

        switch (_charsetDesignator)
        {
            case '(': _g0Charset = charset; break;
            case ')': _g1Charset = charset; break;
        }

        _state = ParserState.Ground;
    }

    private void ProcessEscapeIntermediateState(char c)
    {
        if (c >= '0' && c <= '~')
            _state = ParserState.Ground;
        else if (c == '\x1B')
            _state = ParserState.Escape;
        else if (c is '\x18' or '\x1A')
            _state = ParserState.Ground;
        else if (c < ' ')
            ExecuteControl(c);
    }

    private void ProcessCsiState(char c)
    {
        if (c >= '0' && c <= '9')
        {
            _currentParam = Math.Min(_currentParam * 10 + (c - '0'), MaxParamValue);
            _hasCurrentParam = true;
            _state = ParserState.CsiParam;
        }
        else if (c == ';' || c == ':')
        {
            PushParam();
            _nextParamIsSub = c == ':';
            _state = ParserState.CsiParam;
        }
        else if (c >= '<' && c <= '?')
        {
            // Private marker - only valid directly after CSI.
            if (_state == ParserState.CsiEntry) _privateMarker = c;
            else _intermediate = InvalidSequence; // malformed: ignore the sequence
        }
        else if (c >= ' ' && c <= '/')
        {
            _intermediate = _intermediate == '\0' ? c : InvalidSequence;
            _state = ParserState.CsiIntermediate;
        }
        else if (c >= '@' && c <= '~')
        {
            if (_hasCurrentParam || _parameters.Count > 0) PushParam();
            ExecuteCsi(c);
            _state = ParserState.Ground;
        }
        else if (c == '\x1B')
        {
            _state = ParserState.Escape;
        }
        else if (c is '\x18' or '\x1A')
        {
            _state = ParserState.Ground;
        }
        else if (c < ' ')
        {
            ExecuteControl(c);
        }
    }

    private void PushParam()
    {
        if (_parameters.Count < MaxParamCount)
        {
            _parameters.Add(_hasCurrentParam ? _currentParam : 0);
            _paramIsSub.Add(_nextParamIsSub);
        }
        _currentParam = 0;
        _hasCurrentParam = false;
        _nextParamIsSub = false;
    }

    private void ProcessOscState(char c)
    {
        if (_stringEscPending)
        {
            // ESC terminates the string; "ESC \" is the regular ST. Any other byte starts a new escape sequence.
            _stringEscPending = false;
            ExecuteOsc();
            _state = ParserState.Ground;
            if (c != '\\') ProcessEscapeState(c);
            return;
        }

        if (c == '\x07' || c == '\x9C')
        {
            ExecuteOsc();
            _state = ParserState.Ground;
        }
        else if (c == '\x1B')
        {
            _stringEscPending = true;
        }
        else if (c is '\x18' or '\x1A')
        {
            _state = ParserState.Ground;
        }
        else if (_oscBuffer.Length < 4096)
        {
            _oscBuffer.Append(c);
        }
    }

    private void ProcessIgnoreStringState(char c)
    {
        if (_stringEscPending)
        {
            _stringEscPending = false;
            if (c == '\\') { _state = ParserState.Ground; return; }
            if (c == '\x1B') return; // doubled ESC (e.g. tmux passthrough) stays inside the string
            return;
        }

        if (c == '\x1B') _stringEscPending = true;
        else if (c is '\x9C' or '\x18' or '\x1A') _state = ParserState.Ground;
    }

    private int GetParam(int index, int defaultValue = 1) =>
        index < _parameters.Count && _parameters[index] > 0 ? _parameters[index] : defaultValue;

    private void ExecuteCsi(char cmd)
    {
        if (_intermediate == InvalidSequence)
            return;

        if (_privateMarker != '\0' || _intermediate != '\0')
        {
            ExecuteCsiExtended(cmd);
            return;
        }

        switch (cmd)
        {
            case 'A': CursorUp(GetParam(0)); break;
            case 'B': CursorDown(GetParam(0)); break;
            case 'C': CursorForward(GetParam(0)); break;
            case 'D': CursorBackward(GetParam(0)); break;
            case 'E': CursorNextLine(GetParam(0)); break;
            case 'F': CursorPrevLine(GetParam(0)); break;
            case 'G': CursorCharAbsolute(GetParam(0)); break;
            case 'H' or 'f': CursorPosition(GetParam(0), GetParam(1)); break;
            case 'J': EraseInDisplay(GetParam(0, 0)); break;
            case 'K': EraseInLine(GetParam(0, 0)); break;
            case 'L': InsertLines(GetParam(0)); break;
            case 'M': DeleteLines(GetParam(0)); break;
            case 'P': DeleteCharacters(GetParam(0)); break;
            case '@': InsertCharacters(GetParam(0)); break;
            case 'X': EraseCharacters(GetParam(0)); break;
            case 'S': ScrollUp(GetParam(0)); break;
            case 'T': ScrollDown(GetParam(0)); break;
            case 'd': CursorLineAbsolute(GetParam(0)); break;
            case 'b': RepeatLastCharacter(GetParam(0)); break;
            case 'm': SelectGraphicRendition(); break;
            case 'r': SetScrollRegion(GetParam(0, 1), GetParam(1, _height)); break;
            case 's': SaveCursor(); break;
            case 'u': RestoreCursor(); break;
            case 'h': SetMode(true); break;
            case 'l': SetMode(false); break;
            case 'n': DeviceStatusReport(); break;
            case 'c': DeviceAttributes(); break;
            case 'g': ClearTabStop(GetParam(0, 0)); break;
        }
    }

    /// <summary>
    /// CSI sequences with a private marker or intermediate byte. Anything not listed is
    /// ignored - e.g. "CSI > 4;1 m" (modifyOtherKeys) must not change SGR attributes and
    /// "CSI > 1 u" / "CSI &lt; u" (kitty keyboard protocol) must not restore the cursor.
    /// </summary>
    private void ExecuteCsiExtended(char cmd)
    {
        switch (_privateMarker, _intermediate, cmd)
        {
            case ('?', '\0', 'h'): SetMode(true); break;
            case ('?', '\0', 'l'): SetMode(false); break;
            case ('?', '\0', 'J'): EraseInDisplay(GetParam(0, 0)); break;   // DECSED
            case ('?', '\0', 'K'): EraseInLine(GetParam(0, 0)); break;      // DECSEL
            case ('?', '\0', 'n'): DeviceStatusReport(privateReport: true); break;
            case ('>', '\0', 'c'): SendData?.Invoke("\x1B[>0;276;0c"); break; // secondary DA
            case ('\0', ' ', 'q'): CursorStyleChanged?.Invoke(GetParam(0, 0)); break; // DECSCUSR
            case ('\0', '!', 'p'): SoftReset(); break;                          // DECSTR
        }
    }

    private void RepeatLastCharacter(int count)
    {
        count = Math.Min(count, _width * _height);
        int codePoint = _lastPrintedCodePoint;
        bool wide = UnicodeWidth.GetWidth(codePoint) == 2;
        for (int i = 0; i < count; i++)
        {
            if (wide) PutWide(codePoint);
            else PutChar(codePoint, alreadyMapped: true);
        }
    }

    private void ExecuteOsc()
    {
        if (_oscBuffer.Length == 0) return;

        int semicolonIndex = -1;
        int ps = 0;

        for (int i = 0; i < Math.Min(_oscBuffer.Length, 10); i++)
        {
            char c = _oscBuffer[i];
            if (c == ';') { semicolonIndex = i; break; }
            if (char.IsAsciiDigit(c)) ps = ps * 10 + (c - '0');
            else return;
        }

        if (semicolonIndex > 0)
        {
            if (ps == 0 || ps == 2)
            {
                var text = _oscBuffer.ToString(semicolonIndex + 1, _oscBuffer.Length - (semicolonIndex + 1));
                TitleChanged?.Invoke(text);
            }
        }
    }

    private void PutString(ReadOnlySpan<char> text)
    {
        if (_insertMode)
        {
            foreach (char c in text) PutChar(c);
            return;
        }

        bool graphics = (_glIsG1 ? _g1Charset : _g0Charset) == CharsetMode.DecSpecialGraphics;

        // DELAYED WRAP: If we are at _width, wrap now before writing (xenl behavior)
        if (_cursorColumn >= _width)
        {
            if (_autoWrap)
            {
                _cursorColumn = 0;
                LineFeed();
            }
            else
            {
                _cursorColumn = _width - 1;
            }
        }

        MarkDirty(_cursorRow);
        var line = _buffer[_cursorRow];
        int remainingInLine = _width - _cursorColumn;

        var cell = _charTemplate;
        if (text.Length > 0)
        {
            char last = text[^1];
            _lastPrintedCodePoint = graphics ? MapDecSpecialGraphics(last) : last;
        }

        // Case 1: Text fits in current line
        if (text.Length <= remainingInLine)
        {
            line.PrepareOverwrite(_cursorColumn, _cursorColumn + text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (graphics) ch = MapDecSpecialGraphics(ch);
                cell.CodePoint = ch;
                line[_cursorColumn + i] = cell;
            }

            _cursorColumn += text.Length;
            // DO NOT WRAP HERE. Allow cursor to sit at _width to avoid double-spacing.
        }
        else
        {
            // Case 2: Text wraps
            if (!_autoWrap)
            {
                // Without auto wrap the excess overwrites the last column: its last character remains there.
                int len = remainingInLine;
                line.PrepareOverwrite(_cursorColumn, _width);
                for (int i = 0; i < len; i++)
                {
                    char ch = text[i];
                    if (graphics) ch = MapDecSpecialGraphics(ch);
                    cell.CodePoint = ch;
                    line[_cursorColumn + i] = cell;
                }
                char lastChar = text[^1];
                cell.CodePoint = graphics ? MapDecSpecialGraphics(lastChar) : lastChar;
                line[_width - 1] = cell;
                _cursorColumn = _width - 1;
            }
            else
            {
                int processed = 0;
                while (processed < text.Length)
                {
                    MarkDirty(_cursorRow);
                    line = _buffer[_cursorRow];
                    int chunk = Math.Min(text.Length - processed, _width - _cursorColumn);
                    int startCol = _cursorColumn;

                    line.PrepareOverwrite(startCol, startCol + chunk);
                    for (int i = 0; i < chunk; i++)
                    {
                        char ch = text[processed + i];
                        if (graphics) ch = MapDecSpecialGraphics(ch);
                        cell.CodePoint = ch;
                        line[startCol + i] = cell;
                    }

                    processed += chunk;
                    _cursorColumn += chunk;

                    // Explicit wrap only if we still have text to process
                    if (_cursorColumn >= _width && processed < text.Length)
                    {
                        _cursorColumn = 0;
                        LineFeed();
                        MarkDirty(_cursorRow);
                    }
                }
            }
        }
    }

    /// <summary>Prints a character from the slow path: dispatches on its column width.</summary>
    private void PutCodePoint(int codePoint)
    {
        switch (UnicodeWidth.GetWidth(codePoint))
        {
            case 0: CombineWithPrevious(codePoint); break;
            case 2: PutWide(codePoint); break;
            default: PutChar(codePoint); break;
        }
    }

    /// <summary>Delayed wrap (xenl): a cursor parked behind the last column wraps before the next character.</summary>
    private void WrapIfPending()
    {
        if (_cursorColumn < _width) return;

        if (_autoWrap)
        {
            _cursorColumn = 0;
            LineFeed();
        }
        else
        {
            _cursorColumn = _width - 1;
        }
    }

    private void PutChar(int c, bool alreadyMapped = false)
    {
        if (!alreadyMapped && c < 0x80 && (_glIsG1 ? _g1Charset : _g0Charset) == CharsetMode.DecSpecialGraphics)
            c = MapDecSpecialGraphics((char)c);

        WrapIfPending();
        MarkDirty(_cursorRow);

        var line = _buffer[_cursorRow];
        if (_insertMode)
            line.InsertCharacters(_cursorColumn, 1);

        line.PrepareOverwrite(_cursorColumn, _cursorColumn + 1);
        var cell = _charTemplate;
        cell.CodePoint = c;
        line[_cursorColumn] = cell;

        _lastPrintedCodePoint = c;
        _cursorColumn++;
    }

    /// <summary>Prints a double-width character into two cells.</summary>
    private void PutWide(int codePoint)
    {
        if (_width < 2)
        {
            PutChar(codePoint);
            return;
        }

        WrapIfPending();
        if (_cursorColumn == _width - 1)
        {
            // Does not fit into the last column: wrap early like xterm (that column stays as it is).
            if (_autoWrap)
            {
                _cursorColumn = 0;
                LineFeed();
            }
            else
            {
                _cursorColumn = _width - 2;
            }
        }

        MarkDirty(_cursorRow);
        var line = _buffer[_cursorRow];
        if (_insertMode)
            line.InsertCharacters(_cursorColumn, 2);

        line.PrepareOverwrite(_cursorColumn, _cursorColumn + 2);
        var cell = _charTemplate;
        cell.CodePoint = codePoint;
        cell.Attributes |= CellAttributes.Wide;
        line[_cursorColumn] = cell;

        cell.CodePoint = 0;
        cell.Attributes = _charTemplate.Attributes | CellAttributes.WideContinuation;
        line[_cursorColumn + 1] = cell;

        _lastPrintedCodePoint = codePoint;
        _cursorColumn += 2;
    }

    /// <summary>
    /// Zero-width characters (combining marks, variation selectors, joiners) are appended to
    /// the character before the cursor instead of taking a cell of their own.
    /// </summary>
    private void CombineWithPrevious(int mark)
    {
        int col = Math.Min(_cursorColumn, _width) - 1;
        if (col < 0) return; // nothing to combine with

        var line = _buffer[_cursorRow];
        if (line[col].IsWideContinuation && col > 0) col--;

        var cell = line[col];
        if (cell.CodePoint == 0) return;

        cell.CodePoint = _graphemes.Combine(cell.CodePoint, mark);
        line[col] = cell;
        MarkDirty(_cursorRow);
    }

    /// <summary>Maps DEC Special Graphics (ESC ( 0) characters to their Unicode box-drawing equivalents.</summary>
    private static char MapDecSpecialGraphics(char c) => c switch
    {
        '_' => ' ',
        '`' => '\u25C6', // ◆ diamond
        'a' => '\u2592', // ▒ checkerboard
        'b' => '\u2409', // ␉ HT
        'c' => '\u240C', // ␌ FF
        'd' => '\u240D', // ␍ CR
        'e' => '\u240A', // ␊ LF
        'f' => '\u00B0', // ° degree
        'g' => '\u00B1', // ± plus-minus
        'h' => '\u2424', // ␤ NL
        'i' => '\u240B', // ␋ VT
        'j' => '\u2518', // ┘
        'k' => '\u2510', // ┐
        'l' => '\u250C', // ┌
        'm' => '\u2514', // └
        'n' => '\u253C', // ┼
        'o' => '\u23BA', // ⎺
        'p' => '\u23BB', // ⎻
        'q' => '\u2500', // ─
        'r' => '\u23BC', // ⎼
        's' => '\u23BD', // ⎽
        't' => '\u251C', // ├
        'u' => '\u2524', // ┤
        'v' => '\u2534', // ┴
        'w' => '\u252C', // ┬
        'x' => '\u2502', // │
        'y' => '\u2264', // ≤
        'z' => '\u2265', // ≥
        '{' => '\u03C0', // π
        '|' => '\u2260', // ≠
        '}' => '\u00A3', // £
        '~' => '\u00B7', // ·
        _ => c
    };

    private void LineFeed()
    {
        if (_cursorRow == _scrollBottom)
        {
            if (_scrollTop == 0 && _alternateBuffer == null)
            {
                // Move the top line into scrollback without cloning; the buffer gets a fresh line
                _scrollback.Add(_buffer.ScrollUpDetach(_scrollTop, _scrollBottom));
                ScrollbackLinesAdded++;
                TrimScrollback();
            }
            else
            {
                _buffer.ScrollUp(_scrollTop, _scrollBottom);
            }

            if (IsFullScreenRegion) AddScrollDamage(1);
            else MarkDirty(_scrollTop, _scrollBottom);
        }
        else if (_cursorRow < _height - 1)
        {
            _cursorRow++;
        }

        if (_lineFeedNewLine)
            _cursorColumn = 0;
    }

    private void TrimScrollback()
    {
        // Trim in chunks: removing from the front of a List is O(n).
        int max = Math.Max(0, MaxScrollback);
        if (_scrollback.Count > max + 100)
            _scrollback.RemoveRange(0, _scrollback.Count - max);
    }

    private void ReverseIndex()
    {
        if (_cursorRow == _scrollTop)
        {
            _buffer.ScrollDown(_scrollTop, _scrollBottom);
            MarkDirty(_scrollTop, _scrollBottom);
        }
        else if (_cursorRow > 0)
        {
            _cursorRow--;
        }
    }

    private void Tab()
    {
        int nextStop = _width - 1;
        int start = _cursorColumn + 1;
        int limit = Math.Min(_tabStops.Length, _width);

        for (int i = start; i < limit; i++)
        {
            if (_tabStops[i]) { nextStop = i; break; }
        }

        _cursorColumn = Math.Min(nextStop, _width - 1);
    }

    // Vertical movement stops at the scroll margins only when the cursor is inside them.
    private void CursorUp(int count)
    {
        int top = _cursorRow >= _scrollTop ? _scrollTop : 0;
        _cursorRow = Math.Max(top, _cursorRow - count);
    }

    private void CursorDown(int count)
    {
        int bottom = _cursorRow <= _scrollBottom ? _scrollBottom : _height - 1;
        _cursorRow = Math.Min(bottom, _cursorRow + count);
    }

    private void CursorForward(int count) => _cursorColumn = Math.Min(_width - 1, _cursorColumn + count);
    private void CursorBackward(int count) => _cursorColumn = Math.Max(0, Math.Min(_cursorColumn, _width - 1) - count);
    private void CursorNextLine(int count) { CursorDown(count); _cursorColumn = 0; }
    private void CursorPrevLine(int count) { CursorUp(count); _cursorColumn = 0; }
    private void CursorCharAbsolute(int column) => _cursorColumn = Math.Clamp(column - 1, 0, _width - 1);

    private void CursorLineAbsolute(int row)
    {
        int baseRow = _originMode ? _scrollTop : 0;
        int maxRow = _originMode ? _scrollBottom : _height - 1;
        _cursorRow = Math.Clamp(baseRow + row - 1, baseRow, maxRow);
    }

    private void CursorPosition(int row, int column)
    {
        CursorLineAbsolute(row);
        _cursorColumn = Math.Clamp(column - 1, 0, _width - 1);
    }

    /// <summary>Column for editing operations: a pending wrap (cursor at Width) acts on the last column.</summary>
    private int EditColumn => Math.Min(_cursorColumn, _width - 1);

    private TerminalCharacter BlankWithCurrentBackground()
    {
        var fillChar = _charTemplate;
        fillChar.CodePoint = ' ';
        return fillChar;
    }

    private void EraseInDisplay(int mode)
    {
        var fillChar = BlankWithCurrentBackground();

        switch (mode)
        {
            case 0:
                EraseLineSection(_cursorRow, EditColumn, _width - 1, fillChar);
                for (int y = _cursorRow + 1; y < _height; y++)
                    EraseLineWhole(y, fillChar);
                MarkDirty(_cursorRow, _height - 1);
                break;
            case 1:
                for (int y = 0; y < _cursorRow; y++)
                    EraseLineWhole(y, fillChar);
                EraseLineSection(_cursorRow, 0, EditColumn, fillChar);
                MarkDirty(0, _cursorRow);
                break;
            case 2:
                for (int y = 0; y < _height; y++)
                    EraseLineWhole(y, fillChar);
                MarkDirty(0, _height - 1);
                break;
            case 3:
                // ED 3 (xterm): erase saved lines only - the visible screen stays.
                _scrollback.Clear();
                MarkDirty(0, _height - 1);
                break;
        }
    }

    private void EraseInLine(int mode)
    {
        var fillChar = BlankWithCurrentBackground();
        MarkDirty(_cursorRow);

        switch (mode)
        {
            case 0: EraseLineSection(_cursorRow, EditColumn, _width - 1, fillChar); break;
            case 1: EraseLineSection(_cursorRow, 0, EditColumn, fillChar); break;
            case 2: EraseLineWhole(_cursorRow, fillChar); break;
        }
    }

    private void EraseLineWhole(int row, TerminalCharacter fill)
    {
        if (row >= _buffer.Count) return;
        var line = _buffer[row];
        for (int i = 0; i < _width; i++) line[i] = fill;
    }

    private void EraseLineSection(int row, int start, int end, TerminalCharacter fill)
    {
        if (row >= _buffer.Count) return;
        var line = _buffer[row];
        int limit = Math.Min(end + 1, _width);
        start = Math.Max(0, start);
        line.PrepareOverwrite(start, limit);
        for (int i = start; i < limit; i++) line[i] = fill;
    }

    private void InsertLines(int count)
    {
        if (_cursorRow < _scrollTop || _cursorRow > _scrollBottom) return;
        _buffer.InsertLines(_cursorRow, count, _scrollBottom);
        MarkDirty(_cursorRow, _scrollBottom);
        _cursorColumn = 0;
    }

    private void DeleteLines(int count)
    {
        if (_cursorRow < _scrollTop || _cursorRow > _scrollBottom) return;
        _buffer.DeleteLines(_cursorRow, count, _scrollBottom);
        MarkDirty(_cursorRow, _scrollBottom);
        _cursorColumn = 0;
    }

    private void InsertCharacters(int count)
    {
        _buffer[_cursorRow].InsertCharacters(EditColumn, count);
        MarkDirty(_cursorRow);
    }

    private void DeleteCharacters(int count)
    {
        _buffer[_cursorRow].DeleteCharacters(EditColumn, count);
        MarkDirty(_cursorRow);
    }

    private void EraseCharacters(int count)
    {
        int start = EditColumn;
        EraseLineSection(_cursorRow, start, start + Math.Min(count, _width) - 1, BlankWithCurrentBackground());
        MarkDirty(_cursorRow);
    }

    private void ScrollUp(int count)
    {
        count = Math.Min(count, _scrollBottom - _scrollTop + 1);
        _buffer.ScrollUp(_scrollTop, _scrollBottom, count);
        if (IsFullScreenRegion) AddScrollDamage(count);
        else MarkDirty(_scrollTop, _scrollBottom);
    }

    private void ScrollDown(int count)
    {
        _buffer.ScrollDown(_scrollTop, _scrollBottom, count);
        MarkDirty(_scrollTop, _scrollBottom);
    }

    private void SetScrollRegion(int top, int bottom)
    {
        top = Math.Clamp(top - 1, 0, _height - 1);
        bottom = Math.Clamp(bottom - 1, 0, _height - 1);
        if (top >= bottom) return; // invalid region: ignored (DECSTBM)

        _scrollTop = top;
        _scrollBottom = bottom;
        CursorPosition(1, 1);
    }

    private void SelectGraphicRendition()
    {
        if (_parameters.Count == 0)
        {
            ResetAttributes();
            UpdateTemplate();
            return;
        }

        for (int i = 0; i < _parameters.Count; i++)
        {
            var p = _parameters[i];

            // Sub-parameters belong to the preceding parameter (ITU T.416 "38:2::r:g:b", "4:3").
            int subStart = i + 1, subEnd = i + 1;
            while (subEnd < _parameters.Count && _paramIsSub[subEnd]) subEnd++;
            int subCount = subEnd - subStart;

            switch (p)
            {
                case 0: ResetAttributes(); break;
                case 1: _attributes |= CellAttributes.Bold; break;
                case 2: _attributes |= CellAttributes.Dim; break;
                case 3: _attributes |= CellAttributes.Italic; break;
                case 4:
                    // 4:0 = no underline, 4:1..5 = single/double/curly/dotted/dashed
                    _attributes &= ~(CellAttributes.Underline | CellAttributes.DoubleUnderline);
                    if (subCount == 0 || _parameters[subStart] != 0)
                        _attributes |= subCount > 0 && _parameters[subStart] == 2 ? CellAttributes.DoubleUnderline : CellAttributes.Underline;
                    break;
                case 5 or 6: _attributes |= CellAttributes.Blink; break;
                case 7: _attributes |= CellAttributes.Inverse; break;
                case 8: _attributes |= CellAttributes.Hidden; break;
                case 9: _attributes |= CellAttributes.Strikethrough; break;
                case 21: _attributes |= CellAttributes.DoubleUnderline; break;
                case 22: _attributes &= ~(CellAttributes.Bold | CellAttributes.Dim); break;
                case 23: _attributes &= ~CellAttributes.Italic; break;
                case 24: _attributes &= ~(CellAttributes.Underline | CellAttributes.DoubleUnderline); break;
                case 25: _attributes &= ~CellAttributes.Blink; break;
                case 27: _attributes &= ~CellAttributes.Inverse; break;
                case 28: _attributes &= ~CellAttributes.Hidden; break;
                case 29: _attributes &= ~CellAttributes.Strikethrough; break;
                case >= 30 and <= 37: _foreground = new TerminalColor(p - 30); break;
                case 38:
                    if (ParseExtendedColor(ref i, subStart, subCount) is { } fg) _foreground = fg;
                    continue;
                case 39: _foreground = TerminalColor.Default; break;
                case >= 40 and <= 47: _background = new TerminalColor(p - 40); break;
                case 48:
                    if (ParseExtendedColor(ref i, subStart, subCount) is { } bg) _background = bg;
                    continue;
                case 49: _background = TerminalColor.Default; break;
                case 58:
                    // Underline color: not rendered, but its arguments must not be read as attributes.
                    ParseExtendedColor(ref i, subStart, subCount);
                    continue;
                case >= 90 and <= 97: _foreground = new TerminalColor(p - 90 + 8); break;
                case >= 100 and <= 107: _background = new TerminalColor(p - 100 + 8); break;
            }

            i = subEnd - 1; // skip sub-parameters of this attribute
        }
        UpdateTemplate();
    }

    /// <summary>
    /// Parses "38;5;n" / "38;2;r;g;b" (semicolon form) and "38:5:n" / "38:2:[cs]:r:g:b"
    /// (colon form). Advances <paramref name="i"/> past all consumed parameters.
    /// </summary>
    private TerminalColor? ParseExtendedColor(ref int i, int subStart, int subCount)
    {
        if (subCount > 0)
        {
            int mode = _parameters[subStart];
            i = subStart + subCount - 1;
            if (mode == 5 && subCount >= 2)
                return TerminalColor.FromPalette256(ClampByte(_parameters[subStart + 1]));
            if (mode == 2 && subCount >= 4)
            {
                // With a colorspace id there are 5 sub-parameters: 2:cs:r:g:b
                int rgb = subCount >= 5 ? subStart + 2 : subStart + 1;
                return TerminalColor.FromRgb(ClampByte(_parameters[rgb]), ClampByte(_parameters[rgb + 1]), ClampByte(_parameters[rgb + 2]));
            }
            return null;
        }

        if (i + 1 >= _parameters.Count) return null;
        int kind = _parameters[i + 1];
        if (kind == 5 && i + 2 < _parameters.Count)
        {
            var color = TerminalColor.FromPalette256(ClampByte(_parameters[i + 2]));
            i += 2;
            return color;
        }
        if (kind == 2 && i + 4 < _parameters.Count)
        {
            var color = TerminalColor.FromRgb(ClampByte(_parameters[i + 2]), ClampByte(_parameters[i + 3]), ClampByte(_parameters[i + 4]));
            i += 4;
            return color;
        }
        i += 1;
        return null;
    }

    private static byte ClampByte(int value) => (byte)Math.Clamp(value, 0, 255);

    private void ResetAttributes()
    {
        _foreground = TerminalColor.Default;
        _background = TerminalColor.Default;
        _attributes = CellAttributes.None;
    }

    private void SetMode(bool enabled)
    {
        if (_privateMarker == '?')
        {
            foreach (var p in _parameters)
            {
                switch (p)
                {
                    case 1: _applicationCursorKeys = enabled; break;
                    case 6: _originMode = enabled; CursorPosition(1, 1); break;
                    case 7: _autoWrap = enabled; break;
                    case 25: _cursorVisible = enabled; MarkDirty(_cursorRow); break;
                    case 47: case 1047: SwitchBuffer(enabled, false); break;
                    case 1048: if (enabled) SaveCursor(); else RestoreCursor(); break;
                    case 1049: SwitchBuffer(enabled, true); break;
                    case 9: _mouseTracking = enabled ? MouseTrackingMode.X10 : MouseTrackingMode.None; break;
                    case 1000: _mouseTracking = enabled ? MouseTrackingMode.Normal : MouseTrackingMode.None; break;
                    case 1002: _mouseTracking = enabled ? MouseTrackingMode.ButtonEvent : MouseTrackingMode.None; break;
                    case 1003: _mouseTracking = enabled ? MouseTrackingMode.AnyEvent : MouseTrackingMode.None; break;
                    case 1006: _sgrMouseMode = enabled; break;
                    case 1007: _alternateScroll = enabled; break;
                    case 2004: _bracketedPasteMode = enabled; break;
                }
            }
        }
        else
        {
            foreach (var p in _parameters)
            {
                switch (p)
                {
                    case 4: _insertMode = enabled; break;
                    case 20: _lineFeedNewLine = enabled; break;
                }
            }
        }
    }

    private void SwitchBuffer(bool useAlternate, bool saveCursor)
    {
        if (useAlternate)
        {
            if (_alternateBuffer == null)
            {
                if (saveCursor) SaveCursor();
                _alternateBuffer = _buffer;
                _buffer = new TerminalBuffer(_width, _height);
                if (saveCursor)
                {
                    // 1049 clears the alternate screen with the cursor at home.
                    _cursorColumn = 0;
                    _cursorRow = 0;
                }
            }
        }
        else
        {
            if (_alternateBuffer != null)
            {
                _buffer = _alternateBuffer;
                _alternateBuffer = null;
                if (saveCursor) RestoreCursor();
            }
        }
        MarkAllDirty();
    }

    private void SaveCursor()
    {
        var state = _alternateBuffer != null ? _savedCursorAlt : _savedCursor;
        state.Column = _cursorColumn;
        state.Row = _cursorRow;
        state.Foreground = _foreground;
        state.Background = _background;
        state.Attributes = _attributes;
        state.OriginMode = _originMode;
        state.AutoWrap = _autoWrap;
    }

    private void RestoreCursor()
    {
        var state = _alternateBuffer != null ? _savedCursorAlt : _savedCursor;
        ClampSavedCursor(state);
        _cursorColumn = state.Column;
        _cursorRow = state.Row;
        _foreground = state.Foreground;
        _background = state.Background;
        _attributes = state.Attributes;
        _originMode = state.OriginMode;
        _autoWrap = state.AutoWrap;
        UpdateTemplate();
    }

    private void ClampSavedCursor(CursorState state)
    {
        state.Column = Math.Clamp(state.Column, 0, _width - 1);
        state.Row = Math.Clamp(state.Row, 0, _height - 1);
    }

    private void DeviceStatusReport(bool privateReport = false)
    {
        foreach (var p in _parameters)
        {
            switch (p)
            {
                case 5: SendData?.Invoke("\x1B[0n"); break;
                case 6:
                    int row = _cursorRow + 1 - (_originMode ? _scrollTop : 0);
                    int col = EditColumn + 1;
                    SendData?.Invoke(privateReport ? $"\x1B[?{row};{col}R" : $"\x1B[{row};{col}R");
                    break;
            }
        }
    }

    private void DeviceAttributes()
    {
        if (GetParam(0, 0) == 0)
            SendData?.Invoke("\x1B[?62;1;2;6;9;15;22c");
    }

    private void SetTabStop()
    {
        if (_cursorColumn < _tabStops.Length) _tabStops[_cursorColumn] = true;
    }

    private void ClearTabStop(int mode)
    {
        if (mode == 0)
        {
            if (_cursorColumn < _tabStops.Length) _tabStops[_cursorColumn] = false;
        }
        else if (mode == 3) Array.Clear(_tabStops, 0, _tabStops.Length);
    }

    /// <summary>DECSTR: resets modes and attributes but keeps the screen content.</summary>
    private void SoftReset()
    {
        _cursorVisible = true;
        _originMode = false;
        _autoWrap = true;
        _insertMode = false;
        _applicationCursorKeys = false;
        _applicationKeypad = false;
        _scrollTop = 0;
        _scrollBottom = _height - 1;
        _g0Charset = CharsetMode.Ascii;
        _g1Charset = CharsetMode.Ascii;
        _glIsG1 = false;
        _savedCursor.Row = _savedCursor.Column = 0;
        ResetAttributes();
        UpdateTemplate();
        MarkDirty(_cursorRow);
    }

    private void FullReset()
    {
        SoftReset();
        _cursorColumn = 0;
        _cursorRow = 0;
        _lineFeedNewLine = false;
        _bracketedPasteMode = false;
        _alternateScroll = true;
        _mouseTracking = MouseTrackingMode.None;
        _sgrMouseMode = false;
        _alternateBuffer = null;
        _buffer.Clear();
        _scrollback.Clear();

        Array.Clear(_tabStops, 0, _tabStops.Length);
        for (int i = 8; i < _tabStops.Length; i += 8) _tabStops[i] = true;

        CursorStyleChanged?.Invoke(0);
        MarkAllDirty();
    }
}
