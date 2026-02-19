using CommunityToolkit.Mvvm.ComponentModel;

namespace T.ViewModels
{
    /// <summary>
    /// ViewModel exposing terminal statistics for binding in the UI.
    /// Uses CommunityToolkit.Mvvm source generators to implement INotifyPropertyChanged.
    /// </summary>
    public sealed partial class TerminalStatsViewModel : ViewModelBase
    {
        // Columns / Rows
        [ObservableProperty] private int _columns;
        [ObservableProperty] private int _rows;

        // Scrollback / incoming buffer length
        [ObservableProperty] private int _scrollback;
        [ObservableProperty] private int _incomingLen;

        // Character metrics
        [ObservableProperty] private double _charWidth;
        [ObservableProperty] private double _lineHeight;

        // Cache stats
        [ObservableProperty] private long _hits;
        [ObservableProperty] private long _misses;
        [ObservableProperty] private long _inserts;
        [ObservableProperty] private long _evictions;
        [ObservableProperty] private int _currentCount;
        [ObservableProperty] private int _capacity;
    }
}
