using System.Text.Json.Serialization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace T.Models;

public partial class AppSettings : ObservableObject
{
    // General
    [ObservableProperty] private string _language = "System";
    [ObservableProperty] private string _theme = "System";
    [ObservableProperty] private bool _confirmOnClose = true;
    [ObservableProperty] private bool _reconnectOnStartup;
    [ObservableProperty] private int _connectionTimeout = 15;
    [ObservableProperty] private int _keepAliveInterval = 30;

    // Updates
    [ObservableProperty] private bool _checkForUpdatesOnStartup = true;
    [ObservableProperty] private string _updateChannel = "Stable";

    // Explorer
    [ObservableProperty] private bool _showHiddenFiles;
    [ObservableProperty] private bool _confirmDelete = true;
    [ObservableProperty] private string _defaultDownloadPath = "";
    [ObservableProperty] private bool _doubleClickToOpen = true;
    [ObservableProperty] private string _sortBy = "Name";

    // Terminal
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