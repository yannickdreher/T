using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using FluentAvalonia.UI.Controls;
using T.Models;
using T.UI.Services;

namespace T.UI.ViewModels;

/// <summary>
/// Explorer operations on the selected files: download, delete, cut / copy / paste,
/// move and copy (drag and drop, "Move to"), permissions. All of them work on any number
/// of files and folders; failures of single items are collected instead of aborting the rest.
/// </summary>
public partial class SessionViewModel
{
    private sealed record ClipboardItem(string Path, string Name, bool IsDirectory);

    private enum Conflict { None, Replace, Merge }

    private List<RemoteFile> _selectedFiles = [];
    private List<ClipboardItem> _clipboard = [];
    private bool _clipboardIsCut;

    // Names to select after the next listing (pasted, uploaded, renamed items).
    private HashSet<string>? _selectAfterRefresh;
    // Directory of the listing that is shown: the selection is kept while it stays the same.
    private string? _publishedPath;

    public IReadOnlyList<RemoteFile> SelectedFiles => _selectedFiles;
    public bool HasSelection => _selectedFiles.Count > 0;
    public bool HasSingleSelection => _selectedFiles.Count == 1;
    public bool CanPaste => _clipboard.Count > 0 && IsConnected;

    /// <summary>Asks the view to select these items (after a refresh, paste, upload or rename).</summary>
    public event Action<IReadOnlyList<RemoteFile>>? SelectionRestoreRequested;

    /// <summary>Called by the view whenever the selection of the file list changes.</summary>
    public void SetSelection(IEnumerable<RemoteFile> files)
    {
        _selectedFiles = files.Where(f => !f.IsParentLink).Distinct().ToList();
        OnPropertyChanged(nameof(SelectedFiles));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasSingleSelection));

        int count = _selectedFiles.Count;
        long size = _selectedFiles.Where(f => !f.IsDirectory).Sum(f => f.Size);
        bool anyFile = _selectedFiles.Any(f => !f.IsDirectory);
        SelectionSummary = count == 0 ? ""
            : $"{Describe(count)} selected" + (anyFile ? $"    {RemoteFile.FormatSize(size)}" : "");

        DownloadCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        CutCommand.NotifyCanExecuteChanged();
        CopyCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsConnectedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanPaste));
        PasteCommand.NotifyCanExecuteChanged();
    }

    private static string Describe(int count) => count == 1 ? "1 item" : $"{count} items";

    /// <summary>"Deleted 3 items" or "Deleted 2 of 3 items - failed: x: permission denied (+1 more)".</summary>
    private static string Summarize(string verb, int total, List<string> failures, string? single = null)
    {
        if (failures.Count == 0)
            return total == 1 && single != null ? $"{verb}: {single}" : $"{verb} {Describe(total)}";
        var more = failures.Count > 1 ? $" (+{failures.Count - 1} more)" : "";
        return $"{verb} {total - failures.Count} of {Describe(total)} - failed: {failures[0]}{more}";
    }

    // ── Download ─────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task DownloadAsync() => DownloadToAsync(null);

    /// <summary>Downloads the selected files and folders (recursively) into <paramref name="localFolder"/> (default: the download folder).</summary>
    public async Task DownloadToAsync(string? localFolder)
    {
        var items = _selectedFiles.ToList();
        if (items.Count == 0 || _sshService is null || !SftpReady) return;

        string folder;
        try
        {
            folder = localFolder ?? GetDownloadFolder();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Download failed: {ex.Message}";
            return;
        }

        var failures = new List<string>();
        var token = BeginTransfer(items.Count == 1 ? $"Downloading: {items[0].Name}" : $"Downloading {Describe(items.Count)}...");
        try
        {
            foreach (var item in items)
            {
                try { await DownloadRecursiveAsync(item, folder, token); }
                catch (Exception ex) when (ex is not OperationCanceledException) { failures.Add($"{item.Name}: {ex.Message}"); }
            }
            StatusMessage = Summarize("Downloaded", items.Count, failures, items[0].Name) + $" to {folder}";
        }
        catch (OperationCanceledException) { StatusMessage = "Download canceled"; }
        finally { EndTransfer(); }
    }

    private async Task DownloadRecursiveAsync(RemoteFile item, string localFolder, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        bool isDirectory = item.IsDirectory && !item.IsSymbolicLink;
        var localPath = GetUniqueLocalPath(localFolder, SanitizeFileName(item.Name), isDirectory);

        if (isDirectory)
        {
            Directory.CreateDirectory(localPath);
            foreach (var child in await _sshService!.ListDirectoryAsync(item.FullPath, token))
            {
                if (child.Name is not ("." or ".."))
                    await DownloadRecursiveAsync(child, localPath, token);
            }
            return;
        }

        StatusMessage = $"Downloading: {item.Name}";
        await _sshService!.DownloadFileAsync(item.FullPath, localPath, token);
    }

    // ── Delete ───────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteAsync()
    {
        var items = _selectedFiles.ToList();
        if (items.Count == 0 || _sshService is null || !SftpReady) return;

        if (ExplorerSettings.ConfirmDelete && Host is not null)
        {
            var message = items.Count == 1
                ? items[0].IsDirectory ? $"Delete the folder \"{items[0].Name}\" and everything in it?" : $"Delete \"{items[0].Name}\"?"
                : items.Any(f => f.IsDirectory)
                    ? $"Delete these {items.Count} items? Folders are deleted with everything in them."
                    : $"Delete these {items.Count} items?";
            if (!await DialogService.ConfirmAsync(Host, "Delete", message))
                return;
        }

        var failures = new List<string>();
        foreach (var item in items)
        {
            StatusMessage = $"Deleting: {item.Name}...";
            try { await _sshService.DeleteAsync(item.FullPath, item.IsDirectory && !item.IsSymbolicLink); }
            catch (Exception ex) { failures.Add($"{item.Name}: {ex.Message}"); }
        }

        await RefreshDirectoryAsync();
        if (items.Any(f => f.IsDirectory))
            await RefreshTreeAsync();
        StatusMessage = Summarize("Deleted", items.Count, failures, items[0].Name);
    }

    // ── Permissions ──────────────────────────────────────────────────────

    /// <summary>Applies <paramref name="permissions"/> (e.g. 755) to all selected items.</summary>
    public async Task ChangePermissionsAsync(short permissions)
    {
        var items = _selectedFiles.ToList();
        if (items.Count == 0 || _sshService is null || !SftpReady) return;

        var failures = new List<string>();
        foreach (var item in items)
        {
            try { await _sshService.ChangePermissionsAsync(item.FullPath, permissions); }
            catch (Exception ex) { failures.Add($"{item.Name}: {ex.Message}"); }
        }

        await RefreshDirectoryAsync();
        StatusMessage = Summarize("Permissions changed", items.Count, failures, items[0].Name);
    }

    // ── Cut / copy / paste ───────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Cut() => SetClipboard(isCut: true);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Copy() => SetClipboard(isCut: false);

    private void SetClipboard(bool isCut)
    {
        _clipboard = _selectedFiles.Select(ToClipboardItem).ToList();
        _clipboardIsCut = isCut;
        MarkCutItems();
        OnPropertyChanged(nameof(CanPaste));
        PasteCommand.NotifyCanExecuteChanged();
        StatusMessage = $"{(isCut ? "Cut" : "Copied")} {Describe(_clipboard.Count)} - paste with Ctrl+V";
    }

    private static ClipboardItem ToClipboardItem(RemoteFile file) =>
        new(file.FullPath, file.Name, file.IsDirectory && !file.IsSymbolicLink);

    /// <summary>Dims the items that are cut (like Windows Explorer).</summary>
    private void MarkCutItems()
    {
        var cut = _clipboardIsCut ? _clipboard.Select(c => c.Path).ToHashSet(StringComparer.Ordinal) : [];
        foreach (var file in RemoteFiles)
            file.IsCut = cut.Contains(file.FullPath);
    }

    [RelayCommand(CanExecute = nameof(CanPaste))]
    private async Task PasteAsync()
    {
        bool move = _clipboardIsCut;
        if (await TransferAsync(_clipboard, CurrentPath, move) && move)
        {
            // Moved items are gone from their old place: the clipboard is used up.
            _clipboard = [];
            _clipboardIsCut = false;
            OnPropertyChanged(nameof(CanPaste));
            PasteCommand.NotifyCanExecuteChanged();
        }
    }

    // ── Move / copy between folders ──────────────────────────────────────

    /// <summary>
    /// Moves (or with <paramref name="copy"/> copies) <paramref name="files"/> into <paramref name="targetDirectory"/>:
    /// drag and drop onto a folder, "Move to…", "Copy to…".
    /// </summary>
    public Task<bool> MoveOrCopyAsync(IReadOnlyList<RemoteFile> files, string targetDirectory, bool copy) =>
        TransferAsync(files.Where(f => !f.IsParentLink).Select(ToClipboardItem).ToList(), targetDirectory, move: !copy);

    /// <summary>True when dropping <paramref name="files"/> onto <paramref name="targetDirectory"/> would do something.</summary>
    public static bool CanTransferInto(IReadOnlyList<RemoteFile> files, string targetDirectory, bool copy)
    {
        var target = RemotePath.Normalize(targetDirectory);
        if (files.Any(f => f.IsDirectory && RemotePath.IsSameOrInside(target, f.FullPath)))
            return false; // a folder cannot go into itself
        return copy || files.Any(f => RemotePath.GetParent(f.FullPath) != target);
    }

    private async Task<bool> TransferAsync(IReadOnlyList<ClipboardItem> items, string targetDirectory, bool move)
    {
        if (_sshService is null || !SftpReady || items.Count == 0) return false;

        var target = RemotePath.Normalize(targetDirectory);
        var verb = move ? "Move" : "Copy";

        if (items.FirstOrDefault(i => i.IsDirectory && RemotePath.IsSameOrInside(target, i.Path)) is { } loop)
        {
            StatusMessage = $"{verb} failed: \"{loop.Name}\" cannot be put into itself.";
            return false;
        }

        List<RemoteFile> listing;
        try { listing = target == CurrentPath ? _lastListing : await _sshService.ListDirectoryAsync(target); }
        catch (Exception ex)
        {
            StatusMessage = $"{verb} failed: {ex.Message}";
            return false;
        }

        var existing = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var entry in listing.Where(f => f.Name is not ("." or "..")))
            existing[entry.Name] = entry.IsDirectory && !entry.IsSymbolicLink;
        var usedNames = new HashSet<string>(existing.Keys, StringComparer.Ordinal);

        var plan = new List<(ClipboardItem Item, string Target, Conflict Conflict, bool ExistingIsDirectory)>();
        var conflicts = new List<ClipboardItem>();
        foreach (var item in items)
        {
            if (RemotePath.GetParent(item.Path) == target)
            {
                if (move) continue; // moving into its own folder changes nothing
                var copyName = RemotePath.UniqueCopyName(item.Name, item.IsDirectory, usedNames);
                usedNames.Add(copyName);
                plan.Add((item, RemotePath.Combine(target, copyName), Conflict.None, false));
            }
            else if (existing.ContainsKey(item.Name))
            {
                conflicts.Add(item);
            }
            else
            {
                usedNames.Add(item.Name);
                plan.Add((item, RemotePath.Combine(target, item.Name), Conflict.None, false));
            }
        }

        if (conflicts.Count > 0 && Host is not null)
        {
            var question = conflicts.Count == 1
                ? $"\"{RemotePath.GetName(target)}\" already contains \"{conflicts[0].Name}\"."
                : $"\"{RemotePath.GetName(target)}\" already contains {conflicts.Count} items with the same names.";
            var answer = await DialogService.ChooseAsync(Host, $"{verb} items",
                question + " Replace files with the same name and merge folders?", "Replace", "Skip");
            if (answer is not (FAContentDialogResult.Primary or FAContentDialogResult.Secondary))
                return false;

            if (answer == FAContentDialogResult.Primary)
            {
                foreach (var item in conflicts)
                {
                    bool existingIsDirectory = existing[item.Name];
                    var conflict = item.IsDirectory && existingIsDirectory ? Conflict.Merge : Conflict.Replace;
                    plan.Add((item, RemotePath.Combine(target, item.Name), conflict, existingIsDirectory));
                }
            }
        }

        if (plan.Count == 0)
        {
            StatusMessage = $"Nothing to {verb.ToLowerInvariant()}";
            return true;
        }

        var failures = new List<string>();
        var progressVerb = move ? "Moving" : "Copying";
        var token = BeginTransfer($"{progressVerb} {Describe(plan.Count)}...");
        try
        {
            foreach (var (item, destination, conflict, existingIsDirectory) in plan)
            {
                token.ThrowIfCancellationRequested();
                StatusMessage = $"{progressVerb}: {item.Name}";
                try
                {
                    switch (conflict)
                    {
                        case Conflict.Merge when move:
                            await MergeMoveAsync(item.Path, destination, token);
                            break;
                        case Conflict.Merge:
                            // "dir/." copies the contents into the existing folder, overwriting files.
                            await _sshService.CopyAsync(item.Path + "/.", destination, token);
                            break;
                        default:
                            if (conflict == Conflict.Replace)
                                await _sshService.DeleteAsync(destination, existingIsDirectory, token);
                            if (move) await _sshService.RenameAsync(item.Path, destination, token);
                            else await _sshService.CopyAsync(item.Path, destination, token);
                            break;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failures.Add($"{item.Name}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            failures.Add("canceled");
        }
        finally
        {
            EndTransfer();
        }

        if (target == CurrentPath)
            _selectAfterRefresh = plan.Select(p => RemotePath.GetName(p.Target)).ToHashSet(StringComparer.Ordinal);
        await RefreshDirectoryAsync();
        if (plan.Any(p => p.Item.IsDirectory))
            await RefreshTreeAsync();

        StatusMessage = Summarize(move ? "Moved" : "Copied", plan.Count, failures, plan[0].Item.Name);
        return failures.Count == 0;
    }

    /// <summary>Moves the contents of <paramref name="source"/> into the existing folder <paramref name="target"/>, then removes the empty source.</summary>
    private async Task MergeMoveAsync(string source, string target, CancellationToken token)
    {
        var existing = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var entry in await _sshService!.ListDirectoryAsync(target, token))
        {
            if (entry.Name is not ("." or ".."))
                existing[entry.Name] = entry.IsDirectory && !entry.IsSymbolicLink;
        }

        foreach (var child in await _sshService.ListDirectoryAsync(source, token))
        {
            if (child.Name is "." or "..") continue;
            token.ThrowIfCancellationRequested();

            var destination = RemotePath.Combine(target, child.Name);
            bool childIsDirectory = child.IsDirectory && !child.IsSymbolicLink;
            if (existing.TryGetValue(child.Name, out var existingIsDirectory))
            {
                if (childIsDirectory && existingIsDirectory)
                {
                    await MergeMoveAsync(child.FullPath, destination, token);
                    continue;
                }
                await _sshService.DeleteAsync(destination, existingIsDirectory, token);
            }
            await _sshService.RenameAsync(child.FullPath, destination, token);
        }

        // Only reached when everything was moved: the source folder is empty now.
        await _sshService.DeleteAsync(source, isDirectory: true, token);
    }
}
