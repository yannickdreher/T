using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System;
using System.ComponentModel;
using T.UI.Controls;
using T.UI.Extensions;
using T.UI.ViewModels;

namespace T.UI.Views.Components;

public partial class TerminalView : UserControl, IDisposable
{
    private SessionViewModel? _currentVm;
    private readonly TerminalControl? _terminal;
    private readonly TerminalStatsOverlay? _stats;
    private readonly DispatcherTimer _statsTimer;
    private bool _isDisposed;

    public TerminalView()
    {
        InitializeComponent();

        _terminal = this.FindControl<TerminalControl>("Terminal");
        _stats = this.FindControl<TerminalStatsOverlay>("Stats");

        if (_terminal != null)
        {
            _terminal.TerminalResized += OnTerminalResized;
            _terminal.InputReceived += OnInputReceived;
        }

        _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _statsTimer.Tick += (_, _) =>
        {
            if (_currentVm != null && _terminal != null)
            {
                _terminal.PopulateStatsViewModel(_currentVm.TerminalStats);
            }
        };

        GotFocus += (_, _) => _terminal?.Focus();
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
        _currentVm.PropertyChanged += OnViewModelPropertyChanged;
        _currentVm.TerminalSettings.PropertyChanged += OnSettingsChanged;
        _currentVm.Disposed += OnSessionDisposed;

        ApplySettings();

        _stats?.Model = vm.TerminalStats;
        UpdateStatsOverlay();
    }

    private void DetachViewModel()
    {
        if (_currentVm is null) return;

        _currentVm.OutputReceived -= OnOutputReceived;
        _currentVm.PropertyChanged -= OnViewModelPropertyChanged;
        _currentVm.TerminalSettings.PropertyChanged -= OnSettingsChanged;
        _currentVm.Disposed -= OnSessionDisposed;

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
        _terminal?.Shutdown();
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
        _terminal?.Focus();
    }

    private void OnOutputReceived(string text) => _terminal?.AppendOutput(text);
    private void OnInputReceived(string text) => _currentVm?.SendTerminalInput(text);
    private void OnTerminalResized(uint columns, uint rows, uint pixelWidth, uint pixelHeight)
    {
        _currentVm?.SetTerminalSize(columns, rows, pixelWidth, pixelHeight);
    }
}