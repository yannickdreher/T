using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using T.VT;

namespace T.UI.Controls;

/// <summary>
/// Turns terminal rows into cached drawing data.
/// <para>
/// A terminal is a fixed character grid, so glyphs are looked up directly in the font
/// (with system fallback fonts for missing characters) and placed with a fixed advance -
/// no text shaping is needed. The resulting <see cref="GlyphRun"/>s are cached per screen
/// row and only rebuilt for rows the emulator reports as damaged. When the whole screen
/// scrolls, the row caches are shifted instead of rebuilt, so a scrolling log only builds
/// the new lines. Drawing a cached glyph run is cheap and rasterized by Skia on the GPU.
/// </para>
/// <para>
/// Double-width characters (CJK, emoji) get a box of two cells. Fallback glyphs are
/// centered in their box, or scaled down when they are wider than it (emoji fonts), so
/// they never paint over their neighbors. Characters with combining marks are shaped
/// individually with the platform text shaper.
/// </para>
/// </summary>
internal sealed class TerminalRowRenderer
{
    [Flags]
    private enum Decoration : byte { None = 0, Underline = 1, DoubleUnderline = 2, Strikethrough = 4 }

    private sealed class RowCache
    {
        public readonly List<(Rect Rect, IBrush Brush)> Backgrounds = [];
        public readonly List<(GlyphRun Run, IBrush Brush)> Glyphs = [];
        public readonly List<(double X, double Width, Decoration Decoration, Color Color)> Decorations = [];
        public bool IsValid;

        public void Clear()
        {
            foreach (var (run, _) in Glyphs)
                run.Dispose(); // the compositor keeps its own reference to already recorded runs
            Glyphs.Clear();
            Backgrounds.Clear();
            Decorations.Clear();
            IsValid = false;
        }
    }

    /// <summary>A glyph and its natural advance at the current font size.</summary>
    private readonly record struct GlyphEntry(GlyphTypeface Face, ushort Glyph, double Width);

    /// <summary>A shaped character with combining marks (glyphs positioned at the current font size).</summary>
    private sealed record ClusterEntry(GlyphTypeface Face, GlyphInfo[] Glyphs, double Width);

    private RowCache[] _rows = [];
    private bool[] _damage = [];

    // Regular, Bold, Italic, BoldItalic
    private readonly GlyphTypeface?[] _faces = new GlyphTypeface?[4];
    private readonly Dictionary<int, GlyphEntry> _glyphCache = [];
    private readonly Dictionary<(string Text, int Style), ClusterEntry> _clusterCache = [];
    private readonly Dictionary<GlyphTypeface, ushort> _spaceGlyphs = [];
    private readonly HashSet<GlyphTypeface> _fallbackFaces = [];
    private readonly Dictionary<Color, SolidColorBrush> _brushes = [];
    private readonly Dictionary<Color, Pen> _pens = [];
    private GlyphTypeface? _emojiFace;
    private bool _emojiFaceResolved;

    // Scratch buffers for building one run.
    private readonly List<GlyphInfo> _runGlyphs = new(256);
    private readonly StringBuilder _runChars = new(256);

    public double CharWidth { get; private set; }
    public double LineHeight { get; private set; }
    public double Baseline { get; private set; }
    public double FontSize { get; private set; }

    public Color DefaultForeground { get; set; } = Color.FromRgb(204, 204, 204);
    public Color DefaultBackground { get; set; } = Color.FromRgb(12, 12, 12);
    public bool EnableColors { get; set; } = true;

    // Diagnostics
    public long RowsBuilt { get; private set; }
    public long FramesDrawn { get; private set; }
    public long GlyphLookups { get; private set; }
    public int CachedGlyphs => _glyphCache.Count + _clusterCache.Count;
    public int FallbackFonts => _fallbackFaces.Count;

    public void SetFont(FontFamily family, double fontSize)
    {
        FontSize = fontSize;

        var regular = new Typeface(family);
        var probe = new FormattedText("M", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, regular, fontSize, Brushes.White);
        CharWidth = probe.WidthIncludingTrailingWhitespace;
        LineHeight = probe.Height;
        Baseline = probe.Baseline;

        var regularFace = ResolveFace(regular) ?? ResolveFace(Typeface.Default)
            ?? throw new InvalidOperationException("No usable font found.");
        _faces[0] = regularFace;
        _faces[1] = ResolveFace(new Typeface(family, FontStyle.Normal, FontWeight.Bold)) ?? regularFace;
        _faces[2] = ResolveFace(new Typeface(family, FontStyle.Italic)) ?? regularFace;
        _faces[3] = ResolveFace(new Typeface(family, FontStyle.Italic, FontWeight.Bold)) ?? _faces[1];

        _glyphCache.Clear();
        _clusterCache.Clear();
        _spaceGlyphs.Clear();
        _fallbackFaces.Clear();
        InvalidateAll();
    }

    private static GlyphTypeface? ResolveFace(Typeface typeface) =>
        FontManager.Current.TryGetGlyphTypeface(typeface, out var face) ? face : null;

    public void InvalidateAll()
    {
        foreach (var row in _rows) row.Clear();
    }

    private void EnsureRows(int count)
    {
        if (_rows.Length == count) return;

        foreach (var row in _rows) row.Clear();
        _rows = new RowCache[count];
        for (int i = 0; i < count; i++) _rows[i] = new RowCache();
        _damage = new bool[count];
    }

    /// <summary>
    /// Applies the emulator's damage to the row caches. Returns false when nothing changed.
    /// With <paramref name="viewportDetached"/> (scrolled back into the history) screen rows do
    /// not map to viewport rows, so everything is rebuilt.
    /// </summary>
    public bool ApplyDamage(VirtualTerminal terminal, bool viewportDetached)
    {
        EnsureRows(terminal.Height);
        if (!terminal.TakeDamage(_damage, out int scrolled, out bool full))
            return false;

        if (viewportDetached || full || scrolled >= _rows.Length)
        {
            InvalidateAll();
            return true;
        }

        if (scrolled > 0)
            ShiftRows(scrolled);

        for (int i = 0; i < _rows.Length; i++)
        {
            if (_damage[i]) _rows[i].Clear();
        }
        return true;
    }

    /// <summary>The screen moved up by <paramref name="lines"/>: reuse the caches of the rows that are still visible.</summary>
    private void ShiftRows(int lines)
    {
        var recycled = new RowCache[lines];
        Array.Copy(_rows, 0, recycled, 0, lines);
        Array.Copy(_rows, lines, _rows, 0, _rows.Length - lines);
        for (int i = 0; i < lines; i++)
        {
            recycled[i].Clear();
            _rows[_rows.Length - lines + i] = recycled[i];
        }
    }

    // ── Drawing ──────────────────────────────────────────────────────────

    public void Draw(DrawingContext context, VirtualTerminal terminal, int scrollOffset, Point origin)
    {
        EnsureRows(terminal.Height);
        FramesDrawn++;

        int width = terminal.Width;
        int height = terminal.Height;
        int scrollbackCount = terminal.Scrollback.Count;
        int visibleStart = Math.Max(0, scrollbackCount - scrollOffset);

        for (int screenRow = 0; screenRow < height; screenRow++)
        {
            int bufferIndex = visibleStart + screenRow;
            bool isHistory = bufferIndex < scrollbackCount;
            int rowInBuffer = isHistory ? bufferIndex : bufferIndex - scrollbackCount;
            if (!isHistory && rowInBuffer >= height) break;

            var cache = _rows[screenRow];
            if (!cache.IsValid)
            {
                var line = isHistory ? terminal.Scrollback[rowInBuffer] : terminal.Buffer[rowInBuffer];
                BuildRow(cache, line, width, terminal.Graphemes);
            }

            if (cache.Backgrounds.Count == 0 && cache.Glyphs.Count == 0)
                continue;

            using (context.PushTransform(Matrix.CreateTranslation(origin.X, origin.Y + screenRow * LineHeight)))
            {
                foreach (var (rect, brush) in cache.Backgrounds)
                    context.FillRectangle(brush, rect);

                foreach (var (run, brush) in cache.Glyphs)
                    context.DrawGlyphRun(brush, run);

                foreach (var (x, runWidth, decoration, color) in cache.Decorations)
                    DrawDecoration(context, x, runWidth, decoration, color);
            }
        }
    }

    private void DrawDecoration(DrawingContext context, double x, double width, Decoration decoration, Color color)
    {
        var pen = GetPen(color);
        if ((decoration & Decoration.Underline) != 0)
        {
            double y = LineHeight - 1.5;
            context.DrawLine(pen, new Point(x, y), new Point(x + width, y));
        }
        if ((decoration & Decoration.DoubleUnderline) != 0)
        {
            double y = LineHeight - 1.5;
            context.DrawLine(pen, new Point(x, y), new Point(x + width, y));
            context.DrawLine(pen, new Point(x, y - 2), new Point(x + width, y - 2));
        }
        if ((decoration & Decoration.Strikethrough) != 0)
        {
            double y = LineHeight * 0.55;
            context.DrawLine(pen, new Point(x, y), new Point(x + width, y));
        }
    }

    /// <summary>Draws the content of one cell (block cursor) spanning <paramref name="cells"/> columns at <paramref name="topLeft"/>.</summary>
    public void DrawCell(DrawingContext context, TerminalCharacter cell, int cells, GraphemeTable graphemes, Point topLeft, Color color)
    {
        int codePoint = cell.CodePoint;
        if (codePoint is 0 or ' ' || cell.IsHidden || cell.IsWideContinuation) return;

        int style = (cell.IsBold ? 1 : 0) | (cell.IsItalic ? 2 : 0);
        using var run = GraphemeTable.IsCluster(codePoint)
            ? CreateClusterRun(graphemes.GetText(codePoint), style, cells, topLeft.X)
            : CreateSingleGlyphRun(codePoint, style, cells, topLeft.X);
        if (run is null) return;

        using (context.PushTransform(Matrix.CreateTranslation(0, topLeft.Y)))
            context.DrawGlyphRun(GetBrush(color), run);
    }

    // ── Row building ─────────────────────────────────────────────────────

    private void BuildRow(RowCache cache, TerminalLine line, int width, GraphemeTable graphemes)
    {
        RowsBuilt++;
        cache.IsValid = true;

        var defaultFg = DefaultForeground;
        var defaultBg = DefaultBackground;

        int bgStart = -1;
        Color bgColor = defaultBg;

        var run = new RunState();

        for (int col = 0; col < width; col++)
        {
            var cell = line[col];

            var fg = ResolveForeground(cell, defaultFg);
            var bg = ResolveBackground(cell, defaultBg);
            if (cell.IsInverse) (fg, bg) = (bg, fg);
            if (cell.IsDim)
                fg = Color.FromRgb((byte)((fg.R + bg.R) / 2), (byte)((fg.G + bg.G) / 2), (byte)((fg.B + bg.B) / 2));

            if (bg != bgColor)
            {
                if (bgStart != -1 && bgColor != defaultBg)
                    cache.Backgrounds.Add((new Rect(bgStart * CharWidth, 0, (col - bgStart) * CharWidth, LineHeight), GetBrush(bgColor)));
                bgColor = bg;
                bgStart = col;
            }

            int codePoint = cell.CodePoint;
            if (codePoint == 0 || cell.IsHidden || cell.IsWideContinuation)
                codePoint = ' '; // hidden text, or the right half of a wide character whose left half is gone

            var decoration =
                (cell.IsUnderline ? Decoration.Underline : Decoration.None) |
                ((cell.Attributes & CellAttributes.DoubleUnderline) != 0 ? Decoration.DoubleUnderline : Decoration.None) |
                (cell.IsStrikethrough ? Decoration.Strikethrough : Decoration.None);

            if (codePoint == ' ' && decoration == Decoration.None)
            {
                // Blank cells never start a run; inside an undecorated run they keep it in one piece.
                if (run.Active && run.Decoration == Decoration.None && TryGetSpace(run.Face!, out var space))
                    run.AppendSpace(space, CharWidth, _runGlyphs, _runChars);
                else
                    FlushRun(cache, ref run);
                continue;
            }

            // A wide character covers this cell and the continuation cell to its right.
            int cells = cell.IsWide && codePoint != ' ' && col + 1 < width && line[col + 1].IsWideContinuation ? 2 : 1;
            int style = (cell.IsBold ? 1 : 0) | (cell.IsItalic ? 2 : 0);

            if (GraphemeTable.IsCluster(codePoint))
            {
                // Shaped on its own: marks need positioning that a fixed-advance run cannot express.
                FlushRun(cache, ref run);
                var clusterRun = CreateClusterRun(graphemes.GetText(codePoint), style, cells, col * CharWidth);
                if (clusterRun != null)
                {
                    cache.Glyphs.Add((clusterRun, GetBrush(fg)));
                    if (decoration != Decoration.None)
                        cache.Decorations.Add((col * CharWidth, cells * CharWidth, decoration, fg));
                }
                col += cells - 1;
                continue;
            }

            var entry = ResolveGlyph(codePoint, style);
            var (size, offset) = Fit(entry.Width, cells * CharWidth);

            if (!run.Active || run.Color != fg || run.Decoration != decoration || !ReferenceEquals(run.Face, entry.Face) || run.Size != size)
            {
                FlushRun(cache, ref run);
                run = new RunState { Active = true, StartColumn = col, Color = fg, Decoration = decoration, Face = entry.Face, Size = size };
            }

            run.AppendGlyph(entry.Glyph, codePoint, cells, CharWidth, offset, _runGlyphs, _runChars);
            col += cells - 1;
        }

        if (bgStart != -1 && bgColor != defaultBg)
            cache.Backgrounds.Add((new Rect(bgStart * CharWidth, 0, (width - bgStart) * CharWidth, LineHeight), GetBrush(bgColor)));

        FlushRun(cache, ref run);
    }

    /// <summary>
    /// Placement of a glyph with the natural width <paramref name="glyphWidth"/> in a box of
    /// <paramref name="boxWidth"/>: scaled down when it does not fit, centered when it is narrower.
    /// Glyphs of the (monospaced) terminal font fit exactly and are left untouched.
    /// </summary>
    private (double Size, double Offset) Fit(double glyphWidth, double boxWidth)
    {
        if (glyphWidth > boxWidth * 1.05)
            return (Math.Round(FontSize * boxWidth / glyphWidth, 2), 0);
        if (glyphWidth < boxWidth - 0.5)
            return (FontSize, (boxWidth - glyphWidth) / 2);
        return (FontSize, 0);
    }

    private struct RunState
    {
        public bool Active;
        public int StartColumn;
        public int Cells;
        public int TrailingSpaces;
        public Color Color;
        public Decoration Decoration;
        public GlyphTypeface? Face;
        public double Size;

        public void AppendGlyph(ushort glyph, int codePoint, int cells, double charWidth, double offset, List<GlyphInfo> glyphs, StringBuilder chars)
        {
            glyphs.Add(new GlyphInfo(glyph, chars.Length, cells * charWidth, new Vector(offset, 0)));
            if (codePoint > 0xFFFF) chars.Append(char.ConvertFromUtf32(codePoint));
            else chars.Append((char)codePoint);
            Cells += cells;
            TrailingSpaces = 0;
        }

        public void AppendSpace(ushort glyph, double charWidth, List<GlyphInfo> glyphs, StringBuilder chars)
        {
            glyphs.Add(new GlyphInfo(glyph, chars.Length, charWidth));
            chars.Append(' ');
            Cells++;
            TrailingSpaces++;
        }
    }

    private void FlushRun(RowCache cache, ref RunState run)
    {
        if (!run.Active) return;

        if (run.TrailingSpaces > 0)
        {
            _runGlyphs.RemoveRange(_runGlyphs.Count - run.TrailingSpaces, run.TrailingSpaces);
            _runChars.Length -= run.TrailingSpaces;
            run.Cells -= run.TrailingSpaces;
        }

        if (_runGlyphs.Count > 0)
        {
            double x = run.StartColumn * CharWidth;
            var glyphRun = new GlyphRun(run.Face!, run.Size, _runChars.ToString().AsMemory(), _runGlyphs.ToArray(), new Point(x, Baseline));
            cache.Glyphs.Add((glyphRun, GetBrush(run.Color)));
            if (run.Decoration != Decoration.None)
                cache.Decorations.Add((x, run.Cells * CharWidth, run.Decoration, run.Color));
        }

        _runGlyphs.Clear();
        _runChars.Clear();
        run = default;
    }

    private GlyphRun CreateSingleGlyphRun(int codePoint, int style, int cells, double x)
    {
        var entry = ResolveGlyph(codePoint, style);
        var (size, offset) = Fit(entry.Width, cells * CharWidth);
        return new GlyphRun(entry.Face, size, char.ConvertFromUtf32(codePoint).AsMemory(),
            [new GlyphInfo(entry.Glyph, 0, cells * CharWidth, new Vector(offset, 0))], new Point(x, Baseline));
    }

    private GlyphRun? CreateClusterRun(string text, int style, int cells, double x)
    {
        var cluster = ResolveCluster(text, style);
        if (cluster is null) return null;

        var (size, offset) = Fit(cluster.Width, cells * CharWidth);
        double scale = size / FontSize;
        var glyphs = new GlyphInfo[cluster.Glyphs.Length];
        for (int i = 0; i < glyphs.Length; i++)
        {
            var g = cluster.Glyphs[i];
            glyphs[i] = new GlyphInfo(g.GlyphIndex, g.GlyphCluster, g.GlyphAdvance * scale, g.GlyphOffset * scale);
        }
        return new GlyphRun(cluster.Face, size, text.AsMemory(), glyphs, new Point(x + offset, Baseline));
    }

    // ── Glyphs, colors, brushes ──────────────────────────────────────────

    private GlyphEntry ResolveGlyph(int codePoint, int style)
    {
        GlyphLookups++;
        int key = codePoint * 4 + style; // code points are < 0x110000, so this never overflows
        if (_glyphCache.TryGetValue(key, out var cached))
            return cached;

        var face = _faces[style] ?? _faces[0]!;
        GlyphEntry entry;
        if (face.CharacterToGlyphMap.TryGetGlyph(codePoint, out var glyph) && glyph != 0)
        {
            entry = CreateEntry(face, glyph);
        }
        else if (TryFallback(codePoint, style, out var fallback))
        {
            entry = fallback;
        }
        else
        {
            // No installed font has the character: show '?' like most terminals.
            face.CharacterToGlyphMap.TryGetGlyph('?', out var question);
            entry = CreateEntry(face, question);
        }

        _glyphCache[key] = entry;
        return entry;
    }

    private GlyphEntry CreateEntry(GlyphTypeface face, ushort glyph)
    {
        double width = CharWidth;
        if (face.TryGetHorizontalGlyphAdvance(glyph, out var advance) && face.Metrics.DesignEmHeight > 0)
            width = advance * FontSize / face.Metrics.DesignEmHeight;
        return new GlyphEntry(face, glyph, width);
    }

    private bool TryFallback(int codePoint, int style, out GlyphEntry entry)
    {
        entry = default;
        if (MatchFace(codePoint, style) is not { } face ||
            !face.CharacterToGlyphMap.TryGetGlyph(codePoint, out var glyph) || glyph == 0)
            return false;

        entry = CreateEntry(face, glyph);
        return true;
    }

    private GlyphTypeface? MatchFace(int codePoint, int style)
    {
        var fontStyle = (style & 2) != 0 ? FontStyle.Italic : FontStyle.Normal;
        var weight = (style & 1) != 0 ? FontWeight.Bold : FontWeight.Normal;

        if (!FontManager.Current.TryMatchCharacter(codePoint, fontStyle, weight, FontStretch.Normal, null, CultureInfo.CurrentUICulture, out var typeface) ||
            ResolveFace(typeface) is not { } face)
            return null;

        _fallbackFaces.Add(face);
        return face;
    }

    /// <summary>The system's color emoji font (for text followed by VARIATION SELECTOR-16).</summary>
    private GlyphTypeface? EmojiFace
    {
        get
        {
            if (!_emojiFaceResolved)
            {
                _emojiFaceResolved = true;
                _emojiFace = MatchFace(0x1F600, 0); // 😀 - only color emoji fonts have it
            }
            return _emojiFace;
        }
    }

    private ClusterEntry? ResolveCluster(string text, int style)
    {
        if (_clusterCache.TryGetValue((text, style), out var cached))
            return cached;

        int baseCodePoint = char.ConvertToUtf32(text, 0);
        var face = ResolveGlyph(baseCodePoint, style).Face;

        // An emoji presentation selector asks for the color emoji font.
        if (text.Contains('\uFE0F') && EmojiFace is { } emoji && emoji.CharacterToGlyphMap.TryGetGlyph(baseCodePoint, out var g) && g != 0)
            face = emoji;

        var glyphs = Shape(text, face);
        if (glyphs is null)
            return null;

        // Marks the base font lacks: shape the whole cluster with a font that has the first missing one.
        if (Array.Exists(glyphs, x => x.GlyphIndex == 0))
        {
            foreach (var rune in text.EnumerateRunes())
            {
                if (face.CharacterToGlyphMap.TryGetGlyph(rune.Value, out var existing) && existing != 0) continue;
                if (MatchFace(rune.Value, style) is { } other && Shape(text, other) is { } reshaped && !Array.Exists(reshaped, x => x.GlyphIndex == 0))
                {
                    face = other;
                    glyphs = reshaped;
                }
                break;
            }
        }

        double width = 0;
        foreach (var glyph in glyphs) width += glyph.GlyphAdvance;

        var entry = new ClusterEntry(face, glyphs, width);
        _clusterCache[(text, style)] = entry;
        return entry;
    }

    private GlyphInfo[]? Shape(string text, GlyphTypeface face)
    {
        try
        {
            using var shaped = TextShaper.Current.ShapeText(text, new TextShaperOptions(face, FontSize));
            var glyphs = new GlyphInfo[shaped.Length];
            for (int i = 0; i < glyphs.Length; i++) glyphs[i] = shaped[i];
            return glyphs;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private bool TryGetSpace(GlyphTypeface face, out ushort glyph)
    {
        if (_spaceGlyphs.TryGetValue(face, out glyph))
            return glyph != 0;
        face.CharacterToGlyphMap.TryGetGlyph(' ', out glyph);
        _spaceGlyphs[face] = glyph;
        return glyph != 0;
    }

    private Color ResolveForeground(in TerminalCharacter cell, Color defaultFg)
    {
        if (!EnableColors) return defaultFg;

        var fg = cell.Foreground;
        // Bold-as-bright for the 8 basic ANSI colors (classic terminal behavior)
        if (cell.IsBold && fg.IsPalette && fg.PaletteIndex < 8)
            fg = fg.ToBright();

        if (fg.IsDefault) return defaultFg;
        var (r, g, b) = fg.ToRgb();
        return Color.FromRgb(r, g, b);
    }

    private Color ResolveBackground(in TerminalCharacter cell, Color defaultBg)
    {
        if (!EnableColors || cell.Background.IsDefault) return defaultBg;
        var (r, g, b) = cell.Background.ToRgb();
        return Color.FromRgb(r, g, b);
    }

    public SolidColorBrush GetBrush(Color color)
    {
        if (!_brushes.TryGetValue(color, out var brush))
        {
            if (_brushes.Count > 1024) _brushes.Clear();
            brush = new SolidColorBrush(color);
            _brushes[color] = brush;
        }
        return brush;
    }

    public Pen GetPen(Color color)
    {
        if (!_pens.TryGetValue(color, out var pen))
        {
            if (_pens.Count > 256) _pens.Clear();
            pen = new Pen(GetBrush(color), 1);
            _pens[color] = pen;
        }
        return pen;
    }
}
