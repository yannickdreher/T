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

public partial class TerminalView : UserControl
{
    private SessionViewModel? _currentVm;
    private readonly TerminalControl? _terminal;
    private readonly TerminalStatsOverlay? _stats;
    private readonly DispatcherTimer _statsTimer;

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

        ApplySettings();

        _stats?.Model = vm.TerminalStats;
        _statsTimer.Start();
    }

    private void DetachViewModel()
    {
        if (_currentVm is null) return;

        _currentVm.OutputReceived -= OnOutputReceived;
        _currentVm.PropertyChanged -= OnViewModelPropertyChanged;
        _currentVm.TerminalSettings.PropertyChanged -= OnSettingsChanged;

        _statsTimer.Stop();
        _stats?.Model = null;
        _currentVm = null;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        DetachViewModel();
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
        if (e.PropertyName != nameof(SessionViewModel.TerminalSettings) || _currentVm is null) return;

        _currentVm.TerminalSettings.PropertyChanged -= OnSettingsChanged;
        _currentVm.TerminalSettings.PropertyChanged += OnSettingsChanged;
        ApplySettings();
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