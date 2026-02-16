using System.Text;
using System.Runtime.CompilerServices;

namespace VT;

/// <summary>
/// Main VT100/XTerm terminal controller
/// </summary>
public class VirtualTerminal(int width = 80, int height = 24)
{
    private TerminalBuffer _buffer = new(width, height);
    private TerminalBuffer? _alternateBuffer;
    private readonly List<TerminalLine> _scrollback = [];
    
    // Performance: Dirty tracking to optimize rendering downstream
    private int _dirtyTop = int.MaxValue;
    private int _dirtyBottom = int.MinValue;
    
    public int DirtyTop => _dirtyTop;
    public int DirtyBottom => _dirtyBottom;

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
    private TerminalColor _foreground = TerminalColor.White;
    private TerminalColor _background = TerminalColor.Default;
    private TerminalAttribute _attributes = TerminalAttribute.None;
    
    // Cached template for fast writing (avoids struct construction overhead in loops)
    private TerminalCharacter _charTemplate = new() { Foreground = TerminalColor.White, Background = TerminalColor.Default };
    
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
    private bool _bracketedPasteMode;
    
    private enum ParserState { Ground, Escape, CsiEntry, CsiParam, CsiIntermediate, OscString, DcsEntry, Charset }
    private ParserState _state = ParserState.Ground;
    
    private readonly List<int> _parameters = new(16);
    private int _currentParam;
    private bool _hasCurrentParam;
    
    private readonly StringBuilder _oscBuffer = new(256);
    private char _intermediate;

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
    
    public event Action? ScreenChanged;
    public event Action<string>? TitleChanged;
    public event Action<string>? SendData;

    private static bool[] InitTabStops(int size)
    {
        var tabs = new bool[size];
        for (int i = 8; i < size; i += 8) tabs[i] = true;
        return tabs;
    }

    private void MarkDirty(int row)
    {
        if (row < _dirtyTop) _dirtyTop = row;
        if (row > _dirtyBottom) _dirtyBottom = row;
    }

    private void MarkDirty(int startRow, int endRow)
    {
        if (startRow < _dirtyTop) _dirtyTop = startRow;
        if (endRow > _dirtyBottom) _dirtyBottom = endRow;
    }

    private void UpdateTemplate()
    {
        _charTemplate.Foreground = _foreground;
        _charTemplate.Background = _background;
        _charTemplate.Attributes = _attributes;
        _charTemplate.Char = ' '; 
    }

    public void Resize(int width, int height)
    {
        _width = width;
        _height = height;
        _buffer.Resize(width, height);
        _alternateBuffer?.Resize(width, height);
        _scrollBottom = Math.Min(_scrollBottom, height - 1);
        _cursorColumn = Math.Min(_cursorColumn, width - 1);
        _cursorRow = Math.Min(_cursorRow, height - 1);
        
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
        _dirtyTop = int.MaxValue;
        _dirtyBottom = int.MinValue;
        
        ProcessTextFast(text.AsSpan());
        
        if (_dirtyTop <= _dirtyBottom)
            ScreenChanged?.Invoke();
    }

    public void Feed(ReadOnlySpan<char> chars)
    {
        _dirtyTop = int.MaxValue;
        _dirtyBottom = int.MinValue;

        ProcessTextFast(chars);

        if (_dirtyTop <= _dirtyBottom)
            ScreenChanged?.Invoke();
    }

    public void Feed(ReadOnlySpan<byte> data)
    {
        _dirtyTop = int.MaxValue;
        _dirtyBottom = int.MinValue;

        int charCount = _utf8Decoder.GetCharCount(data, false);
        if (_charBuffer.Length < charCount)
            Array.Resize(ref _charBuffer, Math.Max(charCount, _charBuffer.Length * 2));

        int charsUsed = _utf8Decoder.GetChars(data, _charBuffer, false);
        ProcessTextFast(_charBuffer.AsSpan(0, charsUsed));
        
        if (_dirtyTop <= _dirtyBottom)
            ScreenChanged?.Invoke();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ProcessTextFast(ReadOnlySpan<char> text)
    {
        int currentPos = 0;
        int len = text.Length;

        while (currentPos < len)
        {
            if (_state == ParserState.Ground)
            {
                int i = currentPos;
                // Fast path for printable ASCII
                while (i < len)
                {
                    char c = text[i];
                    if (c < 32 || c == 127) break;
                    i++;
                }

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
            case ParserState.CsiEntry:
            case ParserState.CsiParam:
            case ParserState.CsiIntermediate: ProcessCsiState(c); break;
            case ParserState.OscString: ProcessOscState(c); break;
            case ParserState.Charset: _state = ParserState.Ground; break;
        }
    }

    private void ProcessGroundState(char c)
    {
        if (c >= 32 && c != 127)
        {
            PutChar(c);
            return;
        }

        switch (c)
        {
            case '\x1B': _state = ParserState.Escape; break;
            case '\r': _cursorColumn = 0; break;
            case '\n' or '\x0B' or '\x0C': LineFeed(); break;
            case '\b': if (_cursorColumn > 0) _cursorColumn--; break;
            case '\t': Tab(); break;
            case '\a': break; // Bell
            case (char)127: break; // DEL
            case '\x0E' or '\x0F': break; // Shift IN/OUT
        }
    }

    private void ProcessEscapeState(char c)
    {
        switch (c)
        {
            case '[':
                _state = ParserState.CsiEntry;
                _parameters.Clear();
                _currentParam = 0;
                _hasCurrentParam = false;
                _intermediate = '\0';
                break;
            case ']':
                _state = ParserState.OscString;
                _oscBuffer.Clear();
                break;
            case '(' or ')' or '*' or '+': _state = ParserState.Charset; break;
            case '7': SaveCursor(); _state = ParserState.Ground; break;
            case '8': RestoreCursor(); _state = ParserState.Ground; break;
            case 'D': LineFeed(); _state = ParserState.Ground; break;
            case 'E': _cursorColumn = 0; LineFeed(); _state = ParserState.Ground; break;
            case 'M': ReverseIndex(); _state = ParserState.Ground; break;
            case 'c': FullReset(); _state = ParserState.Ground; break;
            case 'H': SetTabStop(); _state = ParserState.Ground; break;
            case '=' or '>': _state = ParserState.Ground; break;
            default: _state = ParserState.Ground; break;
        }
    }

    private void ProcessCsiState(char c)
    {
        if (c >= '0' && c <= '9')
        {
            _currentParam = _currentParam * 10 + (c - '0');
            _hasCurrentParam = true;
            _state = ParserState.CsiParam;
        }
        else if (c == ';')
        {
            _parameters.Add(_hasCurrentParam ? _currentParam : 0);
            _currentParam = 0;
            _hasCurrentParam = false;
        }
        else if (c == '?' || c == '>' || c == '!' || c == '"' || c == '\'' || c == ' ')
        {
            _intermediate = c;
        }
        else if (c >= 0x40 && c <= 0x7E)
        {
            if (_hasCurrentParam) _parameters.Add(_currentParam);
            ExecuteCsi(c);
            _state = ParserState.Ground;
        }
        else if (c == '\x1B')
        {
            _state = ParserState.Escape;
        }
    }

    private void ProcessOscState(char c)
    {
        if (c == '\x07' || c == '\x9C')
        {
            ExecuteOsc();
            _state = ParserState.Ground;
        }
        else if (c == '\x1B')
        {
            _state = ParserState.Ground;
            ExecuteOsc();
        }
        else if (_oscBuffer.Length < 1024)
        {
            _oscBuffer.Append(c);
        }
    }

    private int GetParam(int index, int defaultValue = 1) =>
        index < _parameters.Count && _parameters[index] > 0 ? _parameters[index] : defaultValue;

    private void ExecuteCsi(char cmd)
    {
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

        // Case 1: Text fits in current line
        if (text.Length <= remainingInLine)
        {
            for (int i = 0; i < text.Length; i++)
            {
                cell.Char = text[i];
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
                int len = remainingInLine;
                for (int i = 0; i < len; i++)
                {
                    cell.Char = text[i];
                    line[_cursorColumn + i] = cell;
                }
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
                    
                    for (int i = 0; i < chunk; i++)
                    {
                        cell.Char = text[processed + i];
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

    private void PutChar(char c)
    {
        // DELAYED WRAP CHECK
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

        if (_insertMode)
            _buffer[_cursorRow].InsertCharacters(_cursorColumn, 1);

        var line = _buffer[_cursorRow];
        
        var cell = _charTemplate;
        cell.Char = c;
        line[_cursorColumn] = cell;

        _cursorColumn++;
    }

    private void LineFeed()
    {
        if (_cursorRow >= _scrollBottom)
        {
            if (_scrollTop == 0 && _alternateBuffer == null)
            {
                _scrollback.Add(_buffer[0].Clone());
                
                if (_scrollback.Count > MaxScrollback + 100)
                {
                    _scrollback.RemoveRange(0, 100);
                }
            }
            _buffer.ScrollUp(_scrollTop, _scrollBottom);
            MarkDirty(_scrollTop, _scrollBottom);
        }
        else
        {
            _cursorRow++;
        }
        
        if (_lineFeedNewLine)
            _cursorColumn = 0;
    }

    private void ReverseIndex()
    {
        if (_cursorRow <= _scrollTop)
        {
            _buffer.ScrollDown(_scrollTop, _scrollBottom);
            MarkDirty(_scrollTop, _scrollBottom);
        }
        else
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

    private void CursorUp(int count) => _cursorRow = Math.Max(_scrollTop, _cursorRow - count);
    private void CursorDown(int count) => _cursorRow = Math.Min(_scrollBottom, _cursorRow + count);
    private void CursorForward(int count) => _cursorColumn = Math.Min(_width - 1, _cursorColumn + count);
    private void CursorBackward(int count) => _cursorColumn = Math.Max(0, _cursorColumn - count);
    private void CursorNextLine(int count) { CursorDown(count); _cursorColumn = 0; }
    private void CursorPrevLine(int count) { CursorUp(count); _cursorColumn = 0; }
    private void CursorCharAbsolute(int column) => _cursorColumn = Math.Clamp(column - 1, 0, _width - 1);
    private void CursorLineAbsolute(int row) => _cursorRow = Math.Clamp(row - 1, 0, _height - 1);
    
    private void CursorPosition(int row, int column)
    {
        int baseRow = _originMode ? _scrollTop : 0;
        _cursorRow = Math.Clamp(baseRow + row - 1, 0, _height - 1);
        _cursorColumn = Math.Clamp(column - 1, 0, _width - 1);
    }

    private void EraseInDisplay(int mode)
    {
        var fillChar = _charTemplate;
        fillChar.Char = ' ';
        
        switch (mode)
        {
            case 0:
                EraseLineSection(_cursorRow, _cursorColumn, _width - 1, fillChar);
                for (int y = _cursorRow + 1; y < _height; y++)
                    EraseLineWhole(y, fillChar);
                MarkDirty(_cursorRow, _height - 1);
                break;
            case 1:
                for (int y = 0; y < _cursorRow; y++)
                    EraseLineWhole(y, fillChar);
                EraseLineSection(_cursorRow, 0, _cursorColumn, fillChar);
                MarkDirty(0, _cursorRow);
                break;
            case 2:
            case 3:
                for (int y = 0; y < _height; y++)
                    EraseLineWhole(y, fillChar);
                if (mode == 3) _scrollback.Clear();
                MarkDirty(0, _height - 1);
                break;
        }
    }

    private void EraseInLine(int mode)
    {
        var fillChar = _charTemplate;
        fillChar.Char = ' ';
        MarkDirty(_cursorRow);
        
        switch (mode)
        {
            case 0: EraseLineSection(_cursorRow, _cursorColumn, _width - 1, fillChar); break;
            case 1: EraseLineSection(_cursorRow, 0, _cursorColumn, fillChar); break;
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
        for (int i = start; i < limit; i++) line[i] = fill;
    }

    private void InsertLines(int count) 
    { 
        _buffer.InsertLines(_cursorRow, count, _scrollBottom); 
        MarkDirty(_cursorRow, _scrollBottom); 
    }
    
    private void DeleteLines(int count) 
    { 
        _buffer.DeleteLines(_cursorRow, count, _scrollBottom); 
        MarkDirty(_cursorRow, _scrollBottom); 
    }
    
    private void InsertCharacters(int count) 
    { 
        _buffer[_cursorRow].InsertCharacters(_cursorColumn, count); 
        MarkDirty(_cursorRow); 
    }
    
    private void DeleteCharacters(int count) 
    { 
        _buffer[_cursorRow].DeleteCharacters(_cursorColumn, count); 
        MarkDirty(_cursorRow); 
    }
    
    private void EraseCharacters(int count)
    {
        var fillChar = _charTemplate;
        fillChar.Char = ' ';
        EraseLineSection(_cursorRow, _cursorColumn, _cursorColumn + count - 1, fillChar);
        MarkDirty(_cursorRow);
    }
    
    private void ScrollUp(int count) 
    { 
        _buffer.ScrollUp(_scrollTop, _scrollBottom, count); 
        MarkDirty(_scrollTop, _scrollBottom); 
    }
    
    private void ScrollDown(int count) 
    { 
        _buffer.ScrollDown(_scrollTop, _scrollBottom, count); 
        MarkDirty(_scrollTop, _scrollBottom); 
    }

    private void SetScrollRegion(int top, int bottom)
    {
        _scrollTop = Math.Clamp(top - 1, 0, _height - 1);
        _scrollBottom = Math.Clamp(bottom - 1, _scrollTop, _height - 1);
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
            switch (p)
            {
                case 0: ResetAttributes(); break;
                case 1: _attributes |= TerminalAttribute.Bold; break;
                case 2: _attributes |= TerminalAttribute.Dim; break;
                case 3: _attributes |= TerminalAttribute.Italic; break;
                case 4: _attributes |= TerminalAttribute.Underline; break;
                case 5: _attributes |= TerminalAttribute.Blink; break;
                case 7: _attributes |= TerminalAttribute.Inverse; break;
                case 8: _attributes |= TerminalAttribute.Hidden; break;
                case 9: _attributes |= TerminalAttribute.Strikethrough; break;
                case 21: _attributes |= TerminalAttribute.DoubleUnderline; break;
                case 22: _attributes &= ~(TerminalAttribute.Bold | TerminalAttribute.Dim); break;
                case 23: _attributes &= ~TerminalAttribute.Italic; break;
                case 24: _attributes &= ~(TerminalAttribute.Underline | TerminalAttribute.DoubleUnderline); break;
                case 25: _attributes &= ~TerminalAttribute.Blink; break;
                case 27: _attributes &= ~TerminalAttribute.Inverse; break;
                case 28: _attributes &= ~TerminalAttribute.Hidden; break;
                case 29: _attributes &= ~TerminalAttribute.Strikethrough; break;
                case >= 30 and <= 37: _foreground = new TerminalColor(p - 30); break;
                case 38:
                    i++;
                    if (i < _parameters.Count)
                    {
                        if (_parameters[i] == 5 && i + 1 < _parameters.Count)
                        {
                            _foreground = TerminalColor.FromPalette256(_parameters[++i]);
                        }
                        else if (_parameters[i] == 2 && i + 3 < _parameters.Count)
                        {
                            _foreground = TerminalColor.FromRgb(
                                (byte)_parameters[++i], 
                                (byte)_parameters[++i], 
                                (byte)_parameters[++i]);
                        }
                    }
                    break;
                case 39: _foreground = TerminalColor.White; break;
                case >= 40 and <= 47: _background = new TerminalColor(p - 40); break;
                case 48:
                    i++;
                    if (i < _parameters.Count)
                    {
                        if (_parameters[i] == 5 && i + 1 < _parameters.Count)
                        {
                            _background = TerminalColor.FromPalette256(_parameters[++i]);
                        }
                        else if (_parameters[i] == 2 && i + 3 < _parameters.Count)
                        {
                            _background = TerminalColor.FromRgb(
                                (byte)_parameters[++i], 
                                (byte)_parameters[++i], 
                                (byte)_parameters[++i]);
                        }
                    }
                    break;
                case 49: _background = TerminalColor.Default; break;
                case >= 90 and <= 97: _foreground = new TerminalColor(p - 90 + 8); break;
                case >= 100 and <= 107: _background = new TerminalColor(p - 100 + 8); break;
            }
        }
        UpdateTemplate();
    }

    private void ResetAttributes()
    {
        _foreground = TerminalColor.White;
        _background = TerminalColor.Default;
        _attributes = TerminalAttribute.None;
    }

    private void SetMode(bool enabled)
    {
        if (_intermediate == '?')
        {
            foreach (var p in _parameters)
            {
                switch (p)
                {
                    case 1: _applicationCursorKeys = enabled; break;
                    case 6: _originMode = enabled; break;
                    case 7: _autoWrap = enabled; break;
                    case 25: _cursorVisible = enabled; break;
                    case 47: case 1047: SwitchBuffer(enabled, false); break;
                    case 1049: SwitchBuffer(enabled, true); break;
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
        MarkDirty(0, _height - 1);
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
        _cursorColumn = state.Column;
        _cursorRow = state.Row;
        _foreground = state.Foreground;
        _background = state.Background;
        _attributes = state.Attributes;
        _originMode = state.OriginMode;
        _autoWrap = state.AutoWrap;
        UpdateTemplate();
    }

    private void DeviceStatusReport()
    {
        foreach (var p in _parameters)
        {
            switch (p)
            {
                case 5: SendData?.Invoke("\x1B[0n"); break; 
                case 6: SendData?.Invoke($"\x1B[{_cursorRow + 1};{_cursorColumn + 1}R"); break; 
            }
        }
    }

    private void DeviceAttributes()
    {
        if (_intermediate == '>') SendData?.Invoke("\x1B[>0;0;0c"); 
        else SendData?.Invoke("\x1B[?62;c"); 
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

    private void FullReset()
    {
        _scrollTop = 0;
        _scrollBottom = _height - 1;
        _cursorColumn = 0;
        _cursorRow = 0;
        _originMode = false;
        _autoWrap = true;
        _insertMode = false;
        _cursorVisible = true;
        ResetAttributes();
        _buffer.Clear();
        _alternateBuffer = null;
        _scrollback.Clear();
        
        Array.Clear(_tabStops, 0, _tabStops.Length);
        for (int i = 8; i < _tabStops.Length; i += 8) _tabStops[i] = true;
        
        UpdateTemplate();
        MarkDirty(0, _height - 1);
    }
}