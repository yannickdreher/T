using CommunityToolkit.Mvvm.ComponentModel;

namespace T.UI.ViewModels;

/// <summary>
/// Terminal diagnostics shown in the stats overlay (toggle in the status bar).
/// </summary>
public sealed partial class TerminalStatsViewModel : ViewModelBase
{
    // Size
    [ObservableProperty] private int _columns;
    [ObservableProperty] private int _rows;

    // Buffers
    [ObservableProperty] private int _scrollback;
    [ObservableProperty] private int _incomingLen;

    // Character metrics
    [ObservableProperty] private double _charWidth;
    [ObservableProperty] private double _lineHeight;

    // Renderer
    [ObservableProperty] private long _framesDrawn;
    [ObservableProperty] private long _rowsBuilt;
    [ObservableProperty] private long _glyphLookups;
    [ObservableProperty] private int _cachedGlyphs;
    [ObservableProperty] private int _fallbackFonts;
}
