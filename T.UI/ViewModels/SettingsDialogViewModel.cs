using CommunityToolkit.Mvvm.ComponentModel;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.IO;
using T.Abstractions;
using T.Models;

namespace T.UI.ViewModels;

public partial class SettingsDialogViewModel : ObservableValidator
{
    private readonly ISettingsService? _settingsService;

    public GeneralSettingsViewModel General { get; }
    public UpdateSettingsViewModel Update { get; }
    public ExplorerSettingsViewModel Explorer { get; }
    public TerminalSettingsViewModel Terminal { get; }

    public bool HasAnyErrors =>
        General.HasErrors || Update.HasErrors || Explorer.HasErrors || Terminal.HasErrors;

    public SettingsDialogViewModel()
        : this(new AppSettings())
    {
    }

    public SettingsDialogViewModel(AppSettings settings)
    {
        General = new GeneralSettingsViewModel(settings.General);
        Update = new UpdateSettingsViewModel(settings.Update);
        Explorer = new ExplorerSettingsViewModel(settings.Explorer);
        Terminal = new TerminalSettingsViewModel(settings.Terminal);

        HookValidation();
    }

    public SettingsDialogViewModel(ISettingsService settingsService)
        : this(settingsService.Current)
    {
        _settingsService = settingsService;
    }

    public void ApplyTo()
    {
        if (_settingsService is null) return;
        ApplyTo(_settingsService.Current);
    }

    public void ApplyTo(AppSettings target)
    {
        General.ApplyTo(target.General);
        Update.ApplyTo(target.Update);
        Explorer.ApplyTo(target.Explorer);
        Terminal.ApplyTo(target.Terminal);
    }

    private void HookValidation()
    {
        General.ErrorsChanged += OnChildErrorsChanged;
        Update.ErrorsChanged += OnChildErrorsChanged;
        Explorer.ErrorsChanged += OnChildErrorsChanged;
        Terminal.ErrorsChanged += OnChildErrorsChanged;
        OnPropertyChanged(nameof(HasAnyErrors));
    }

    private void OnChildErrorsChanged(object? sender, DataErrorsChangedEventArgs e) =>
        OnPropertyChanged(nameof(HasAnyErrors));
}

public partial class GeneralSettingsViewModel : ObservableValidator
{
    [Required(ErrorMessage = "Language is required.")]
    [RegularExpression("^(System|English|Deutsch)$", ErrorMessage = "Language must be System, English, or Deutsch.")]
    [ObservableProperty] private string _language = "System";

    [Required(ErrorMessage = "Theme is required.")]
    [RegularExpression("^(System|Light|Dark)$", ErrorMessage = "Theme must be System, Light, or Dark.")]
    [ObservableProperty] private string _theme = "System";

    [ObservableProperty] private bool _confirmOnClose = true;

    [ObservableProperty] private bool _reconnectOnStartup;

    [Range(5, 120, ErrorMessage = "Connection timeout must be between 5 and 120 seconds.")]
    [ObservableProperty] private int _connectionTimeout = 15;

    [Range(10, 120, ErrorMessage = "Keep-alive interval must be between 10 and 120 seconds.")]
    [ObservableProperty] private int _keepAliveInterval = 30;

    public GeneralSettingsViewModel()
    {
        ValidateAllProperties();
    }

    public GeneralSettingsViewModel(GeneralSettings settings)
    {
        _language = settings.Language;
        _theme = settings.Theme;
        _confirmOnClose = settings.ConfirmOnClose;
        _reconnectOnStartup = settings.ReconnectOnStartup;
        _connectionTimeout = settings.ConnectionTimeout;
        _keepAliveInterval = settings.KeepAliveInterval;
        ValidateAllProperties();
    }

    public void ApplyTo(GeneralSettings target)
    {
        target.Language = Language;
        target.Theme = Theme;
        target.ConfirmOnClose = ConfirmOnClose;
        target.ReconnectOnStartup = ReconnectOnStartup;
        target.ConnectionTimeout = ConnectionTimeout;
        target.KeepAliveInterval = KeepAliveInterval;
    }

    partial void OnLanguageChanged(string value) => ValidateProperty(value, nameof(Language));
    partial void OnThemeChanged(string value) => ValidateProperty(value, nameof(Theme));
    partial void OnConnectionTimeoutChanged(int value) => ValidateProperty(value, nameof(ConnectionTimeout));
    partial void OnKeepAliveIntervalChanged(int value) => ValidateProperty(value, nameof(KeepAliveInterval));
}

public partial class UpdateSettingsViewModel : ObservableValidator
{
    [ObservableProperty] private bool _checkForUpdatesOnStartup = true;

    [Required(ErrorMessage = "Update channel is required.")]
    [RegularExpression("^(Stable|Beta)$", ErrorMessage = "Update channel must be Stable or Beta.")]
    [ObservableProperty] private string _updateChannel = "Stable";

    public UpdateSettingsViewModel()
    {
        ValidateAllProperties();
    }

    public UpdateSettingsViewModel(UpdateSettings settings)
    {
        _checkForUpdatesOnStartup = settings.CheckForUpdatesOnStartup;
        _updateChannel = settings.UpdateChannel;
        ValidateAllProperties();
    }

    public void ApplyTo(UpdateSettings target)
    {
        target.CheckForUpdatesOnStartup = CheckForUpdatesOnStartup;
        target.UpdateChannel = UpdateChannel;
    }

    partial void OnUpdateChannelChanged(string value) => ValidateProperty(value, nameof(UpdateChannel));
}

public partial class ExplorerSettingsViewModel : ObservableValidator
{
    [ObservableProperty] private bool _showHiddenFiles;
    [ObservableProperty] private bool _confirmDelete = true;
    [ObservableProperty] private bool _doubleClickToOpen = true;

    [CustomValidation(typeof(SettingsValidators), nameof(SettingsValidators.ValidateDownloadPath))]
    [ObservableProperty] private string _defaultDownloadPath = "";

    [Required(ErrorMessage = "Sort by is required.")]
    [RegularExpression("^(Name|Size|Date|Permissions)$", ErrorMessage = "Sort by must be Name, Size, Date, or Permissions.")]
    [ObservableProperty] private string _sortBy = "Name";

    public ExplorerSettingsViewModel()
    {
        ValidateAllProperties();
    }

    public ExplorerSettingsViewModel(ExplorerSettings settings)
    {
        _showHiddenFiles = settings.ShowHiddenFiles;
        _confirmDelete = settings.ConfirmDelete;
        _doubleClickToOpen = settings.DoubleClickToOpen;
        _defaultDownloadPath = settings.DefaultDownloadPath;
        _sortBy = settings.SortBy;
        ValidateAllProperties();
    }

    public void ApplyTo(ExplorerSettings target)
    {
        target.ShowHiddenFiles = ShowHiddenFiles;
        target.ConfirmDelete = ConfirmDelete;
        target.DoubleClickToOpen = DoubleClickToOpen;
        target.DefaultDownloadPath = DefaultDownloadPath;
        target.SortBy = SortBy;
    }

    partial void OnDefaultDownloadPathChanged(string value) => ValidateProperty(value, nameof(DefaultDownloadPath));
    partial void OnSortByChanged(string value) => ValidateProperty(value, nameof(SortBy));
}

public partial class TerminalSettingsViewModel : ObservableValidator
{
    [ObservableProperty] private bool _enableTerminalColors = true;

    [Range(8, 32, ErrorMessage = "Font size must be between 8 and 32.")]
    [ObservableProperty] private int _terminalFontSize = 14;

    [Required(ErrorMessage = "Font family is required.")]
    [ObservableProperty] private string _terminalFontFamily = "Consolas";

    [Required(ErrorMessage = "Cursor style is required.")]
    [RegularExpression("^(Bar|Block|Underline)$", ErrorMessage = "Cursor style must be Bar, Block, or Underline.")]
    [ObservableProperty] private string _cursorStyle = "Bar";

    [RegularExpression("^#([0-9A-Fa-f]{6})$", ErrorMessage = "Cursor color must be in #RRGGBB format.")]
    [ObservableProperty] private string _cursorColor = "#00FF00";

    [RegularExpression("^#([0-9A-Fa-f]{6})$", ErrorMessage = "Background color must be in #RRGGBB format.")]
    [ObservableProperty] private string _terminalBackground = "#000000";

    [RegularExpression("^#([0-9A-Fa-f]{6})$", ErrorMessage = "Foreground color must be in #RRGGBB format.")]
    [ObservableProperty] private string _terminalForeground = "#CCCCCC";

    [Range(1000, 100000, ErrorMessage = "Scrollback lines must be between 1,000 and 100,000.")]
    [ObservableProperty] private int _scrollbackLines = 10000;

    [ObservableProperty] private bool _cursorBlink = true;

    [Range(0, 32, ErrorMessage = "Terminal padding must be between 0 and 32.")]
    [ObservableProperty] private int _terminalPadding = 10;

    public TerminalSettingsViewModel()
    {
        ValidateAllProperties();
    }

    public TerminalSettingsViewModel(TerminalSettings settings)
    {
        _enableTerminalColors = settings.EnableTerminalColors;
        _terminalFontSize = settings.TerminalFontSize;
        _terminalFontFamily = settings.TerminalFontFamily;
        _cursorStyle = settings.CursorStyle;
        _cursorColor = settings.CursorColor;
        _terminalBackground = settings.TerminalBackground;
        _terminalForeground = settings.TerminalForeground;
        _scrollbackLines = settings.ScrollbackLines;
        _cursorBlink = settings.CursorBlink;
        _terminalPadding = settings.TerminalPadding;
        ValidateAllProperties();
    }

    public void ApplyTo(TerminalSettings target)
    {
        target.EnableTerminalColors = EnableTerminalColors;
        target.TerminalFontSize = TerminalFontSize;
        target.TerminalFontFamily = TerminalFontFamily;
        target.CursorStyle = CursorStyle;
        target.CursorColor = CursorColor;
        target.TerminalBackground = TerminalBackground;
        target.TerminalForeground = TerminalForeground;
        target.ScrollbackLines = ScrollbackLines;
        target.CursorBlink = CursorBlink;
        target.TerminalPadding = TerminalPadding;
    }

    partial void OnTerminalFontSizeChanged(int value) => ValidateProperty(value, nameof(TerminalFontSize));
    partial void OnTerminalFontFamilyChanged(string value) => ValidateProperty(value, nameof(TerminalFontFamily));
    partial void OnCursorStyleChanged(string value) => ValidateProperty(value, nameof(CursorStyle));
    partial void OnCursorColorChanged(string value) => ValidateProperty(value, nameof(CursorColor));
    partial void OnTerminalBackgroundChanged(string value) => ValidateProperty(value, nameof(TerminalBackground));
    partial void OnTerminalForegroundChanged(string value) => ValidateProperty(value, nameof(TerminalForeground));
    partial void OnScrollbackLinesChanged(int value) => ValidateProperty(value, nameof(ScrollbackLines));
    partial void OnTerminalPaddingChanged(int value) => ValidateProperty(value, nameof(TerminalPadding));
}

public static class SettingsValidators
{
    public static ValidationResult? ValidateDownloadPath(string? value, ValidationContext context)
    {
        if (string.IsNullOrWhiteSpace(value))
            return ValidationResult.Success;

        return Path.IsPathRooted(value)
            ? ValidationResult.Success
            : new ValidationResult("Default download path must be empty or an absolute path.");
    }
}