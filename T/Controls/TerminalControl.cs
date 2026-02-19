using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using VT;
using T.ViewModels;
using T.Services;

namespace T.Controls;

public enum TerminalCursorStyle { Block, Bar, Underline }

public class TerminalControl : Control
{
    private VirtualTerminal? _terminal;
    private double _charWidth;
    private double _lineHeight;
    private bool _cursorBlink = true;
    private bool _hasFocus;
    private readonly DispatcherTimer _cursorBlinkTimer;
    private readonly StringBuilder _textRunBuilder = new(512);

    // Input buffering / throttling to prevent infinite-stream (e.g. "yes") from starving UI
    private readonly StringBuilder _incomingBuffer = new(16 * 1024);
    private readonly Lock _incomingLock = new();
    private readonly DispatcherTimer _inputTimer;
    private const int InputProcessIntervalMs = 16; // how often we process incoming data (ms)
    private const int InputProcessMaxChars = 4096; // max chars processed per tick
    private const int MaxInputBufferSize = 200_000; // safety: trim buffer when exceeding this
    private const int TrimToSize = 100_000; // when trimming, keep last N chars

    // Render coalescing
    private readonly DispatcherTimer _renderTimer;
    private int _pendingDirtyTop = int.MaxValue;
    private int _pendingDirtyBottom = int.MinValue;
    private const int RenderCoalesceMs = 16; // ~60 FPS cap for bursts

    // FormattedText cache (reduces GC pressure)
    private readonly FormattedTextCache _formattedTextCache = new(1024);

    private class RowRenderCache
    {
        public readonly List<(double X, FormattedText Text, bool Underline, Color Foreground)> TextRuns = new();
        public readonly List<(Rect Rect, Brush Brush)> Backgrounds = new();
        public bool IsDirty = true;

        public void Clear()
        {
            TextRuns.Clear();
            Backgrounds.Clear();
            IsDirty = true;
        }
    }

    private RowRenderCache[] _rowCaches = [];

    private bool _isSelecting;
    private int _selectionStartCol, _selectionStartRow;
    private int _selectionEndCol, _selectionEndRow;

    private int _scrollOffset;

    private uint _terminalColumns;
    private uint _terminalRows;

    private readonly Dictionary<Color, SolidColorBrush> _brushCache = [];
    private Typeface _cachedTypeface = new();
    private Typeface _cachedBoldTypeface = new();

    #region Styled Properties

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        AvaloniaProperty.Register<TerminalControl, FontFamily>(nameof(FontFamily),
            new FontFamily("Consolas, Courier New, monospace"));

    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<TerminalControl, double>(nameof(FontSize), 14);

    public static readonly StyledProperty<Color> DefaultForegroundProperty =
        AvaloniaProperty.Register<TerminalControl, Color>(nameof(DefaultForeground),
            ToAvaloniaColor(TerminalColor.White));

    public static readonly StyledProperty<Color> DefaultBackgroundProperty =
        AvaloniaProperty.Register<TerminalControl, Color>(nameof(DefaultBackground),
            ToAvaloniaColor(TerminalColor.Black));

    public static readonly StyledProperty<Color> CursorColorProperty =
        AvaloniaProperty.Register<TerminalControl, Color>(nameof(CursorColor), Colors.White);

    public static readonly StyledProperty<Thickness> PaddingProperty =
        AvaloniaProperty.Register<TerminalControl, Thickness>(nameof(Padding), new Thickness(0));

    public static readonly StyledProperty<CornerRadius> CornerRadiusProperty =
        AvaloniaProperty.Register<TerminalControl, CornerRadius>(nameof(CornerRadius), new CornerRadius(0));

    public static readonly StyledProperty<TerminalCursorStyle> CursorStyleProperty =
        AvaloniaProperty.Register<TerminalControl, TerminalCursorStyle>(nameof(CursorStyle), TerminalCursorStyle.Bar);

    public static readonly StyledProperty<bool> ShowStatsOverlayProperty =
        AvaloniaProperty.Register<TerminalControl, bool>(nameof(ShowStatsOverlay), true);

    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public Color DefaultForeground
    {
        get => GetValue(DefaultForegroundProperty);
        set => SetValue(DefaultForegroundProperty, value);
    }

    public Color DefaultBackground
    {
        get => GetValue(DefaultBackgroundProperty);
        set => SetValue(DefaultBackgroundProperty, value);
    }

    public Color CursorColor
    {
        get => GetValue(CursorColorProperty);
        set => SetValue(CursorColorProperty, value);
    }

    public Thickness Padding
    {
        get => GetValue(PaddingProperty);
        set => SetValue(PaddingProperty, value);
    }

    public CornerRadius CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public TerminalCursorStyle CursorStyle
    {
        get => GetValue(CursorStyleProperty);
        set => SetValue(CursorStyleProperty, value);
    }

    public bool ShowStatsOverlay
    {
        get => GetValue(ShowStatsOverlayProperty);
        set => SetValue(ShowStatsOverlayProperty, value);
    }

    #endregion

    public event Action<uint, uint, uint, uint>? TerminalResized;
    public event Action<string>? InputReceived;

    public TerminalControl()
    {
        Focusable = true;
        IsTabStop = true;
        ClipToBounds = true;

        _cursorBlinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
        _cursorBlinkTimer.Tick += (_, _) =>
        {
            _cursorBlink = !_cursorBlink;
            InvalidateVisual();
        };

        // Input processing timer - processes buffered incoming text in chunks
        _inputTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(InputProcessIntervalMs) };
        _inputTimer.Tick += (_, _) => ProcessInputBuffer();

        // Render coalescing timer: collect small updates and render once per interval
        _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(RenderCoalesceMs) };
        _renderTimer.Tick += (_, _) =>
        {
            _renderTimer.Stop();
            ProcessPendingChanges();
        };
    }

    static TerminalControl()
    {
        FontSizeProperty.Changed.AddClassHandler<TerminalControl>((x, _) => x.UpdateMetrics());
        FontFamilyProperty.Changed.AddClassHandler<TerminalControl>((x, _) => x.UpdateMetrics());
        PaddingProperty.Changed.AddClassHandler<TerminalControl>((x, _) => x.OnPaddingChanged());
    }

    private void OnPaddingChanged()
    {
        if (_terminal != null && Bounds.Width > 0 && Bounds.Height > 0)
        {
            InvalidateArrange();
        }
    }

    #region Public API

    // AppendOutput now buffers incoming text; heavy streams are throttled
    public void AppendOutput(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        lock (_incomingLock)
        {
            // safety trim: keep buffer bounded to avoid OOM with infinite producers
            if (_incomingBuffer.Length + text.Length > MaxInputBufferSize)
            {
                // keep the last TrimToSize chars
                if (_incomingBuffer.Length > TrimToSize)
                {
                    _incomingBuffer.Remove(0, _incomingBuffer.Length - TrimToSize);
                }
                // if still too big, drop oldest to make room
                if (_incomingBuffer.Length + text.Length > MaxInputBufferSize)
                {
                    int toDrop = (_incomingBuffer.Length + text.Length) - MaxInputBufferSize;
                    if (toDrop >= _incomingBuffer.Length)
                        _incomingBuffer.Clear();
                    else
                        _incomingBuffer.Remove(0, toDrop);
                }
            }

            _incomingBuffer.Append(text);

            // Ensure input processing timer is running
            if (!_inputTimer.IsEnabled)
                _inputTimer.Start();
        }
    }

    // Processes a chunk of incoming data per tick to avoid locking UI for long periods
    private void ProcessInputBuffer()
    {
        if (_terminal == null)
        {
            lock (_incomingLock) { _incomingBuffer.Clear(); }
            _inputTimer.Stop();
            return;
        }

        string chunk;
        lock (_incomingLock)
        {
            if (_incomingBuffer.Length == 0)
            {
                _inputTimer.Stop();
                return;
            }

            int len = Math.Min(InputProcessMaxChars, _incomingBuffer.Length);
            chunk = _incomingBuffer.ToString(0, len);
            _incomingBuffer.Remove(0, len);
        }

        // Feed the terminal (this may trigger ScreenChanged -> OnTerminalScreenChanged)
        try
        {
            _terminal.Feed(chunk);
        }
        catch
        {
            // swallow parsing errors to keep control responsive
        }
    }

    /// <summary>
    /// Return a snapshot of formatted-text cache statistics for UI display.
    /// </summary>
    public FormattedTextCache.Stats GetFormattedTextCacheStats() => _formattedTextCache.GetStats();

    /// <summary>
    /// Clear formatted-text cache and reset its statistics.
    /// </summary>
    public void ClearFormattedTextCache() => _formattedTextCache.Clear();

    public (uint Columns, uint Rows, uint PixelWidth, uint PixelHeight) GetTerminalSize()
    {
        var padding = Padding;
        return (_terminalColumns, _terminalRows,
            (uint)Math.Max(0, Bounds.Width - padding.Left - padding.Right),
            (uint)Math.Max(0, Bounds.Height - padding.Top - padding.Bottom));
    }

    public void PopulateStatsViewModel(TerminalStatsViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);

        // Snapshot cache stats
        var cacheStats = _formattedTextCache.GetStats();

        int incomingLen;
        lock (_incomingLock) { incomingLen = _incomingBuffer.Length; }

        // Terminal size (columns/rows) and scrollback count
        var (cols, rows, _, _) = GetTerminalSize();
        int scrollback = _terminal?.Scrollback.Count ?? 0;

        vm.Columns = (int)cols;
        vm.Rows = (int)rows;
        vm.Scrollback = scrollback;
        vm.IncomingLen = incomingLen;
        vm.CharWidth = _charWidth;
        vm.LineHeight = _lineHeight;

        vm.Hits = cacheStats.Hits;
        vm.Misses = cacheStats.Misses;
        vm.Inserts = cacheStats.Inserts;
        vm.Evictions = cacheStats.Evictions;
        vm.CurrentCount = cacheStats.CurrentCount;
        vm.Capacity = cacheStats.Capacity;
    }

    #endregion

    #region Layout & Metrics

    private static Color ToAvaloniaColor(TerminalColor tc)
    {
        var (r, g, b) = tc.ToRgb();
        return Color.FromRgb(r, g, b);
    }

    private static Color ResolveColor(TerminalColor color, Color defaultColor)
    {
        if (color.IsDefault) return defaultColor;
        var (r, g, b) = color.ToRgb();
        return Color.FromRgb(r, g, b);
    }

    private SolidColorBrush GetBrush(Color color)
    {
        if (!_brushCache.TryGetValue(color, out var brush))
        {
            if (_brushCache.Count > 256)
                _brushCache.Clear();
            brush = new SolidColorBrush(color);
            _brushCache[color] = brush;
        }
        return brush;
    }

    private void UpdateMetrics()
    {
        _cachedTypeface = new Typeface(FontFamily);
        _cachedBoldTypeface = new Typeface(FontFamily, FontStyle.Normal, FontWeight.Bold);

        var testText = new FormattedText("M", CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, _cachedTypeface, FontSize, Brushes.White);

        var newCharWidth = testText.WidthIncludingTrailingWhitespace;
        var newLineHeight = testText.Height;

        bool changed = Math.Abs(_charWidth - newCharWidth) > 0.01 || Math.Abs(_lineHeight - newLineHeight) > 0.01;

        _charWidth = newCharWidth;
        _lineHeight = newLineHeight;

        if (changed)
        {
            InvalidateAllRowCaches();
            InvalidateMeasure();
        }
        InvalidateVisual();
    }

    private void InvalidateAllRowCaches()
    {
        foreach (var c in _rowCaches) c.Clear();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateMetrics();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _cursorBlinkTimer.Stop();
        if (_renderTimer.IsEnabled) _renderTimer.Stop();
        if (_inputTimer.IsEnabled) _inputTimer.Stop();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_charWidth <= 0 || _lineHeight <= 0) UpdateMetrics();
        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (finalSize.Width <= 10 || finalSize.Height <= 10) return finalSize;
        if (_charWidth <= 0 || _lineHeight <= 0) UpdateMetrics();
        if (_charWidth <= 0 || _lineHeight <= 0) return finalSize;

        var padding = Padding;
        var availableWidth = Math.Max(0, finalSize.Width - padding.Left - padding.Right);
        var availableHeight = Math.Max(0, finalSize.Height - padding.Top - padding.Bottom);

        var newColumns = (uint)Math.Max(20, availableWidth / _charWidth);
        var newRows = (uint)Math.Max(5, availableHeight / _lineHeight);

        bool colsChanged = newColumns != _terminalColumns;
        bool rowsChanged = newRows != _terminalRows;

        if (_terminal == null)
        {
            _terminalColumns = newColumns;
            _terminalRows = newRows;
            ResizeCache((int)newRows);

            _terminal = new VirtualTerminal((int)newColumns, (int)newRows);
            _terminal.ScreenChanged += OnTerminalScreenChanged;

            TerminalResized?.Invoke(newColumns, newRows, (uint)availableWidth, (uint)availableHeight);
        }
        else if (colsChanged || rowsChanged)
        {
            _terminalColumns = newColumns;
            _terminalRows = newRows;
            ResizeCache((int)newRows);
            InvalidateAllRowCaches();

            _terminal.Resize((int)newColumns, (int)newRows);

            TerminalResized?.Invoke(newColumns, newRows, (uint)availableWidth, (uint)availableHeight);
        }

        return finalSize;
    }

    private void ResizeCache(int rows)
    {
        if (_rowCaches.Length != rows)
        {
            var newCache = new RowRenderCache[rows];
            for (int i = 0; i < rows; i++) newCache[i] = new RowRenderCache();
            _rowCaches = newCache;
        }
    }

    // Collect dirty ranges and coalesce render calls to reduce redraw frequency
    private void OnTerminalScreenChanged()
    {
        if (_terminal == null || _rowCaches.Length == 0) return;

        if (_terminal.DirtyTop <= _terminal.DirtyBottom)
        {
            int top = Math.Max(0, _terminal.DirtyTop);
            int bottom = Math.Min(_rowCaches.Length - 1, _terminal.DirtyBottom);

            // accumulate pending range
            _pendingDirtyTop = Math.Min(_pendingDirtyTop, top);
            _pendingDirtyBottom = Math.Max(_pendingDirtyBottom, bottom);

            // start/keep the short timer running to batch updates
            if (!_renderTimer.IsEnabled)
                _renderTimer.Start();
        }
    }

    // Apply pending changes (clear caches for dirty rows and trigger one InvalidateVisual)
    private void ProcessPendingChanges()
    {
        if (_pendingDirtyTop == int.MaxValue || _pendingDirtyBottom == int.MinValue) return;

        int top = _pendingDirtyTop;
        int bottom = _pendingDirtyBottom;
        _pendingDirtyTop = int.MaxValue;
        _pendingDirtyBottom = int.MinValue;

        top = Math.Max(0, Math.Min(top, _rowCaches.Length - 1));
        bottom = Math.Max(0, Math.Min(bottom, _rowCaches.Length - 1));
        for (int i = top; i <= bottom; i++)
            _rowCaches[i].Clear();

        // single invalidate for the batch
        InvalidateVisual();
    }

    #endregion

    #region Rendering

    public override void Render(DrawingContext context)
    {
        if (_terminal == null) return;

        var defaultBg = DefaultBackground;
        var bounds = new Rect(Bounds.Size);
        var cornerRadius = CornerRadius;

        var bgBrush = GetBrush(defaultBg);
        if (cornerRadius != default)
        {
            var roundedRect = new RoundedRect(bounds, cornerRadius.TopLeft, cornerRadius.TopRight, cornerRadius.BottomRight, cornerRadius.BottomLeft);
            context.DrawRectangle(bgBrush, null, roundedRect);
            using (context.PushClip(roundedRect))
            {
                RenderContent(context);
            }
        }
        else
        {
            context.FillRectangle(bgBrush, bounds);
            RenderContent(context);
        }
    }

    private void RenderContent(DrawingContext context)
    {
        var terminal = _terminal;
        if (terminal == null) return;

        int width = terminal.Width;
        int height = terminal.Height;
        int scrollbackCount = terminal.Scrollback.Count;
        int visibleStart = Math.Max(0, scrollbackCount - _scrollOffset);

        int cursorCol = terminal.CursorColumn;
        int cursorRow = terminal.CursorRow;

        bool isCursorValid = _scrollOffset == 0 && cursorCol >= 0 && cursorCol < width && cursorRow >= 0 && cursorRow < height;
        bool hasSelection = HasSelection();
        (int selStartRow, int selEndRow) = hasSelection ? OrderedRows() : (-1, -1);

        double offsetX = Padding.Left;
        double offsetY = Padding.Top;
        var defaultFg = DefaultForeground;
        var defaultBg = DefaultBackground;

        var selectionBrush = GetBrush(Color.FromArgb(100, 51, 153, 255));

        for (int screenRow = 0; screenRow < height; screenRow++)
        {
            if (screenRow >= _rowCaches.Length) break;

            int bufferIndex = visibleStart + screenRow;
            bool isHistory = bufferIndex < scrollbackCount;
            int rowInBuffer = isHistory ? bufferIndex : (bufferIndex - scrollbackCount);

            if (!isHistory && rowInBuffer >= terminal.Height) break;

            double rowY = offsetY + screenRow * _lineHeight;

            var cache = _rowCaches[screenRow];
            if (cache.IsDirty)
            {
                cache.IsDirty = false;
                BuildRowCache(cache, terminal, rowInBuffer, width, defaultFg, defaultBg, isHistory);
            }

            foreach (var bg in cache.Backgrounds)
            {
                var r = bg.Rect;
                context.FillRectangle(bg.Brush, new Rect(r.X + offsetX, r.Y + rowY, r.Width, r.Height));
            }

            foreach (var run in cache.TextRuns)
            {
                context.DrawText(run.Text, new Point(run.X + offsetX, rowY));
                if (run.Underline)
                {
                    double y = rowY + _lineHeight - 1;
                    context.DrawLine(new Pen(GetBrush(run.Foreground), 1),
                       new Point(run.X + offsetX, y),
                       new Point(run.X + offsetX + run.Text.Width, y));
                }
            }

            if (hasSelection && screenRow >= selStartRow && screenRow <= selEndRow)
            {
                var (sc, ec) = GetSelectionRangeForRow(screenRow);
                if (sc < ec)
                {
                    var selRect = new Rect(offsetX + sc * _charWidth, rowY, (ec - sc) * _charWidth, _lineHeight);
                    context.FillRectangle(selectionBrush, selRect);
                }
            }

            if (isCursorValid && !isHistory && rowInBuffer == cursorRow)
            {
                double cx = offsetX + cursorCol * _charWidth;
                DrawCursor(context, cx, rowY, CursorStyle, CursorColor);
            }
        }
    }

    private void BuildRowCache(RowRenderCache cache, VirtualTerminal terminal, int rowInBuffer, int width, Color defaultFg, Color defaultBg, bool isHistory)
    {
        int bgRunStart = -1;
        Color currentBg = defaultBg;

        int textRunStart = -1;
        Color currentTextFg = defaultFg;
        bool currentBold = false;
        bool currentUnderline = false;
        _textRunBuilder.Clear();

        for (int col = 0; col < width; col++)
        {
            TerminalCharacter cell;
            if (isHistory)
            {
                var line = terminal.Scrollback[rowInBuffer];
                cell = col < line.Length ? line[col] : TerminalCharacter.Blank;
            }
            else
            {
                cell = terminal.GetCell(col, rowInBuffer);
            }

            var cellBg = cell.Background.IsDefault ? defaultBg : ResolveColor(cell.Background, defaultBg);
            var cellFg = cell.Foreground.IsDefault ? defaultFg : ResolveColor(cell.Foreground, defaultFg);
            if (cell.IsInverse) (cellFg, cellBg) = (cellBg, cellFg);

            if (cellBg != currentBg)
            {
                if (bgRunStart != -1 && currentBg != defaultBg)
                {
                    double runX = bgRunStart * _charWidth;
                    double runW = (col - bgRunStart) * _charWidth;
                    cache.Backgrounds.Add((new Rect(runX, 0, runW, _lineHeight), GetBrush(currentBg)));
                }
                currentBg = cellBg;
                bgRunStart = col;
            }

            char c = (cell.Char == '\0' || cell.IsHidden) ? ' ' : cell.Char;

            if (cellFg != currentTextFg || cell.IsBold != currentBold || cell.IsUnderline != currentUnderline)
            {
                AddTextRunToCache(cache, _textRunBuilder, textRunStart, currentTextFg, currentBold, currentUnderline);
                textRunStart = col;
                currentTextFg = cellFg;
                currentBold = cell.IsBold;
                currentUnderline = cell.IsUnderline;
            }
            else if (textRunStart == -1)
            {
                textRunStart = col;
                currentTextFg = cellFg;
                currentBold = cell.IsBold;
                currentUnderline = cell.IsUnderline;
            }
            _textRunBuilder.Append(c);
        }

        if (bgRunStart != -1 && currentBg != defaultBg)
        {
            double runX = bgRunStart * _charWidth;
            cache.Backgrounds.Add((new Rect(runX, 0, (width - bgRunStart) * _charWidth, _lineHeight), GetBrush(currentBg)));
        }
        AddTextRunToCache(cache, _textRunBuilder, textRunStart, currentTextFg, currentBold, currentUnderline);
    }

    private void AddTextRunToCache(RowRenderCache cache, StringBuilder sb, int startCol, Color fg, bool bold, bool underline)
    {
        if (sb.Length == 0 || startCol == -1)
        {
            sb.Clear();
            return;
        }

        string text = sb.ToString();
        sb.Clear();

        if (!underline && string.IsNullOrWhiteSpace(text)) return;

        double x = startCol * _charWidth;
        var tf = bold ? _cachedBoldTypeface : _cachedTypeface;

        // Use cached FormattedText to reduce allocations and GC pressure
        var ft = _formattedTextCache.GetOrCreate(text, tf, FontSize, fg);

        cache.TextRuns.Add((x, ft, underline, fg));
    }

    private void DrawCursor(DrawingContext context, double x, double y, TerminalCursorStyle style, Color cursorColor)
    {
        Rect cursorRect = style switch
        {
            TerminalCursorStyle.Block => new Rect(x, y, _charWidth, _lineHeight),
            TerminalCursorStyle.Bar => new Rect(x, y, 2, _lineHeight),
            TerminalCursorStyle.Underline => new Rect(x, y + _lineHeight - 2, _charWidth, 2),
            _ => new Rect(x, y, 2, _lineHeight)
        };

        if (_hasFocus && _cursorBlink)
        {
            context.FillRectangle(GetBrush(cursorColor), cursorRect);
        }
        else if (!_hasFocus)
        {
            var dimmed = Color.FromArgb(100, cursorColor.R, cursorColor.G, cursorColor.B);
            context.FillRectangle(GetBrush(dimmed), cursorRect);
        }
    }

    #endregion

    #region Selection

    private int TerminalWidth => _terminal?.Width ?? 80;
    private int TerminalHeight => _terminal?.Height ?? 24;

    private bool HasSelection() =>
        _selectionStartRow != _selectionEndRow || _selectionStartCol != _selectionEndCol;

    private (int start, int end) OrderedRows() =>
        _selectionStartRow <= _selectionEndRow
            ? (_selectionStartRow, _selectionEndRow)
            : (_selectionEndRow, _selectionStartRow);

    private (int startCol, int endCol) GetSelectionRangeForRow(int row)
    {
        var (startRow, endRow) = OrderedRows();
        int sc = _selectionStartRow <= _selectionEndRow ? _selectionStartCol : _selectionEndCol;
        int ec = _selectionStartRow <= _selectionEndRow ? _selectionEndCol : _selectionStartCol;

        if (row == startRow && row == endRow) return (Math.Min(sc, ec), Math.Max(sc, ec));
        if (row == startRow) return (sc, TerminalWidth);
        if (row == endRow) return (0, ec);
        return (0, TerminalWidth);
    }

    private (int col, int row) HitTest(Point point) =>
        (Math.Clamp((int)((point.X - Padding.Left) / Math.Max(1, _charWidth)), 0, TerminalWidth - 1),
         Math.Clamp((int)((point.Y - Padding.Top) / Math.Max(1, _lineHeight)), 0, TerminalHeight - 1));

    private string GetSelectedText()
    {
        if (_terminal == null || !HasSelection()) return "";
        var sb = new StringBuilder();
        var (startRow, endRow) = OrderedRows();
        for (int row = startRow; row <= endRow; row++)
        {
            var (sc, ec) = GetSelectionRangeForRow(row);
            for (int col = sc; col < ec; col++)
            {
                var cell = _terminal.GetCell(col, row);
                if (cell.Char != '\0') sb.Append(cell.Char);
            }
            if (row < endRow) sb.AppendLine();
        }
        return sb.ToString();
    }

    private void ClearSelection()
    {
        _selectionStartRow = _selectionEndRow = 0;
        _selectionStartCol = _selectionEndCol = 0;
        InvalidateVisual();
    }

    #endregion

    #region Input Handling

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        _hasFocus = true;

        var (col, row) = HitTest(e.GetPosition(this));
        _isSelecting = true;
        _selectionStartCol = _selectionEndCol = col;
        _selectionStartRow = _selectionEndRow = row;

        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_isSelecting)
        {
            var (col, row) = HitTest(e.GetPosition(this));
            if (col != _selectionEndCol || row != _selectionEndRow)
            {
                _selectionEndCol = col;
                _selectionEndRow = row;
                InvalidateVisual();
            }
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _isSelecting = false;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_terminal == null) return;
        int delta = e.Delta.Y > 0 ? 3 : -3;
        var maxScroll = Math.Max(0, _terminal.Scrollback.Count);
        var newOffset = Math.Clamp(_scrollOffset + delta, 0, maxScroll);

        if (newOffset != _scrollOffset)
        {
            _scrollOffset = newOffset;
            InvalidateAllRowCaches();
            InvalidateVisual();
        }
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (_scrollOffset != 0)
        {
            _scrollOffset = 0;
            InvalidateAllRowCaches();
            InvalidateVisual();
        }

        if (ctrl && e.Key == Key.C && HasSelection())
        {
            CopyToClipboard();
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.V)
        {
            PasteFromClipboard();
            e.Handled = true;
            return;
        }

        if (HasSelection() && !ctrl && !shift)
            ClearSelection();

        var consoleKey = MapKey(e.Key);
        if (consoleKey != null)
        {
            var sequence = KeyboardTranslations.TranslateKey(consoleKey.Value, ctrl, alt, shift);
            if (sequence != null)
            {
                InputReceived?.Invoke(sequence);
                e.Handled = true;
            }
        }

        _cursorBlink = true;
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Text))
        {
            if (_scrollOffset != 0)
            {
                _scrollOffset = 0;
                InvalidateAllRowCaches();
            }

            if (HasSelection()) ClearSelection();

            InputReceived?.Invoke(e.Text);
            e.Handled = true;
            _cursorBlink = true;
        }
    }

    protected override void OnGotFocus(GotFocusEventArgs e)
    {
        base.OnGotFocus(e);
        _hasFocus = true;
        _cursorBlink = true;
        _cursorBlinkTimer.Start();
        InvalidateVisual();
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        _hasFocus = false;
        _cursorBlinkTimer.Stop();
        InvalidateVisual();
    }

    private async void CopyToClipboard()
    {
        var text = GetSelectedText();
        if (string.IsNullOrEmpty(text)) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null)
            await clipboard.SetTextAsync(text);
    }

    private async void PasteFromClipboard()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;
        try
        {
            var text = await ClipboardExtensions.TryGetTextAsync(clipboard);
            if (!string.IsNullOrEmpty(text))
            {
                text = text.Replace("\r\n", "\r").Replace("\n", "\r");
                InputReceived?.Invoke(text);
            }
        }
        catch { /* Ignore clipboard errors */ }
    }

    private static ConsoleKey? MapKey(Key key) => key switch
    {
        Key.Enter => ConsoleKey.Enter,
        Key.Back => ConsoleKey.Backspace,
        Key.Tab => ConsoleKey.Tab,
        Key.Escape => ConsoleKey.Escape,
        Key.Space => ConsoleKey.Spacebar,
        Key.Up => ConsoleKey.UpArrow,
        Key.Down => ConsoleKey.DownArrow,
        Key.Left => ConsoleKey.LeftArrow,
        Key.Right => ConsoleKey.RightArrow,
        Key.Home => ConsoleKey.Home,
        Key.End => ConsoleKey.End,
        Key.Insert => ConsoleKey.Insert,
        Key.Delete => ConsoleKey.Delete,
        Key.PageUp => ConsoleKey.PageUp,
        Key.PageDown => ConsoleKey.PageDown,
        >= Key.F1 and <= Key.F12 => ConsoleKey.F1 + (key - Key.F1),
        >= Key.A and <= Key.Z => ConsoleKey.A + (key - Key.A),
        >= Key.D0 and <= Key.D9 => ConsoleKey.D0 + (key - Key.D0),
        Key.OemMinus => ConsoleKey.OemMinus,
        Key.OemPlus => ConsoleKey.OemPlus,
        Key.OemOpenBrackets => ConsoleKey.Oem4,
        Key.OemCloseBrackets => ConsoleKey.Oem6,
        Key.OemPipe => ConsoleKey.Oem5,
        _ => null
    };

    #endregion
}