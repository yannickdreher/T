using Microsoft.Extensions.DependencyInjection;
using T.Abstractions;
using T.Services;
using T.UI.Abstractions;
using T.UI.Services;
using T.UI.ViewModels;
using T.UI.Views;
using T.UI.Views.Components;
using T.UI.Views.Dialogs;

namespace T.UI.Extensions;

public static class ServiceCollectionExtensions
{
    public static void AddCommonServices(this IServiceCollection services)
    {
        // ── Services ──
        services.AddSingleton<IEncryptionService, EncryptionService>();
        services.AddSingleton<IKnownHostsService, KnownHostsService>();
        services.AddSingleton<ISessionStorageService, SqliteSessionStorageService>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IUpdateService, UpdateService>();
        services.AddSingleton<ISshManager, SshManager>();
        services.AddSingleton<IWindowProvider, WindowProvider>();

        services.AddTransient<ISshService, SshService>();

        // ── ViewModels ──
        services.AddSingleton<SessionsTreeViewModel>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddTransient<UpdateDialogViewModel>();
        services.AddTransient<SessionViewModel>();

        // ── Dialog Views ──
        services.AddTransient<AboutDialog>();
        services.AddTransient<CredentialsDialog>();
        services.AddTransient<FolderEditorDialog>();
        services.AddTransient<HostKeyDialog>();
        services.AddTransient<PermissionDialog>();
        services.AddTransient<SessionEditorDialog>();
        services.AddTransient<UpdateDialog>();

        // ── Views ──
        services.AddSingleton<MainWindow>();
        services.AddTransient<SessionView>();
        services.AddTransient<TerminalView>();
    }
}