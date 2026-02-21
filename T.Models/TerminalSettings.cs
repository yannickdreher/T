using CommunityToolkit.Mvvm.ComponentModel;

namespace T.Models;

public partial class TerminalSettings : ObservableObject
{
    [ObservableProperty] private bool _enableTerminalColors = true;
    [ObservableProperty] private int _terminalFontSize = 14;
    [ObservableProperty] private string _terminalFontFamily = "Consolas";
    [ObservableProperty] private string _cursorStyle = "Bar";
    [ObservableProperty] private string _cursorColor = "#00FF00";
    [ObservableProperty] private string _terminalBackground = "#000000";
    [ObservableProperty] private string _terminalForeground = "#CCCCCC";
    [ObservableProperty] private int _scrollbackLines = 10000;
    [ObservableProperty] private bool _cursorBlink = true;
    [ObservableProperty] private int _terminalPadding = 10;
}