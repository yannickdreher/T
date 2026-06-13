using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using FluentAvalonia.UI.Controls;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Threading.Tasks;
using T.Abstractions;
using T.Models;
using T.UI.Abstractions;
using T.UI.Views.Dialogs;
using T.UI.Views;

namespace T.UI.ViewModels;

public partial class MainWindowViewModel(
    SessionsTreeViewModel sessionsTree,
    ISettingsService settingsService,
    IWindowProvider windowProvider,
    IServiceProvider serviceProvider) : ViewModelBase
{
    private readonly SessionsTreeViewModel _sessionsTree = sessionsTree;
    private readonly ISettingsService _settingsService = settingsService;
    private readonly IWindowProvider _windowProvider = windowProvider;
    private readonly IServiceProvider _serviceProvider = serviceProvider;

    private Window? Host => _windowProvider.MainWindow;

    public AppSettings Settings => _settingsService.Current;
    public SessionsTreeViewModel SessionsTree => _sessionsTree;

    [RelayCommand]
    private async Task ShowSettingsAsync()
    {
        if (Host is null) return;

        var vm = _serviceProvider.GetRequiredService<SettingsDialogViewModel>();
        var window = new SettingsWindow { DataContext = vm };
        var result = await window.ShowDialog<bool>(Host);

        if (result)
        {
            vm.ApplyTo();
            await _settingsService.SaveAsync();
        }
    }

    [RelayCommand]
    private async Task ShowPermissionDialogAsync()
    {
        if (Host is null || _sessionsTree.ActiveSession?.SelectedFile is null) return;

        var vm = PermissionDialogViewModel.FromOctal(_sessionsTree.ActiveSession.SelectedFile.Permissions);
        var dialogContent = _serviceProvider.GetRequiredService<PermissionDialog>();
        dialogContent.DataContext = vm;

        var dialog = new FAContentDialog
        {
            Title = "Permissions", PrimaryButtonText = "Apply", CloseButtonText = "Cancel",
            DefaultButton = FAContentDialogButton.Primary,
            Content = dialogContent
        };

        if (await dialog.ShowAsync(Host) == FAContentDialogResult.Primary)
            await _sessionsTree.ActiveSession.ChangePermissionsAsync(vm.ToOctal());
    }

    [RelayCommand]
    private async Task ShowAboutAsync()
    {
        if (Host is null) return;

        var dialog = new FAContentDialog
        {
            Title = "About", CloseButtonText = "Close",
            DefaultButton = FAContentDialogButton.Close,
            Content = _serviceProvider.GetRequiredService<AboutDialog>()
        };

        await dialog.ShowAsync(Host);
    }
}
