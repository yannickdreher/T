using System.Text.Json.Serialization;
using Avalonia.Media;
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

    // Computed — nicht serialisieren
    [JsonIgnore] public Color TerminalBackgroundColor => Color.Parse(TerminalBackground);
    [JsonIgnore] public Color TerminalForegroundColor => Color.Parse(TerminalForeground);
    [JsonIgnore] public Color CursorColorValue => Color.Parse(CursorColor);
    [JsonIgnore] public FontFamily TerminalFont => new(TerminalFontFamily);

    partial void OnTerminalBackgroundChanged(string value) => OnPropertyChanged(nameof(TerminalBackgroundColor));
    partial void OnTerminalForegroundChanged(string value) => OnPropertyChanged(nameof(TerminalForegroundColor));
    partial void OnCursorColorChanged(string value) => OnPropertyChanged(nameof(CursorColorValue));
    partial void OnTerminalFontFamilyChanged(string value) => OnPropertyChanged(nameof(TerminalFont));
}