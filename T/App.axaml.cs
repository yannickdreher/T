using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using T.Helpers;
using T.Services;
using T.ViewModels;
using T.Views;

namespace T
{
    public partial class App : Application
    {
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                DisableAvaloniaDataAnnotationValidation();
                
                SettingsService.Load();
                
                desktop.MainWindow = new MainWindow
                {
                    DataContext = new MainWindowViewModel(),
                };

                if (SettingsService.Current.CheckForUpdatesOnStartup)
                {
                    desktop.MainWindow.Opened += async (s, e) => await CheckForUpdatesAsync(desktop.MainWindow);
                }
            }

            base.OnFrameworkInitializationCompleted();
        }

        private static async Task CheckForUpdatesAsync(Window mainWindow)
        {
            var updateInfo = await UpdateService.CheckForUpdatesAsync(SettingsService.Current.UpdateChannel);

            if (updateInfo != null)
            {
                await UpdateDialogHelper.ShowUpdateDialogAsync(mainWindow, updateInfo, SettingsService.Current.UpdateChannel);
            }
        }

        private static void DisableAvaloniaDataAnnotationValidation()
        {
            var dataValidationPluginsToRemove = BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

            foreach (var plugin in dataValidationPluginsToRemove)
            {
                BindingPlugins.DataValidators.Remove(plugin);
            }
        }
    }
}