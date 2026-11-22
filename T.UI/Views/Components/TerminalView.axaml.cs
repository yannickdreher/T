using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using T.UI.Controls;
using T.UI.Extensions;
using T.UI.ViewModels;

namespace T.UI.Views.Components;

public partial class TerminalView : UserControl, IDisposable
{
    private SessionViewModel? _currentVm;
    private readonly TerminalControl? _terminal;
    private readonly TerminalStatsOverlay? _stats;
    private readonly Border _searchBar;
    private readonly TextBox _searchQuery;
    private readonly TextBlock _searchStatus;
    private readonly ToggleButton _searchMatchCase;
    private readonly Button _searchPrevious;
    private readonly Button _searchNext;
    private readonly CommandSuggestionController? _suggestions;
    private readonly DispatcherTimer _statsTimer;
    private bool _isDisposed;

    public TerminalView()
    {
        InitializeComponent();

        _terminal = this.FindControl<TerminalControl>("Terminal");
        _stats = this.FindControl<TerminalStatsOverlay>("Stats");
        _searchBar = this.FindControl<Border>("SearchBar")!;
        _searchQuery = this.FindControl<TextBox>("SearchQuery")!;
        _searchStatus = this.FindControl<TextBlock>("SearchStatus")!;
        _searchMatchCase = this.FindControl<ToggleButton>("SearchMatchCase")!;
        _searchPrevious = this.FindControl<Button>("SearchPrevious")!;
        _searchNext = this.FindControl<Button>("SearchNext")!;
        _searchBar.AddHandler(KeyDownEvent, OnSearchKeyDown, RoutingStrategies.Tunnel);
        _searchQuery.TextChanged += (_, _) => UpdateSearch();
        _searchMatchCase.IsCheckedChanged += (_, _) => UpdateSearch();

        if (_terminal != null)
        {
            _terminal.TerminalResized += OnTerminalResized;
            _terminal.InputReceived += OnInputReceived;
            _terminal.SearchRequested += OpenSearch;
            _terminal.SearchUpdated += ShowSearchState;

            if (this.FindControl<CommandSuggestionPopup>("Suggestions") is { } popup)
                _suggestions = new CommandSuggestionController(_terminal, popup);
        }

        _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _statsTimer.Tick += (_, _) =>
        {
            if (_currentVm != null && _terminal != null)
            {
                _terminal.PopulateStatsViewModel(_currentVm.TerminalStats);
            }
        };

        GotFocus += (_, e) =>
        {
            if (ReferenceEquals(e.Source, this))
                _terminal?.Focus();
        };
    }

    public TerminalView(SessionViewModel viewModel) : this()
    {
        AttachViewModel(viewModel);
    }

    private void AttachViewModel(SessionViewModel vm)
    {
        _currentVm = vm;
        DataContext = vm;

        _currentVm.OutputReceived += OnOutputReceived;
        _currentVm.CopyOutputRequested += OnCopyOutputRequested;
        _currentVm.ClearOutputRequested += OnClearOutputRequested;
        _currentVm.PropertyChanged += OnViewModelPropertyChanged;
        _currentVm.TerminalSettings.PropertyChanged += OnSettingsChanged;
        _currentVm.Disposed += OnSessionDisposed;

        ApplySettings();
        _suggestions?.Attach(vm);

        _stats?.Model = vm.TerminalStats;
        UpdateStatsOverlay();
    }

    private void DetachViewModel()
    {
        if (_currentVm is null) return;

        _currentVm.OutputReceived -= OnOutputReceived;
        _currentVm.CopyOutputRequested -= OnCopyOutputRequested;
        _currentVm.ClearOutputRequested -= OnClearOutputRequested;
        _currentVm.PropertyChanged -= OnViewModelPropertyChanged;
        _currentVm.TerminalSettings.PropertyChanged -= OnSettingsChanged;
        _currentVm.Disposed -= OnSessionDisposed;
        _suggestions?.Detach();

        _statsTimer.Stop();
        if (_stats != null) _stats.IsVisible = false;
        _stats?.Model = null;
        _currentVm = null;
    }

    private void OnSessionDisposed(SessionViewModel vm) => Dispose();

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        DetachViewModel();
        _suggestions?.Dispose();
        if (_terminal != null)
        {
            _terminal.SearchRequested -= OpenSearch;
            _terminal.SearchUpdated -= ShowSearchState;
        }
        _terminal?.Shutdown();
        GC.SuppressFinalize(this);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // The terminal may have been hidden in a background tab. Resume the stats
        // sampling now that it is visible again. VM subscriptions are kept alive
        // across tab switches so terminal output never stops flowing.
        UpdateStatsOverlay();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        // Switching to another tab detaches this view. Only pause the stats
        // sampling - the session wiring stays connected. Full teardown happens
        // when the session itself is disposed (see OnSessionDisposed).
        _statsTimer.Stop();
    }

    private void ApplySettings()
    {
        if (_terminal == null || _currentVm is null) return;
        var s = _currentVm.TerminalSettings;

        _terminal.FontFamily = s.GetFont();
        _terminal.FontSize = s.TerminalFontSize;
        _terminal.DefaultBackground = s.GetBackgroundColor();
        _terminal.DefaultForeground = s.GetForegroundColor();
        _terminal.CursorColor = s.GetCursorColor();
        _terminal.Padding = new Thickness(s.TerminalPadding);
        _terminal.CursorBlink = s.CursorBlink;
        _terminal.EnableColors = s.EnableTerminalColors;
        _terminal.ScrollbackLines = s.ScrollbackLines;

        _terminal.CursorStyle = s.CursorStyle switch
        {
            "Block" => TerminalCursorStyle.Block,
            "Underline" => TerminalCursorStyle.Underline,
            _ => TerminalCursorStyle.Bar
        };
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(ApplySettings);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_currentVm is null) return;

        if (e.PropertyName == nameof(SessionViewModel.TerminalSettings))
        {
            _currentVm.TerminalSettings.PropertyChanged -= OnSettingsChanged;
            _currentVm.TerminalSettings.PropertyChanged += OnSettingsChanged;
            ApplySettings();
        }
        else if (e.PropertyName == nameof(SessionViewModel.ShowTerminalStatsOverlay))
        {
            UpdateStatsOverlay();
        }
    }

    private void UpdateStatsOverlay()
    {
        if (_currentVm is null || _stats is null) return;

        var show = _currentVm.ShowTerminalStatsOverlay;
        _stats.IsVisible = show;

        if (show)
            _statsTimer.Start();
        else
            _statsTimer.Stop();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!_searchBar.IsPointerOver)
            _terminal?.Focus();
    }

    private void OpenSearch()
    {
        if (!_searchBar.IsVisible)
        {
            _searchBar.IsVisible = true;
            UpdateSearch();
        }
        _searchQuery.Focus();
        _searchQuery.SelectAll();
    }

    private void UpdateSearch()
    {
        if (_searchBar.IsVisible)
            _terminal?.SetSearch(_searchQuery.Text, _searchMatchCase.IsChecked == true);
    }

    private void ShowSearchState(int current, int count, bool searching)
    {
        _searchPrevious.IsEnabled = count > 0;
        _searchNext.IsEnabled = count > 0;

        string? query = _searchQuery.Text;
        if (searching)
            _searchStatus.Text = "Searching…";
        else if (count > 0)
            _searchStatus.Text = $"{current} of {count}";
        else if (string.IsNullOrEmpty(query))
            _searchStatus.Text = "";
        else if (!TerminalControl.IsSearchQueryValid(query))
            _searchStatus.Text = "Type 2 characters";
        else
            _searchStatus.Text = "No matches";
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseSearch();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && _searchQuery.IsKeyboardFocusWithin &&
                 e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift)
        {
            _terminal?.MoveSearch(e.KeyModifiers == KeyModifiers.Shift ? -1 : 1);
            e.Handled = true;
        }
    }

    private void OnSearchPrevious(object? sender, RoutedEventArgs e) => _terminal?.MoveSearch(-1);
    private void OnSearchNext(object? sender, RoutedEventArgs e) => _terminal?.MoveSearch(1);
    private void OnSearchClose(object? sender, RoutedEventArgs e) => CloseSearch();

    private void CloseSearch()
    {
        _searchBar.IsVisible = false;
        _terminal?.ClearSearch();
        _terminal?.Focus();
    }

    private void OnOutputReceived(string text) => _terminal?.AppendOutput(text);
    private void OnCopyOutputRequested() => _terminal?.CopyOutput();
    private void OnClearOutputRequested() => _terminal?.ClearOutput();
    private void OnInputReceived(string text) => _currentVm?.SendTerminalInput(text);
    private void OnTerminalResized(uint columns, uint rows, uint pixelWidth, uint pixelHeight)
    {
        _currentVm?.SetTerminalSize(columns, rows, pixelWidth, pixelHeight);
    }
}
