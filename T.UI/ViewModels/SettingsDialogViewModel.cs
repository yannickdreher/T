using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using T.Abstractions;
using T.Models;
using T.Services;

namespace T.UI.ViewModels;

public partial class SettingsDialogViewModel : ObservableValidator
{
    private readonly ISettingsService? _settingsService;
    private readonly IStepCertificateService? _stepCertificateService;

    public GeneralSettingsViewModel General { get; }
    public UpdateSettingsViewModel Update { get; }
    public ExplorerSettingsViewModel Explorer { get; }
    public TerminalSettingsViewModel Terminal { get; }
    public StepSettingsViewModel Step { get; }

    public bool HasAnyErrors =>
        General.HasErrors || Update.HasErrors || Explorer.HasErrors || Terminal.HasErrors || Step.HasAnyErrors;

    public SettingsDialogViewModel()
        : this(new AppSettings())
    {
    }

    public SettingsDialogViewModel(AppSettings settings, IStepCertificateService? stepCertificateService = null)
    {
        _stepCertificateService = stepCertificateService;
        General = new GeneralSettingsViewModel(settings.General);
        Update = new UpdateSettingsViewModel(settings.Update);
        Explorer = new ExplorerSettingsViewModel(settings.Explorer);
        Terminal = new TerminalSettingsViewModel(settings.Terminal);
        Step = new StepSettingsViewModel(settings.Step, stepCertificateService);

        HookValidation();
    }

    public SettingsDialogViewModel(ISettingsService settingsService, IStepCertificateService stepCertificateService)
        : this(settingsService.Current, stepCertificateService)
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

        // The key pair of a removed CA profile must not stay on disk.
        foreach (var removedId in Step.ApplyTo(target.Step))
            _stepCertificateService?.Forget(removedId);
    }

    private void HookValidation()
    {
        General.ErrorsChanged += OnChildErrorsChanged;
        Update.ErrorsChanged += OnChildErrorsChanged;
        Explorer.ErrorsChanged += OnChildErrorsChanged;
        Terminal.ErrorsChanged += OnChildErrorsChanged;
        Step.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(StepSettingsViewModel.HasAnyErrors))
                OnPropertyChanged(nameof(HasAnyErrors));
        };
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

    [ObservableProperty] private bool _useDefaultIdentityFiles = true;

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
        _useDefaultIdentityFiles = settings.UseDefaultIdentityFiles;
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
        target.UseDefaultIdentityFiles = UseDefaultIdentityFiles;
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
    [RegularExpression("^(Name|Size|Date|Type|Permissions)$", ErrorMessage = "Sort by must be Name, Size, Date, Type, or Permissions.")]
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

public partial class StepSettingsViewModel : ObservableValidator
{
    private readonly IStepCertificateService? _certificates;

    [CustomValidation(typeof(SettingsValidators), nameof(SettingsValidators.ValidateStepExecutablePath))]
    [ObservableProperty] private string _executablePath = "";

    [ObservableProperty] private string _executableStatus = "";

    [NotifyCanExecuteChangedFor(nameof(RemoveProfileCommand))]
    [ObservableProperty] private StepProfileViewModel? _selectedProfile;

    public ObservableCollection<StepProfileViewModel> Profiles { get; } = [];

    public bool HasAnyErrors => HasErrors || Profiles.Any(p => p.HasErrors);

    public StepSettingsViewModel()
        : this(new StepSettings(), null)
    {
    }

    public StepSettingsViewModel(StepSettings settings, IStepCertificateService? certificates)
    {
        _certificates = certificates;
        _executablePath = settings.ExecutablePath;
        foreach (var profile in settings.Profiles)
            AddProfileViewModel(new StepProfileViewModel(profile, certificates));
        _selectedProfile = Profiles.FirstOrDefault();

        ErrorsChanged += (_, _) => OnPropertyChanged(nameof(HasAnyErrors));
        ValidateAllProperties();
        UpdateExecutableStatus();
    }

    /// <summary>Writes the settings back and returns the ids of removed profiles.</summary>
    public IReadOnlyList<string> ApplyTo(StepSettings target)
    {
        var removed = target.Profiles.Select(p => p.Id).Except(Profiles.Select(p => p.Id)).ToList();
        target.ExecutablePath = ExecutablePath.Trim();
        // Replaced instead of changed: connections read the list on background threads.
        target.Profiles = [.. Profiles.Select(p => p.ToModel())];
        return removed;
    }

    [RelayCommand]
    private void AddProfile()
    {
        var profile = new StepProfileViewModel(new StepCaProfile { Name = "step-ca" }, _certificates);
        AddProfileViewModel(profile);
        SelectedProfile = profile;
    }

    [RelayCommand(CanExecute = nameof(CanRemoveProfile))]
    private void RemoveProfile()
    {
        if (SelectedProfile is not { } profile) return;

        profile.ErrorsChanged -= OnProfileErrorsChanged;
        Profiles.Remove(profile);
        SelectedProfile = Profiles.FirstOrDefault();
        OnPropertyChanged(nameof(HasAnyErrors));
    }

    private bool CanRemoveProfile() => SelectedProfile != null;

    private void AddProfileViewModel(StepProfileViewModel profile)
    {
        profile.ErrorsChanged += OnProfileErrorsChanged;
        Profiles.Add(profile);
        OnPropertyChanged(nameof(HasAnyErrors));
    }

    private void OnProfileErrorsChanged(object? sender, DataErrorsChangedEventArgs e) =>
        OnPropertyChanged(nameof(HasAnyErrors));

    partial void OnExecutablePathChanged(string value)
    {
        ValidateProperty(value, nameof(ExecutablePath));
        UpdateExecutableStatus();
    }

    private void UpdateExecutableStatus()
    {
        if (_certificates == null || HasErrors)
        {
            ExecutableStatus = "";
            return;
        }

        ExecutableStatus = _certificates.FindExecutable(ExecutablePath) is { } found
            ? $"Using {found}"
            : "The step CLI was not found in PATH or the usual install folders, or other users could change it. Install it or enter its path.";
    }
}

public partial class StepProfileViewModel : ObservableValidator
{
    private readonly IStepCertificateService? _certificates;

    public string Id { get; }

    [CustomValidation(typeof(SettingsValidators), nameof(SettingsValidators.ValidateStepName))]
    [ObservableProperty] private string _name = "";

    [CustomValidation(typeof(SettingsValidators), nameof(SettingsValidators.ValidateStepIdentity))]
    [ObservableProperty] private string _identity = "";

    [CustomValidation(typeof(SettingsValidators), nameof(SettingsValidators.ValidateStepCaUrl))]
    [ObservableProperty] private string _caUrl = "";

    [CustomValidation(typeof(SettingsValidators), nameof(SettingsValidators.ValidateStepRootCertificate))]
    [ObservableProperty] private string _rootCertificatePath = "";

    [CustomValidation(typeof(SettingsValidators), nameof(SettingsValidators.ValidateStepContext))]
    [ObservableProperty] private string _context = "";

    [CustomValidation(typeof(SettingsValidators), nameof(SettingsValidators.ValidateStepProvisioner))]
    [ObservableProperty] private string _provisioner = "";

    [CustomValidation(typeof(SettingsValidators), nameof(SettingsValidators.ValidateStepPrincipals))]
    [ObservableProperty] private string _principals = "";

    [NotifyCanExecuteChangedFor(nameof(DeleteCertificateCommand))]
    [ObservableProperty] private bool _hasCertificate;

    [ObservableProperty] private string _certificateStatus = "";

    public StepProfileViewModel()
        : this(new StepCaProfile(), null)
    {
    }

    public StepProfileViewModel(StepCaProfile profile, IStepCertificateService? certificates)
    {
        _certificates = certificates;
        // A broken id (hand-edited settings) gets a new one; the old certificate is then unused.
        Id = StepProfileValidation.TryParseId(profile.Id, out _) ? profile.Id : Guid.NewGuid().ToString();
        _name = profile.Name;
        _identity = profile.Identity;
        _caUrl = profile.CaUrl;
        _rootCertificatePath = profile.RootCertificatePath;
        _context = profile.Context;
        _provisioner = profile.Provisioner;
        _principals = profile.Principals;
        ValidateAllProperties();
        UpdateCertificateStatus();
    }

    public StepCaProfile ToModel() => new()
    {
        Id = Id,
        Name = Name.Trim(),
        Identity = Identity.Trim(),
        CaUrl = CaUrl.Trim(),
        RootCertificatePath = RootCertificatePath.Trim(),
        Context = Context.Trim(),
        Provisioner = Provisioner.Trim(),
        Principals = string.Join(", ", StepProfileValidation.SplitPrincipals(Principals))
    };

    /// <summary>Deletes the stored key pair right away (a new certificate is requested on the next connect).</summary>
    [RelayCommand(CanExecute = nameof(HasCertificate))]
    private void DeleteCertificate()
    {
        _certificates?.Forget(Id);
        UpdateCertificateStatus();
    }

    partial void OnNameChanged(string value) => ValidateProperty(value, nameof(Name));

    // The stored certificate belongs to these values; changing them shows it as no longer usable.
    partial void OnIdentityChanged(string value) => OnCertificateSettingChanged(value, nameof(Identity));
    partial void OnCaUrlChanged(string value) => OnCertificateSettingChanged(value, nameof(CaUrl));
    partial void OnRootCertificatePathChanged(string value) => OnCertificateSettingChanged(value, nameof(RootCertificatePath));
    partial void OnContextChanged(string value) => OnCertificateSettingChanged(value, nameof(Context));
    partial void OnProvisionerChanged(string value) => OnCertificateSettingChanged(value, nameof(Provisioner));
    partial void OnPrincipalsChanged(string value) => OnCertificateSettingChanged(value, nameof(Principals));

    private void OnCertificateSettingChanged(string value, string propertyName)
    {
        ValidateProperty(value, propertyName);
        UpdateCertificateStatus();
    }

    private void UpdateCertificateStatus()
    {
        var validUntil = _certificates?.GetValidUntil(ToModel());
        HasCertificate = validUntil != null;
        CertificateStatus = validUntil is { } until
            ? $"Certificate valid until {until.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}"
            : "No valid certificate. It is requested when a session using this profile connects.";
    }
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

    public static ValidationResult? ValidateStepExecutablePath(string? value, ValidationContext context) =>
        ToResult(StepProfileValidation.ValidateExecutablePath(value));

    public static ValidationResult? ValidateStepName(string? value, ValidationContext context) =>
        ToResult(StepProfileValidation.ValidateName(value));

    public static ValidationResult? ValidateStepIdentity(string? value, ValidationContext context) =>
        ToResult(StepProfileValidation.ValidateIdentity(value));

    public static ValidationResult? ValidateStepCaUrl(string? value, ValidationContext context) =>
        ToResult(StepProfileValidation.ValidateCaUrl(value));

    public static ValidationResult? ValidateStepRootCertificate(string? value, ValidationContext context) =>
        ToResult(StepProfileValidation.ValidateRootCertificatePath(value));

    public static ValidationResult? ValidateStepContext(string? value, ValidationContext context) =>
        ToResult(StepProfileValidation.ValidateContext(value));

    public static ValidationResult? ValidateStepProvisioner(string? value, ValidationContext context) =>
        ToResult(StepProfileValidation.ValidateProvisioner(value));

    public static ValidationResult? ValidateStepPrincipals(string? value, ValidationContext context) =>
        ToResult(StepProfileValidation.ValidatePrincipals(value));

    private static ValidationResult? ToResult(string? error) =>
        error == null ? ValidationResult.Success : new ValidationResult(error);
}
