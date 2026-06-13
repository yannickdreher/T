using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using T.VT;
using T.UI.Services;
using T.UI.ViewModels;

namespace T.UI.Controls;

public enum TerminalCursorStyle { Block, Bar, Underline }

/// <summary>
/// VT100/xterm terminal control.
/// Rendering is split into three layers to minimize CPU usage:
/// the host control draws text/backgrounds (cached per row), while selection and
/// cursor live on separate child visuals - cursor blinking and selection drags
/// therefore never trigger a full text repaint.
/// </summary>
public class TerminalControl : Control
{
    private VirtualTerminal? _terminal;
    private double _charWidth;
    private double _lineHeight;
    private bool _cursorBlink = true;
    private bool _hasFocus;
    private bool _isShutdown;
    private bool _isAttached;
    private readonly DispatcherTimer _cursorBlinkTimer;
    private readonly StringBuilder _textRunBuilder = new(512);

    // Input buffering: incoming PTY data is fed to the parser in time-budgeted
    // slices so an infinite stream (e.g. `yes`) cannot starve the UI thread.
    private readonly StringBuilder _incomingBuffer = new(16 * 1024);
    private readonly Lock _incomingLock = new();
    private readonly DispatcherTimer _inputTimer;
    private readonly char[] _feedBuffer = new char[8192];
    private const int InputProcessIntervalMs = 8;   // tick rate while data is pending
    private const double InputBudgetMs = 6.0;       // max parser time per tick
    private const int MaxInputBufferSize = 1_000_000; // safety bound
    private const int TrimToSize = 500_000;

    // Render coalescing
    private readonly DispatcherTimer _renderTimer;
    private int _pendingDirtyTop = int.MaxValue;
    private int _pendingDirtyBottom = int.MinValue;
    private const int RenderCoalesceMs = 16; // ~60 FPS cap for bursts

    // FormattedText cache (reduces GC pressure)
    private readonly FormattedTextCache _formattedTextCache = new(2048);

    [Flags]
    private enum RunDecoration : byte { None = 0, Underline = 1, Strikethrough = 2 }

    private sealed class RowRenderCache
    {
        public readonly List<(double X, FormattedText Text, RunDecoration Deco, Color Foreground)> TextRuns = new();
        public readonly List<(Rect Rect, IBrush Brush)> Backgrounds = new();
        public bool IsDirty = true;

        public void Clear()
        {
            TextRuns.Clear();
            Backgrounds.Clear();
            IsDirty = true;
        }
    }

    private RowRenderCache[] _rowCaches = [];

    // Overlay layers (selection below cursor, both above text)
    private readonly OverlayLayer _selectionLayer;
    private readonly OverlayLayer _cursorLayer;

    private sealed class OverlayLayer : Control
    {
        private readonly Action<DrawingContext> _render;
        public OverlayLayer(Action<DrawingContext> render)
        {
            _render = render;
            IsHitTestVisible = false;
        }
        public override void Render(DrawingContext context) => _render(context);
    }

    // Selection state - stored in absolute buffer coordinates
    // (absolute row = index into scrollback followed by the live screen)
    private enum SelectionMode { Char, Word, Line }
    private bool _isSelecting;
    private bool _hasSelectionRange;
    private SelectionMode _selectionMode = SelectionMode.Char;
    private int _selAnchorStartAbsRow, _selAnchorStartCol;
    private int _selAnchorEndAbsRow, _selAnchorEndCol;
    private int _selStartAbsRow, _selStartCol;
    private int _selEndAbsRow, _selEndCol;

    private int _scrollOffset;

    private uint _terminalColumns;
    private uint _terminalRows;

    // Mouse tracking (reporting to host)
    private int _pressedMouseButton = -1;
    private int _lastMouseReportCol = -1, _lastMouseReportRow = -1;

    // DECSCUSR override (host-requested cursor shape)
    private TerminalCursorStyle? _cursorStyleOverride;
    private bool _cursorBlinkEnabled = true;

    private readonly Dictionary<Color, SolidColorBrush> _brushCache = [];
    private readonly Dictionary<Color, Pen> _penCache = [];
    private static readonly SolidColorBrush SelectionBrush = new(Color.FromArgb(100, 51, 153, 255));
    private Typeface _typeface;
    private Typeface _typefaceBold;
    private Typeface _typefaceItalic;
    private Typeface _typefaceBoldItalic;

    #region Styled Properties

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        AvaloniaProperty.Register<TerminalControl, FontFamily>(nameof(FontFamily),
            new FontFamily("Cascadia Mono, Consolas, Courier New, monospace"));

    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<TerminalControl, double>(nameof(FontSize), 14);

    public static readonly StyledProperty<Color> DefaultForegroundProperty =
        AvaloniaProperty.Register<TerminalControl, Color>(nameof(DefaultForeground),
            Color.FromRgb(204, 204, 204));

    public static readonly StyledProperty<Color> DefaultBackgroundProperty =
        AvaloniaProperty.Register<TerminalControl, Color>(nameof(DefaultBackground),
            Color.FromRgb(12, 12, 12));

    public static readonly StyledProperty<Color> CursorColorProperty =
        AvaloniaProperty.Register<TerminalControl, Color>(nameof(CursorColor), Colors.White);

    public static readonly StyledProperty<Thickness> PaddingProperty =
        AvaloniaProperty.Register<TerminalControl, Thickness>(nameof(Padding), new Thickness(0));

    public static readonly StyledProperty<CornerRadius> CornerRadiusProperty =
        AvaloniaProperty.Register<TerminalControl, CornerRadius>(nameof(CornerRadius), new CornerRadius(0));

    public static readonly StyledProperty<TerminalCursorStyle> CursorStyleProperty =
        AvaloniaProperty.Register<TerminalControl, TerminalCursorStyle>(nameof(CursorStyle), TerminalCursorStyle.Bar);

    public static readonly StyledProperty<bool> ShowStatsOverlayProperty =
        AvaloniaProperty.Register<TerminalControl, bool>(nameof(ShowStatsOverlay), false);

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
    public event Action<string>? TitleChanged;

    public TerminalControl()
    {
        Focusable = true;
        IsTabStop = true;
        ClipToBounds = true;

        _selectionLayer = new OverlayLayer(RenderSelection);
        _cursorLayer = new OverlayLayer(RenderCursor);
        VisualChildren.Add(_selectionLayer);
        VisualChildren.Add(_cursorLayer);
        LogicalChildren.Add(_selectionLayer);
        LogicalChildren.Add(_cursorLayer);

        // Cursor blink only repaints the tiny cursor layer - not the text.
        _cursorBlinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
        _cursorBlinkTimer.Tick += (_, _) =>
        {
            _cursorBlink = !_cursorBlink;
            _cursorLayer.InvalidateVisual();
        };

        _inputTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(InputProcessIntervalMs) };
        _inputTimer.Tick += (_, _) => ProcessInputBuffer();

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
        DefaultForegroundProperty.Changed.AddClassHandler<TerminalControl>((x, _) => x.OnThemeColorsChanged());
        DefaultBackgroundProperty.Changed.AddClassHandler<TerminalControl>((x, _) => x.OnThemeColorsChanged());
        FocusableProperty.OverrideDefaultValue<TerminalControl>(true);
    }

    private void OnPaddingChanged()
    {
        if (_terminal != null && Bounds.Width > 0 && Bounds.Height > 0)
        {
            InvalidateArrange();
        }
    }

    private void OnThemeColorsChanged()
    {
        InvalidateAllRowCaches();
        InvalidateVisual();
    }

    #region Public API

    /// <summary>Buffers incoming PTY data; parsing happens in budgeted slices on the UI timer.</summary>
    public void AppendOutput(string text)
    {
        if (string.IsNullOrEmpty(text) || _isShutdown) return;

        lock (_incomingLock)
        {
            // Safety: bound the buffer to avoid OOM with runaway producers.
            if (_incomingBuffer.Length + text.Length > MaxInputBufferSize)
            {
                if (_incomingBuffer.Length > TrimToSize)
                    _incomingBuffer.Remove(0, _incomingBuffer.Length - TrimToSize);

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

            if (!_inputTimer.IsEnabled)
                _inputTimer.Start();
        }
    }

    /// <summary>
    /// Feeds buffered data to the parser. Time-budgeted: processes at most
    /// <see cref="InputBudgetMs"/> ms per tick, in fixed-size slices copied into a
    /// reusable buffer (no string allocation per slice).
    /// </summary>
    private void ProcessInputBuffer()
    {
        if (_terminal == null)
        {
            lock (_incomingLock) { _incomingBuffer.Clear(); }
            _inputTimer.Stop();
            return;
        }

        var sw = Stopwatch.StartNew();

        while (sw.Elapsed.TotalMilliseconds < InputBudgetMs)
        {
            int len;
            lock (_incomingLock)
            {
                len = Math.Min(_feedBuffer.Length, _incomingBuffer.Length);
                if (len == 0)
                {
                    _inputTimer.Stop();
                    return;
                }
                _incomingBuffer.CopyTo(0, _feedBuffer, 0, len);
                _incomingBuffer.Remove(0, len);
            }

            try
            {
                _terminal.Feed(_feedBuffer.AsSpan(0, len));
            }
            catch
            {
                // swallow parsing errors to keep the control responsive
            }
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

        var cacheStats = _formattedTextCache.GetStats();

        int incomingLen;
        lock (_incomingLock) { incomingLen = _incomingBuffer.Length; }

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

    private Color ResolveForeground(in TerminalCharacter cell, Color defaultFg)
    {
        var fg = cell.Foreground;
        // Bold-as-bright for the 8 basic ANSI colors (classic terminal behavior)
        if (cell.IsBold && fg.IsPalette && fg.PaletteIndex < 8)
            fg = fg.ToBright();

        if (fg.IsDefault) return defaultFg;
        var (r, g, b) = fg.ToRgb();
        return Color.FromRgb(r, g, b);
    }

    private static Color ResolveBackground(in TerminalCharacter cell, Color defaultBg)
    {
        if (cell.Background.IsDefault) return defaultBg;
        var (r, g, b) = cell.Background.ToRgb();
        return Color.FromRgb(r, g, b);
    }

    private SolidColorBrush GetBrush(Color color)
    {
        if (!_brushCache.TryGetValue(color, out var brush))
        {
            if (_brushCache.Count > 512)
                _brushCache.Clear();
            brush = new SolidColorBrush(color);
            _brushCache[color] = brush;
        }
        return brush;
    }

    private Pen GetPen(Color color)
    {
        if (!_penCache.TryGetValue(color, out var pen))
        {
            if (_penCache.Count > 256)
                _penCache.Clear();
            pen = new Pen(GetBrush(color), 1);
            _penCache[color] = pen;
        }
        return pen;
    }

    private void UpdateMetrics()
    {
        var family = FontFamily;
        _typeface = new Typeface(family);
        _typefaceBold = new Typeface(family, FontStyle.Normal, FontWeight.Bold);
        _typefaceItalic = new Typeface(family, FontStyle.Italic);
        _typefaceBoldItalic = new Typeface(family, FontStyle.Italic, FontWeight.Bold);

        var testText = new FormattedText("M", CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, _typeface, FontSize, Brushes.White);

        var newCharWidth = testText.WidthIncludingTrailingWhitespace;
        var newLineHeight = testText.Height;

        bool changed = Math.Abs(_charWidth - newCharWidth) > 0.01 || Math.Abs(_lineHeight - newLineHeight) > 0.01;

        _charWidth = newCharWidth;
        _lineHeight = newLineHeight;

        if (changed)
        {
            _formattedTextCache.Clear();
            InvalidateAllRowCaches();
            InvalidateMeasure();
            InvalidateArrange();
        }
        InvalidateVisual();
        _cursorLayer.InvalidateVisual();
    }

    private void InvalidateAllRowCaches()
    {
        foreach (var c in _rowCaches) c.Clear();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        UpdateMetrics();

        // The session may have produced output while this control was detached
        // (e.g. its tab was in the background). The VirtualTerminal stayed current
        // because the input timer kept parsing, so force a full repaint to reflect
        // everything that changed while we weren't rendering.
        if (!_isShutdown && _terminal != null)
        {
            InvalidateAllRowCaches();
            _pendingDirtyTop = int.MaxValue;
            _pendingDirtyBottom = int.MinValue;
            InvalidateVisual();
            _cursorLayer.InvalidateVisual();

            // Resume parsing if data is still pending.
            lock (_incomingLock)
            {
                if (_incomingBuffer.Length > 0 && !_inputTimer.IsEnabled)
                    _inputTimer.Start();
            }
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _isAttached = false;

        // Stop rendering-related timers - there is nothing to paint while detached.
        _cursorBlinkTimer.Stop();
        if (_renderTimer.IsEnabled) _renderTimer.Stop();

        // NOTE: the input timer is intentionally left running so the VirtualTerminal
        // keeps consuming SSH output while the tab is backgrounded. It is stopped
        // permanently only via Shutdown() when the session is closed.
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_charWidth <= 0 || _lineHeight <= 0) UpdateMetrics();
        _selectionLayer.Measure(availableSize);
        _cursorLayer.Measure(availableSize);
        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var layerRect = new Rect(finalSize);
        _selectionLayer.Arrange(layerRect);
        _cursorLayer.Arrange(layerRect);

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
            _terminal.SendData += OnTerminalSendData;
            _terminal.TitleChanged += OnTerminalTitleChanged;
            _terminal.CursorStyleChanged += OnCursorStyleChanged;

            TerminalResized?.Invoke(newColumns, newRows, (uint)availableWidth, (uint)availableHeight);
        }
        else if (colsChanged || rowsChanged)
        {
            _terminalColumns = newColumns;
            _terminalRows = newRows;
            ResizeCache((int)newRows);
            InvalidateAllRowCaches();
            ClearSelection();

            _terminal.Resize((int)newColumns, (int)newRows);

            TerminalResized?.Invoke(newColumns, newRows, (uint)availableWidth, (uint)availableHeight);
        }

        return finalSize;
    }

    private void OnTerminalSendData(string data) => InputReceived?.Invoke(data);

    private void OnTerminalTitleChanged(string title) => TitleChanged?.Invoke(title);

    /// <summary>
    /// Permanently stops the terminal: halts all timers and unhooks the
    /// underlying <see cref="VirtualTerminal"/> events. Call when the owning
    /// session is closed/disposed. After this the control no longer parses or
    /// renders, so it is safe to drop from the visual tree.
    /// </summary>
    public void Shutdown()
    {
        if (_isShutdown) return;
        _isShutdown = true;

        _cursorBlinkTimer.Stop();
        _renderTimer.Stop();
        _inputTimer.Stop();

        lock (_incomingLock) { _incomingBuffer.Clear(); }

        if (_terminal != null)
        {
            _terminal.ScreenChanged -= OnTerminalScreenChanged;
            _terminal.SendData -= OnTerminalSendData;
            _terminal.TitleChanged -= OnTerminalTitleChanged;
            _terminal.CursorStyleChanged -= OnCursorStyleChanged;
        }
    }

    private void OnCursorStyleChanged(int style)
    {
        (_cursorStyleOverride, _cursorBlinkEnabled) = style switch
        {
            0 or 1 => ((TerminalCursorStyle?)TerminalCursorStyle.Block, true),
            2 => (TerminalCursorStyle.Block, false),
            3 => (TerminalCursorStyle.Underline, true),
            4 => (TerminalCursorStyle.Underline, false),
            5 => (TerminalCursorStyle.Bar, true),
            6 => (TerminalCursorStyle.Bar, false),
            _ => (null, true)
        };
        RestartCursorBlink();
        _cursorLayer.InvalidateVisual();
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

            _pendingDirtyTop = Math.Min(_pendingDirtyTop, top);
            _pendingDirtyBottom = Math.Max(_pendingDirtyBottom, bottom);

            // Only schedule a repaint while attached; a detached control resolves
            // all accumulated changes with a full repaint on re-attach.
            if (_isAttached && !_isShutdown && !_renderTimer.IsEnabled)
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

        // single invalidate for the batch; cursor may have moved with the output
        InvalidateVisual();
        _cursorLayer.InvalidateVisual();
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

        double offsetX = Padding.Left;
        double offsetY = Padding.Top;
        var defaultFg = DefaultForeground;
        var defaultBg = DefaultBackground;

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

                if (run.Deco != RunDecoration.None)
                {
                    var pen = GetPen(run.Foreground);
                    double runEnd = run.X + offsetX + run.Text.WidthIncludingTrailingWhitespace;

                    if ((run.Deco & RunDecoration.Underline) != 0)
                    {
                        double y = rowY + _lineHeight - 1.5;
                        context.DrawLine(pen, new Point(run.X + offsetX, y), new Point(runEnd, y));
                    }
                    if ((run.Deco & RunDecoration.Strikethrough) != 0)
                    {
                        double y = rowY + _lineHeight * 0.55;
                        context.DrawLine(pen, new Point(run.X + offsetX, y), new Point(runEnd, y));
                    }
                }
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
        bool currentItalic = false;
        RunDecoration currentDeco = RunDecoration.None;
        _textRunBuilder.Clear();

        TerminalLine? historyLine = isHistory ? terminal.Scrollback[rowInBuffer] : null;

        for (int col = 0; col < width; col++)
        {
            TerminalCharacter cell = historyLine != null
                ? (col < historyLine.Length ? historyLine[col] : TerminalCharacter.Blank)
                : terminal.GetCell(col, rowInBuffer);

            var cellFg = ResolveForeground(in cell, defaultFg);
            var cellBg = ResolveBackground(in cell, defaultBg);

            if (cell.IsInverse) (cellFg, cellBg) = (cellBg, cellFg);

            if (cell.IsDim)
            {
                // Blend toward the background to fake reduced intensity
                cellFg = Color.FromRgb(
                    (byte)((cellFg.R + cellBg.R) / 2),
                    (byte)((cellFg.G + cellBg.G) / 2),
                    (byte)((cellFg.B + cellBg.B) / 2));
            }

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

            var deco = (cell.IsUnderline ? RunDecoration.Underline : RunDecoration.None)
                     | (cell.IsStrikethrough ? RunDecoration.Strikethrough : RunDecoration.None);

            if (cellFg != currentTextFg || cell.IsBold != currentBold || cell.IsItalic != currentItalic || deco != currentDeco)
            {
                AddTextRunToCache(cache, _textRunBuilder, textRunStart, currentTextFg, currentBold, currentItalic, currentDeco);
                textRunStart = col;
                currentTextFg = cellFg;
                currentBold = cell.IsBold;
                currentItalic = cell.IsItalic;
                currentDeco = deco;
            }
            else if (textRunStart == -1)
            {
                textRunStart = col;
                currentTextFg = cellFg;
                currentBold = cell.IsBold;
                currentItalic = cell.IsItalic;
                currentDeco = deco;
            }
            _textRunBuilder.Append(c);
        }

        if (bgRunStart != -1 && currentBg != defaultBg)
        {
            double runX = bgRunStart * _charWidth;
            cache.Backgrounds.Add((new Rect(runX, 0, (width - bgRunStart) * _charWidth, _lineHeight), GetBrush(currentBg)));
        }
        AddTextRunToCache(cache, _textRunBuilder, textRunStart, currentTextFg, currentBold, currentItalic, currentDeco);
    }

    private void AddTextRunToCache(RowRenderCache cache, StringBuilder sb, int startCol, Color fg, bool bold, bool italic, RunDecoration deco)
    {
        if (sb.Length == 0 || startCol == -1)
        {
            sb.Clear();
            return;
        }

        string text = sb.ToString();
        sb.Clear();

        if (deco == RunDecoration.None && string.IsNullOrWhiteSpace(text)) return;

        double x = startCol * _charWidth;
        var tf = (bold, italic) switch
        {
            (true, true) => _typefaceBoldItalic,
            (true, false) => _typefaceBold,
            (false, true) => _typefaceItalic,
            _ => _typeface
        };

        var ft = _formattedTextCache.GetOrCreate(text, tf, FontSize, fg);

        cache.TextRuns.Add((x, ft, deco, fg));
    }

    // ----- Cursor layer -----

    private void RenderCursor(DrawingContext context)
    {
        var terminal = _terminal;
        if (terminal == null || !terminal.CursorVisible || _scrollOffset != 0) return;

        int cursorCol = terminal.CursorColumn;
        int cursorRow = terminal.CursorRow;
        if (cursorCol < 0 || cursorRow < 0 || cursorRow >= terminal.Height) return;
        cursorCol = Math.Min(cursorCol, terminal.Width - 1);

        double x = Padding.Left + cursorCol * _charWidth;
        double y = Padding.Top + cursorRow * _lineHeight;

        var style = _cursorStyleOverride ?? CursorStyle;
        Rect cursorRect = style switch
        {
            TerminalCursorStyle.Block => new Rect(x, y, _charWidth, _lineHeight),
            TerminalCursorStyle.Bar => new Rect(x, y, Math.Max(1.5, _charWidth * 0.12), _lineHeight),
            TerminalCursorStyle.Underline => new Rect(x, y + _lineHeight - 2, _charWidth, 2),
            _ => new Rect(x, y, 2, _lineHeight)
        };

        var cursorColor = CursorColor;

        if (_hasFocus)
        {
            if (_cursorBlink || !_cursorBlinkEnabled)
            {
                if (style == TerminalCursorStyle.Block)
                {
                    // Block cursor inverts the cell: filled box + glyph in background color
                    context.FillRectangle(GetBrush(cursorColor), cursorRect);
                    var cell = terminal.GetCell(cursorCol, cursorRow);
                    char ch = (cell.Char == '\0' || cell.IsHidden) ? ' ' : cell.Char;
                    if (ch != ' ')
                    {
                        var ft = _formattedTextCache.GetOrCreate(ch.ToString(), _typeface, FontSize, DefaultBackground);
                        context.DrawText(ft, new Point(x, y));
                    }
                }
                else
                {
                    context.FillRectangle(GetBrush(cursorColor), cursorRect);
                }
            }
        }
        else
        {
            // Unfocused: hollow outline (Windows Terminal style)
            var outline = new Rect(x + 0.5, y + 0.5, Math.Max(1, _charWidth - 1), Math.Max(1, _lineHeight - 1));
            context.DrawRectangle(null, GetPen(Color.FromArgb(180, cursorColor.R, cursorColor.G, cursorColor.B)), outline);
        }
    }

    private void RestartCursorBlink()
    {
        _cursorBlink = true;
        if (_hasFocus && _cursorBlinkEnabled)
        {
            _cursorBlinkTimer.Stop();
            _cursorBlinkTimer.Start();
        }
        else
        {
            _cursorBlinkTimer.Stop();
        }
        _cursorLayer.InvalidateVisual();
    }

    // ----- Selection layer -----

    private void RenderSelection(DrawingContext context)
    {
        if (!_hasSelectionRange || _terminal == null) return;

        var (startAbs, startCol, endAbs, endCol) = OrderedSelection();

        int scrollbackCount = _terminal.Scrollback.Count;
        int visibleStart = Math.Max(0, scrollbackCount - _scrollOffset);
        int height = _terminal.Height;
        int width = _terminal.Width;

        double offsetX = Padding.Left;
        double offsetY = Padding.Top;

        for (int screenRow = 0; screenRow < height; screenRow++)
        {
            int absRow = visibleStart + screenRow;
            if (absRow < startAbs || absRow > endAbs) continue;

            int sc = absRow == startAbs ? startCol : 0;
            int ec = absRow == endAbs ? endCol : width;
            if (sc >= ec) continue;

            var rect = new Rect(
                offsetX + sc * _charWidth,
                offsetY + screenRow * _lineHeight,
                (ec - sc) * _charWidth,
                _lineHeight);
            context.FillRectangle(SelectionBrush, rect);
        }
    }

    #endregion

    #region Selection

    private int TerminalWidth => _terminal?.Width ?? 80;
    private int TerminalHeight => _terminal?.Height ?? 24;

    private (int startAbs, int startCol, int endAbs, int endCol) OrderedSelection()
    {
        if (_selStartAbsRow < _selEndAbsRow || (_selStartAbsRow == _selEndAbsRow && _selStartCol <= _selEndCol))
            return (_selStartAbsRow, _selStartCol, _selEndAbsRow, _selEndCol);
        return (_selEndAbsRow, _selEndCol, _selStartAbsRow, _selStartCol);
    }

    /// <summary>Converts a pointer position to (column, absolute row). Column may equal Width (end of line).</summary>
    private (int col, int absRow) HitTestAbsolute(Point point)
    {
        int screenRow = Math.Clamp((int)((point.Y - Padding.Top) / Math.Max(1, _lineHeight)), 0, TerminalHeight - 1);
        int col = Math.Clamp((int)Math.Round((point.X - Padding.Left) / Math.Max(1, _charWidth)), 0, TerminalWidth);

        int scrollbackCount = _terminal?.Scrollback.Count ?? 0;
        int visibleStart = Math.Max(0, scrollbackCount - _scrollOffset);
        return (col, visibleStart + screenRow);
    }

    /// <summary>Converts a pointer position to a screen cell (for mouse reporting). Column &lt; Width.</summary>
    private (int col, int row) HitTestScreen(Point point) =>
        (Math.Clamp((int)((point.X - Padding.Left) / Math.Max(1, _charWidth)), 0, TerminalWidth - 1),
         Math.Clamp((int)((point.Y - Padding.Top) / Math.Max(1, _lineHeight)), 0, TerminalHeight - 1));

    private TerminalCharacter GetCellAbsolute(int col, int absRow)
    {
        if (_terminal == null) return TerminalCharacter.Blank;
        int scrollbackCount = _terminal.Scrollback.Count;
        if (absRow < scrollbackCount)
        {
            if (absRow < 0) return TerminalCharacter.Blank;
            var line = _terminal.Scrollback[absRow];
            return col < line.Length ? line[col] : TerminalCharacter.Blank;
        }
        return _terminal.GetCell(col, absRow - scrollbackCount);
    }

    private static bool IsWordChar(char c) =>
        !char.IsWhiteSpace(c) && c is not ('\0' or '(' or ')' or '[' or ']' or '{' or '}' or '\'' or '"' or ',' or ';' or '|' or '<' or '>' or '&');

    private (int start, int end) WordBoundsAt(int col, int absRow)
    {
        int width = TerminalWidth;
        col = Math.Clamp(col, 0, width - 1);

        if (!IsWordChar(GetCellAbsolute(col, absRow).Char))
            return (col, Math.Min(col + 1, width));

        int start = col, end = col + 1;
        while (start > 0 && IsWordChar(GetCellAbsolute(start - 1, absRow).Char)) start--;
        while (end < width && IsWordChar(GetCellAbsolute(end, absRow).Char)) end++;
        return (start, end);
    }

    private string GetSelectedText()
    {
        if (_terminal == null || !_hasSelectionRange) return "";

        var (startAbs, startCol, endAbs, endCol) = OrderedSelection();
        var sb = new StringBuilder();
        int width = TerminalWidth;

        for (int absRow = startAbs; absRow <= endAbs; absRow++)
        {
            int sc = absRow == startAbs ? startCol : 0;
            int ec = absRow == endAbs ? endCol : width;

            int lineEnd = sb.Length;
            for (int col = sc; col < ec; col++)
            {
                var cell = GetCellAbsolute(col, absRow);
                sb.Append(cell.Char == '\0' ? ' ' : cell.Char);
            }

            // Trim trailing whitespace per line (standard terminal copy behavior)
            int trimTo = sb.Length;
            while (trimTo > lineEnd && sb[trimTo - 1] == ' ') trimTo--;
            sb.Length = trimTo;

            if (absRow < endAbs) sb.AppendLine();
        }
        return sb.ToString();
    }

    private void ClearSelection()
    {
        if (!_hasSelectionRange && !_isSelecting) return;
        _hasSelectionRange = false;
        _isSelecting = false;
        _selectionLayer.InvalidateVisual();
    }

    #endregion

    #region Mouse Input

    private static int EncodeMouseModifiers(KeyModifiers mods)
    {
        int m = 0;
        if (mods.HasFlag(KeyModifiers.Alt)) m |= MouseEncoder.ModAlt;
        if (mods.HasFlag(KeyModifiers.Control)) m |= MouseEncoder.ModCtrl;
        return m;
    }

    private bool MouseReportingActive(KeyModifiers mods) =>
        _terminal != null
        && _terminal.MouseTracking != MouseTrackingMode.None
        && !mods.HasFlag(KeyModifiers.Shift); // Shift bypasses tracking for local selection

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();

        var point = e.GetCurrentPoint(this);
        var props = point.Properties;
        var mods = e.KeyModifiers;

        // --- Mouse reporting to host application (vim, tmux, htop, ...) ---
        if (MouseReportingActive(mods))
        {
            int button =
                props.IsLeftButtonPressed ? MouseEncoder.ButtonLeft :
                props.IsMiddleButtonPressed ? MouseEncoder.ButtonMiddle :
                props.IsRightButtonPressed ? MouseEncoder.ButtonRight : -1;

            if (button >= 0)
            {
                var (col, row) = HitTestScreen(point.Position);
                _pressedMouseButton = button;
                _lastMouseReportCol = col;
                _lastMouseReportRow = row;
                InputReceived?.Invoke(MouseEncoder.Encode(
                    button | EncodeMouseModifiers(mods), col, row, isRelease: false, _terminal!.SgrMouseMode));
                e.Handled = true;
                return;
            }
        }

        // --- Local handling ---
        if (props.IsRightButtonPressed)
        {
            // Windows-Terminal behavior: right-click copies the selection if present,
            // otherwise pastes the clipboard.
            if (_hasSelectionRange)
            {
                CopyToClipboard();
                ClearSelection();
            }
            else
            {
                PasteFromClipboard();
            }
            e.Handled = true;
            return;
        }

        if (props.IsMiddleButtonPressed)
        {
            PasteFromClipboard();
            e.Handled = true;
            return;
        }

        if (!props.IsLeftButtonPressed) return;

        var (hitCol, hitAbsRow) = HitTestAbsolute(point.Position);

        _selectionMode = e.ClickCount switch
        {
            2 => SelectionMode.Word,
            >= 3 => SelectionMode.Line,
            _ => SelectionMode.Char
        };

        switch (_selectionMode)
        {
            case SelectionMode.Word:
            {
                var (ws, we) = WordBoundsAt(hitCol, hitAbsRow);
                _selAnchorStartAbsRow = _selAnchorEndAbsRow = hitAbsRow;
                _selAnchorStartCol = ws;
                _selAnchorEndCol = we;
                _selStartAbsRow = hitAbsRow; _selStartCol = ws;
                _selEndAbsRow = hitAbsRow; _selEndCol = we;
                _hasSelectionRange = true;
                break;
            }
            case SelectionMode.Line:
            {
                _selAnchorStartAbsRow = _selAnchorEndAbsRow = hitAbsRow;
                _selAnchorStartCol = 0;
                _selAnchorEndCol = TerminalWidth;
                _selStartAbsRow = hitAbsRow; _selStartCol = 0;
                _selEndAbsRow = hitAbsRow; _selEndCol = TerminalWidth;
                _hasSelectionRange = true;
                break;
            }
            default:
            {
                _selAnchorStartAbsRow = _selAnchorEndAbsRow = hitAbsRow;
                _selAnchorStartCol = _selAnchorEndCol = hitCol;
                _selStartAbsRow = _selEndAbsRow = hitAbsRow;
                _selStartCol = _selEndCol = hitCol;
                _hasSelectionRange = false; // becomes true once the pointer moves
                break;
            }
        }

        _isSelecting = true;
        e.Pointer.Capture(this);
        _selectionLayer.InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        var mods = e.KeyModifiers;

        // --- Mouse motion reporting ---
        if (_terminal != null && MouseReportingActive(mods) && !_isSelecting)
        {
            var mode = _terminal.MouseTracking;
            bool buttonHeld = _pressedMouseButton >= 0;

            bool report =
                (mode == MouseTrackingMode.ButtonEvent && buttonHeld) ||
                mode == MouseTrackingMode.AnyEvent;

            if (report)
            {
                var (col, row) = HitTestScreen(e.GetPosition(this));
                if (col != _lastMouseReportCol || row != _lastMouseReportRow)
                {
                    _lastMouseReportCol = col;
                    _lastMouseReportRow = row;
                    int button = buttonHeld ? _pressedMouseButton : 3;
                    InputReceived?.Invoke(MouseEncoder.Encode(
                        button + MouseEncoder.MotionFlag + EncodeMouseModifiers(mods),
                        col, row, isRelease: false, _terminal.SgrMouseMode));
                }
                e.Handled = true;
                return;
            }
        }

        // --- Local selection drag ---
        if (!_isSelecting) return;

        var (hitCol, hitAbsRow) = HitTestAbsolute(e.GetPosition(this));

        switch (_selectionMode)
        {
            case SelectionMode.Word:
            {
                var (ws, we) = WordBoundsAt(Math.Min(hitCol, TerminalWidth - 1), hitAbsRow);
                bool before = hitAbsRow < _selAnchorStartAbsRow ||
                              (hitAbsRow == _selAnchorStartAbsRow && ws < _selAnchorStartCol);
                if (before)
                {
                    _selStartAbsRow = hitAbsRow; _selStartCol = ws;
                    _selEndAbsRow = _selAnchorEndAbsRow; _selEndCol = _selAnchorEndCol;
                }
                else
                {
                    _selStartAbsRow = _selAnchorStartAbsRow; _selStartCol = _selAnchorStartCol;
                    _selEndAbsRow = hitAbsRow; _selEndCol = we;
                }
                _hasSelectionRange = true;
                break;
            }
            case SelectionMode.Line:
            {
                if (hitAbsRow < _selAnchorStartAbsRow)
                {
                    _selStartAbsRow = hitAbsRow; _selStartCol = 0;
                    _selEndAbsRow = _selAnchorEndAbsRow; _selEndCol = TerminalWidth;
                }
                else
                {
                    _selStartAbsRow = _selAnchorStartAbsRow; _selStartCol = 0;
                    _selEndAbsRow = hitAbsRow; _selEndCol = TerminalWidth;
                }
                _hasSelectionRange = true;
                break;
            }
            default:
            {
                if (hitCol != _selEndCol || hitAbsRow != _selEndAbsRow)
                {
                    _selEndCol = hitCol;
                    _selEndAbsRow = hitAbsRow;
                    _hasSelectionRange = _selStartAbsRow != _selEndAbsRow || _selStartCol != _selEndCol;
                }
                break;
            }
        }

        _selectionLayer.InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        // --- Mouse release reporting ---
        if (_terminal != null && _pressedMouseButton >= 0)
        {
            var mode = _terminal.MouseTracking;
            if (mode is MouseTrackingMode.Normal or MouseTrackingMode.ButtonEvent or MouseTrackingMode.AnyEvent)
            {
                var (col, row) = HitTestScreen(e.GetPosition(this));
                InputReceived?.Invoke(MouseEncoder.Encode(
                    _pressedMouseButton | EncodeMouseModifiers(e.KeyModifiers),
                    col, row, isRelease: true, _terminal.SgrMouseMode));
            }
            _pressedMouseButton = -1;
            e.Handled = true;
        }

        if (_isSelecting)
        {
            _isSelecting = false;
            e.Pointer.Capture(null);
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_terminal == null) return;

        var mods = e.KeyModifiers;

        // Ctrl+Wheel = font zoom (Windows Terminal behavior)
        if (mods.HasFlag(KeyModifiers.Control))
        {
            double newSize = Math.Clamp(FontSize + (e.Delta.Y > 0 ? 1 : -1), 6, 72);
            if (Math.Abs(newSize - FontSize) > 0.01)
                FontSize = newSize;
            e.Handled = true;
            return;
        }

        // Wheel reporting to host (mouse tracking active)
        if (MouseReportingActive(mods))
        {
            var (col, row) = HitTestScreen(e.GetPosition(this));
            int button = (e.Delta.Y > 0 ? MouseEncoder.WheelUp : MouseEncoder.WheelDown) | EncodeMouseModifiers(mods);
            int notches = Math.Max(1, (int)Math.Abs(e.Delta.Y));
            for (int i = 0; i < notches; i++)
                InputReceived?.Invoke(MouseEncoder.Encode(button, col, row, isRelease: false, _terminal.SgrMouseMode));
            e.Handled = true;
            return;
        }

        // Alternate screen without tracking: translate wheel to arrow keys
        // so `less`, vim, man etc. scroll naturally (xterm alternateScroll).
        if (_terminal.IsAlternateScreen && _terminal.AlternateScrollMode)
        {
            string seq = e.Delta.Y > 0
                ? (_terminal.ApplicationCursorKeys ? "\x1bOA" : "\x1b[A")
                : (_terminal.ApplicationCursorKeys ? "\x1bOB" : "\x1b[B");
            int count = Math.Max(1, (int)Math.Abs(e.Delta.Y)) * 3;
            var sb = new StringBuilder(seq.Length * count);
            for (int i = 0; i < count; i++) sb.Append(seq);
            InputReceived?.Invoke(sb.ToString());
            e.Handled = true;
            return;
        }

        // Scrollback
        int lines = (int)(e.Delta.Y * 3);
        if (lines == 0) lines = Math.Sign(e.Delta.Y);
        ScrollViewportBy(lines);
        e.Handled = true;
    }

    private void ScrollViewportBy(int lines)
    {
        if (_terminal == null || lines == 0) return;
        var maxScroll = Math.Max(0, _terminal.Scrollback.Count);
        var newOffset = Math.Clamp(_scrollOffset + lines, 0, maxScroll);
        SetScrollOffset(newOffset);
    }

    private void SetScrollOffset(int newOffset)
    {
        if (newOffset == _scrollOffset) return;
        _scrollOffset = newOffset;
        InvalidateAllRowCaches();
        InvalidateVisual();
        _selectionLayer.InvalidateVisual();
        _cursorLayer.InvalidateVisual();
    }

    #endregion

    #region Keyboard Input

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var mods = e.KeyModifiers;
        var ctrl = mods.HasFlag(KeyModifiers.Control);
        var alt = mods.HasFlag(KeyModifiers.Alt);
        var shift = mods.HasFlag(KeyModifiers.Shift);

        // ---- Local shortcuts (never sent to the host) ----

        // Copy: Ctrl+Shift+C / Ctrl+Insert, and Ctrl+C when a selection exists
        if ((ctrl && shift && e.Key == Key.C) || (ctrl && e.Key == Key.Insert))
        {
            CopyToClipboard();
            e.Handled = true;
            return;
        }
        if (ctrl && !shift && e.Key == Key.C && _hasSelectionRange)
        {
            CopyToClipboard();
            ClearSelection();
            e.Handled = true;
            return;
        }

        // Paste: Ctrl+V / Ctrl+Shift+V / Shift+Insert
        if ((ctrl && e.Key == Key.V) || (shift && e.Key == Key.Insert))
        {
            PasteFromClipboard();
            e.Handled = true;
            return;
        }

        // Scrollback navigation (Shift+PageUp/PageDown, Ctrl+Shift+Home/End)
        if (shift && e.Key == Key.PageUp)
        {
            ScrollViewportBy(Math.Max(1, TerminalHeight - 1));
            e.Handled = true;
            return;
        }
        if (shift && e.Key == Key.PageDown)
        {
            ScrollViewportBy(-Math.Max(1, TerminalHeight - 1));
            e.Handled = true;
            return;
        }
        if (ctrl && shift && e.Key == Key.Home)
        {
            SetScrollOffset(_terminal?.Scrollback.Count ?? 0);
            e.Handled = true;
            return;
        }
        if (ctrl && shift && e.Key == Key.End)
        {
            SetScrollOffset(0);
            e.Handled = true;
            return;
        }

        // Escape clears an active selection locally
        if (e.Key == Key.Escape && _hasSelectionRange)
        {
            ClearSelection();
            e.Handled = true;
            return;
        }

        // ---- Everything else goes to the host ----

        // Typing jumps back to the live screen
        if (_scrollOffset != 0)
            SetScrollOffset(0);

        if (_hasSelectionRange && !ctrl && !shift && !alt)
            ClearSelection();

        var consoleKey = MapKey(e.Key);
        if (consoleKey != null)
        {
            var sequence = KeyboardTranslations.TranslateKey(
                consoleKey.Value, ctrl, alt, shift,
                applicationMode: _terminal?.ApplicationCursorKeys ?? false);

            if (sequence != null)
            {
                InputReceived?.Invoke(sequence);
                e.Handled = true;
            }
        }

        RestartCursorBlink();
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Text))
        {
            if (_scrollOffset != 0)
                SetScrollOffset(0);

            if (_hasSelectionRange) ClearSelection();

            InputReceived?.Invoke(e.Text);
            e.Handled = true;
            RestartCursorBlink();
        }
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        _hasFocus = true;
        RestartCursorBlink();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        _hasFocus = false;
        _cursorBlinkTimer.Stop();
        _cursorLayer.InvalidateVisual();
    }

    private async void CopyToClipboard()
    {
        var text = GetSelectedText();
        if (string.IsNullOrEmpty(text)) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null)
        {
            try { await clipboard.SetTextAsync(text); }
            catch { /* Ignore clipboard errors */ }
        }
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

                // Bracketed paste lets shells/editors treat the block atomically
                text = KeyboardTranslations.WrapForBracketedPaste(
                    text, _terminal?.BracketedPasteMode ?? false);

                if (_scrollOffset != 0) SetScrollOffset(0);
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
        >= Key.NumPad0 and <= Key.NumPad9 => ConsoleKey.NumPad0 + (key - Key.NumPad0),
        Key.OemMinus => ConsoleKey.OemMinus,
        Key.OemPlus => ConsoleKey.OemPlus,
        Key.OemComma => ConsoleKey.OemComma,
        Key.OemPeriod => ConsoleKey.OemPeriod,
        Key.OemOpenBrackets => ConsoleKey.Oem4,
        Key.OemCloseBrackets => ConsoleKey.Oem6,
        Key.OemPipe => ConsoleKey.Oem5,
        _ => null
    };

    #endregion
}
