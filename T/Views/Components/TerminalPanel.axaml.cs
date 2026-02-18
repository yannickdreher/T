using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System;
using System.ComponentModel;
using T.Controls;
using T.Services;
using T.ViewModels;

namespace T.Views.Components;

public partial class TerminalPanel : UserControl
{
    private readonly TerminalControl? _terminal;
    private SessionViewModel? _currentVm;
    
    public TerminalPanel()
    {
        InitializeComponent();
        
        _terminal = this.FindControl<TerminalControl>("Terminal");
        if (_terminal != null)
        {
            _terminal.TerminalResized += OnTerminalResized;
            _terminal.InputReceived += OnInputReceived;
            
            ApplySettings();
        }
        
        Loaded += OnLoaded;
        GotFocus += (_, _) => _terminal?.Focus();
    }
    
    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (_terminal != null)
        {
            SettingsService.Current.PropertyChanged += OnSettingsChanged;
            SettingsService.Current.Terminal.PropertyChanged += OnSettingsChanged;
            _terminal.Focus();
        }
        
        SubscribeToViewModel();
    }

    private void ApplySettings()
    {
        if (_terminal == null) return;
        var s = SettingsService.Current.Terminal;

        _terminal.FontFamily = s.TerminalFont;
        _terminal.FontSize = s.TerminalFontSize;
        _terminal.DefaultBackground = s.TerminalBackgroundColor;
        _terminal.DefaultForeground = s.TerminalForegroundColor;
        _terminal.CursorColor = s.CursorColorValue;
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

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        SubscribeToViewModel();
    }
    
    private void SubscribeToViewModel()
    {
        if (_currentVm != null)
        {
            _currentVm.OutputReceived -= OnOutputReceived;
            _currentVm = null;
        }
        
        if (DataContext is SessionViewModel vm)
        {
            _currentVm = vm;
            _currentVm.OutputReceived += OnOutputReceived;
        }
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