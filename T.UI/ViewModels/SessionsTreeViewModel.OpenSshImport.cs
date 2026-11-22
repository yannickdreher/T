using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using FluentAvalonia.UI.Controls;
using T.Models;
using T.Services;
using T.UI.Models;
using T.UI.Views.Dialogs;

namespace T.UI.ViewModels;

public partial class SessionsTreeViewModel
{
    private const int MaxSshConfigCharacters = 512 * 1024;

    [RelayCommand]
    private async Task ImportOpenSshConfigAsync()
    {
        if (Host is not { } owner) return;

        try
        {
            await _loadTask;
            var storage = owner.StorageProvider;
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var configFolder = Path.Combine(home, ".ssh");
            bool defaultConfigExists = File.Exists(Path.Combine(configFolder, "config"));
            using var start = defaultConfigExists ? await storage.TryGetFolderFromPathAsync(configFolder) : null;
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open OpenSSH configuration",
                SuggestedStartLocation = start,
                SuggestedFileName = defaultConfigExists ? "config" : null,
                AllowMultiple = false
            });
            if (files.Count == 0) return;

            string snapshot;
            using (var file = files[0])
                snapshot = await ReadSshConfigSnapshotAsync(file);

            var entries = await Task.Run(() => OpenSshConfigImport.Parse(snapshot, home, Environment.UserName));
            if (entries.Count == 0)
            {
                await ShowErrorAsync("No sessions to import", "The configuration contains no concrete Host aliases.");
                return;
            }

            var initialFolderId = SelectedTreeNode switch
            {
                FolderTreeNode node => node.Folder.Id,
                SessionTreeNode node => node.Session.FolderId,
                _ => null
            };
            var viewModel = new OpenSshImportDialogViewModel(entries, Folders.ToArray(), Sessions.ToArray(), initialFolderId);
            var content = new OpenSshImportDialog { DataContext = viewModel };
            var dialog = new FAContentDialog
            {
                Title = "Import OpenSSH configuration",
                PrimaryButtonText = "Import selected",
                CloseButtonText = "Cancel",
                DefaultButton = FAContentDialogButton.Close,
                Content = content
            };

            IReadOnlyList<SshImportProfile> profiles = [];
            dialog.PrimaryButtonClick += (_, args) =>
            {
                var error = viewModel.TryBuildImport(out profiles);
                content.ValidationError = error;
                args.Cancel = error != null;
            };
            if (await dialog.ShowAsync(owner) != FAContentDialogResult.Primary)
                return;

            var inserted = await _storageService.ImportSessionsAsync(profiles, viewModel.Destination?.Id);
            foreach (var session in inserted)
                Sessions.Add(session);
            BuildTree();
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Importing OpenSSH configuration failed", ex.Message);
        }
    }

    private static async Task<string> ReadSshConfigSnapshotAsync(IStorageFile file)
    {
        await using var stream = await file.OpenReadAsync();
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        var snapshot = new StringBuilder();
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer, 0, buffer.Length) is var count && count > 0)
        {
            if (snapshot.Length + count > MaxSshConfigCharacters)
                throw new FormatException("The SSH configuration exceeds 512 KiB of text.");
            snapshot.Append(buffer, 0, count);
        }

        return snapshot.ToString();
    }
}
