using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using FluentAvalonia.UI.Controls;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Linq;
using System.Threading.Tasks;
using T.Abstractions;
using T.UI.Abstractions;
using T.UI.Extensions;
using T.UI.ViewModels;
using T.UI.Views;
using T.UI.Views.Dialogs;
using Velopack;

namespace T.UI;

public partial class App : Application
{
    private IServiceProvider? _serviceProvider;
    private ISettingsService? _settingsService;
    private IUpdateService? _updateService;

    public IServiceProvider Services =>
        _serviceProvider ?? throw new InvalidOperationException("Service provider is not initialized.");

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            DisableAvaloniaDataAnnotationValidation();

            var collection = new ServiceCollection();
            collection.AddCommonServices();

            var services = collection.BuildServiceProvider();
            _serviceProvider = services;

            _settingsService = services.GetRequiredService<ISettingsService>();
            _updateService = services.GetRequiredService<IUpdateService>();

            var mainWindow = services.GetRequiredService<MainWindow>();
            desktop.MainWindow = mainWindow;

            services.GetRequiredService<IWindowProvider>().MainWindow = mainWindow;

            if (_settingsService.Current.Update.CheckForUpdatesOnStartup)
                mainWindow.Opened += async (_, _) => await CheckForUpdatesAsync(mainWindow);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task CheckForUpdatesAsync(Window mainWindow)
    {
        if (_updateService is null || _serviceProvider is null) return;

        var updateInfo = await _updateService.CheckForUpdatesAsync();

        if (updateInfo != null)
            await ShowUpdateDialogAsync(mainWindow, updateInfo);
    }

    private async Task ShowUpdateDialogAsync(Window owner, UpdateInfo updateInfo)
    {
        if (_serviceProvider is null) return;

        var vm = ActivatorUtilities.CreateInstance<UpdateDialogViewModel>(_serviceProvider, updateInfo);
        var view = _serviceProvider.GetRequiredService<UpdateDialog>();
        view.DataContext = vm;

        var dialog = new ContentDialog
        {
            Title = "Update Available",
            PrimaryButtonText = "Install",
            CloseButtonText = "Later",
            DefaultButton = ContentDialogButton.Primary,
            Content = view
        };

        if (await dialog.ShowAsync(owner) == ContentDialogResult.Primary)
        {
            var progressDialog = new ContentDialog
            {
                Title = "Installing Update",
                Content = "Downloading and installing update...\nThe application will restart automatically.",
                IsPrimaryButtonEnabled = false,
                IsSecondaryButtonEnabled = false
            };

            _ = progressDialog.ShowAsync(owner);
            await vm.InstallAsync();
        }
    }

    private static void DisableAvaloniaDataAnnotationValidation()
    {
        var toRemove = BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();
        foreach (var plugin in toRemove)
            BindingPlugins.DataValidators.Remove(plugin);
    }
}