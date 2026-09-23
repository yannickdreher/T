using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using FluentAvalonia.UI.Controls;
using Microsoft.Extensions.DependencyInjection;
using T.Abstractions;
using T.Models;
using T.UI.Abstractions;
using T.UI.Views;
using T.UI.Views.Dialogs;

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
        var session = _sessionsTree.ActiveSession;
        if (Host is null || session is null || session.SelectedFiles.Count == 0) return;

        // Several items: the dialog starts with the permissions of the first one and applies to all.
        var selected = session.SelectedFiles;
        var vm = PermissionDialogViewModel.FromOctal(selected[0].Permissions);
        var dialogContent = _serviceProvider.GetRequiredService<PermissionDialog>();
        dialogContent.DataContext = vm;

        var dialog = new FAContentDialog
        {
            Title = selected.Count == 1 ? $"Permissions - {selected[0].Name}" : $"Permissions - {selected.Count} items",
            PrimaryButtonText = "Apply",
            CloseButtonText = "Cancel",
            DefaultButton = FAContentDialogButton.Primary,
            Content = dialogContent
        };

        if (await dialog.ShowAsync(Host) == FAContentDialogResult.Primary)
            await session.ChangePermissionsAsync(vm.ToOctal());
    }

    [RelayCommand]
    private async Task ShowAboutAsync()
    {
        if (Host is null) return;

        var dialog = new FAContentDialog
        {
            Title = "About",
            CloseButtonText = "Close",
            DefaultButton = FAContentDialogButton.Close,
            Content = _serviceProvider.GetRequiredService<AboutDialog>()
        };

        await dialog.ShowAsync(Host);
    }
}
