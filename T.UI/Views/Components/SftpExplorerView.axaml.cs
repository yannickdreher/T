using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using T.Models;
using T.UI.Models;
using T.UI.Services;
using T.UI.ViewModels;

namespace T.UI.Views.Components;

public partial class SftpExplorerView : UserControl
{
    private const double DragThreshold = 6;

    private SessionViewModel? _subscribedVm;
    private bool _syncingSelection;

    // Pointer state of the file lists (drag start, click inside a multi-selection).
    private RemoteFile? _pressedFile;
    private PointerPressedEventArgs? _pressArgs;
    private Point _pressPoint;
    private RemoteFile? _selectOnlyOnRelease;
    private bool _dragging;

    // Files dragged inside the explorer (null while an external drag - files from Windows Explorer - is over it).
    private List<RemoteFile>? _draggedFiles;
    private Control? _dropHighlight;

    public SftpExplorerView()
    {
        InitializeComponent();

        Breadcrumb.ItemClicked += OnBreadcrumbItemClicked;
        AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(KeyDownEvent, OnExplorerKeyDown, RoutingStrategies.Tunnel);
        FilePane.AddHandler(ContextRequestedEvent, OnFileContextRequested);

        foreach (var list in FileLists)
        {
            list.AddHandler(PointerPressedEvent, OnListPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            list.AddHandler(PointerMovedEvent, OnListPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
            list.AddHandler(PointerReleasedEvent, OnListPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        }
        DetailsGrid.SelectionChanged += OnFileSelectionChanged;
        ListView.SelectionChanged += OnFileSelectionChanged;
        IconsView.SelectionChanged += OnFileSelectionChanged;
    }

    private SessionViewModel? Vm => DataContext as SessionViewModel;
    private Window? HostWindow => TopLevel.GetTopLevel(this) as Window;

    /// <summary>The three views of the same listing (details, list, large icons).</summary>
    private Control[] FileLists => [DetailsGrid, ListView, IconsView];

    private Control ActiveList => Vm?.ExplorerViewMode switch
    {
        1 => ListView,
        2 => IconsView,
        _ => DetailsGrid
    };

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_subscribedVm != null)
            _subscribedVm.SelectionRestoreRequested -= ApplySelection;
        _subscribedVm = Vm;
        if (_subscribedVm != null)
            _subscribedVm.SelectionRestoreRequested += ApplySelection;
    }

    // ── Selection (kept in sync across the three views) ──────────────────

    private static IList? SelectedItemsOf(Control list) => list switch
    {
        DataGrid grid => grid.SelectedItems,
        ListBox box => box.SelectedItems,
        _ => null
    };

    private void OnFileSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection || Vm is not { } vm || sender is not Control list || SelectedItemsOf(list) is not { } items)
            return;

        // Only the visible view drives the selection. The hidden ones also report changes while a
        // new listing is being bound to them one after the other - mirroring those would push items
        // of the old listing into views that already show the new one.
        if (!ReferenceEquals(list, ActiveList))
            return;

        var selection = items.OfType<RemoteFile>().ToList();
        vm.SelectedFile = list switch
        {
            DataGrid grid => grid.SelectedItem as RemoteFile,
            ListBox box => box.SelectedItem as RemoteFile,
            _ => null
        };
        vm.SetSelection(selection);

        // Mirror into the hidden views so switching the view keeps the selection.
        ApplySelection(selection, except: list);
    }

    private void ApplySelection(IReadOnlyList<RemoteFile> files) => ApplySelection(files, except: null);

    private void ApplySelection(IReadOnlyList<RemoteFile> files, Control? except)
    {
        _syncingSelection = true;
        try
        {
            foreach (var list in FileLists)
            {
                if (list == except || SelectedItemsOf(list) is not { } items) continue;
                var source = (list as DataGrid)?.ItemsSource ?? (list as ItemsControl)?.ItemsSource;
                items.Clear();
                foreach (var file in files)
                {
                    // Selecting an item the view does not contain throws (and breaks its ItemsSource binding).
                    if (source is IList listSource && !listSource.Contains(file)) continue;
                    items.Add(file);
                }
            }
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    private void SelectOnly(RemoteFile file)
    {
        ApplySelection([file]);
        Vm?.SetSelection([file]);
    }

    private void OnSelectAllClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var all = vm.RemoteFiles.Where(f => !f.IsParentLink).ToList();
        ApplySelection(all);
        vm.SetSelection(all);
    }

    private static RemoteFile? FileAt(object? source)
    {
        var visual = source as Visual;
        var container = (Control?)visual?.FindAncestorOfType<DataGridRow>(includeSelf: true)
                        ?? visual?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
        return container?.DataContext as RemoteFile;
    }

    // ── Pointer: keep a multi-selection when it is dragged, start drags ──

    private void OnListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _pressedFile = null;
        _selectOnlyOnRelease = null;
        if (Vm is not { } vm || sender is not Control list) return;

        var point = e.GetCurrentPoint(list);
        var file = FileAt(e.Source);

        if (point.Properties.IsRightButtonPressed)
        {
            // The context menu acts on the selection: right-clicking another item selects it.
            if (file is { IsParentLink: false } && !vm.SelectedFiles.Contains(file))
                SelectOnly(file);
            return;
        }

        if (!point.Properties.IsLeftButtonPressed || file is null || file.IsParentLink)
            return;

        _pressedFile = file;
        _pressArgs = e;
        _pressPoint = point.Position;

        // A plain click into a multi-selection must not drop the other items yet - it may start a drag.
        // Without a drag the release selects only this item (like Windows Explorer).
        if (e.KeyModifiers == KeyModifiers.None && e.ClickCount == 1 &&
            vm.SelectedFiles.Count > 1 && vm.SelectedFiles.Contains(file))
        {
            _selectOnlyOnRelease = file;
            e.Handled = true;
        }
    }

    private async void OnListPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressedFile is null || _pressArgs is null || _dragging || sender is not Control list || Vm is not { } vm)
            return;

        var point = e.GetCurrentPoint(list);
        if (!point.Properties.IsLeftButtonPressed)
        {
            _pressedFile = null;
            return;
        }

        var delta = point.Position - _pressPoint;
        if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold)
            return;

        _dragging = true;
        _selectOnlyOnRelease = null;
        _draggedFiles = vm.SelectedFiles.Contains(_pressedFile) ? vm.SelectedFiles.ToList() : [_pressedFile];
        try
        {
            await DragDrop.DoDragDropAsync(_pressArgs, new DataTransfer(), DragDropEffects.Move | DragDropEffects.Copy);
        }
        finally
        {
            _draggedFiles = null;
            _dragging = false;
            _pressedFile = null;
            HideDropFeedback();
        }
    }

    private void OnListPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging && _selectOnlyOnRelease is { } file)
            SelectOnly(file);
        _selectOnlyOnRelease = null;
        _pressedFile = null;
    }

    // ── Keyboard (Windows Explorer shortcuts) ────────────────────────────

    private void OnExplorerKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;
        bool alt = e.KeyModifiers == KeyModifiers.Alt;
        bool ctrl = e.KeyModifiers == KeyModifiers.Control;
        bool none = e.KeyModifiers == KeyModifiers.None;
        bool inTextBox = e.Source is TextBox; // Ctrl+C/V/X/A and Delete belong to the text there

        switch (e.Key)
        {
            case Key.Left when alt:
                Execute(vm.NavigateBackCommand);
                break;
            case Key.Right when alt:
                Execute(vm.NavigateForwardCommand);
                break;
            case Key.Up when alt:
                Execute(vm.NavigateUpCommand);
                break;
            case Key.F5 when none:
                Execute(vm.RefreshDirectoryCommand);
                break;
            case Key.L when ctrl:
            case Key.F4 when none:
                BeginPathEdit();
                break;
            case Key.F when ctrl:
            case Key.E when ctrl:
                SearchBox.Focus();
                SearchBox.SelectAll();
                break;
            case Key.X when ctrl && !inTextBox:
                Execute(vm.CutCommand);
                break;
            case Key.C when ctrl && !inTextBox:
                Execute(vm.CopyCommand);
                break;
            case Key.V when ctrl && !inTextBox:
                Execute(vm.PasteCommand);
                break;
            case Key.A when ctrl && !inTextBox:
                OnSelectAllClick(sender, e);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private static void Execute(System.Windows.Input.ICommand command)
    {
        if (command.CanExecute(null)) command.Execute(null);
    }

    // ── Address bar ──────────────────────────────────────────────────────

    private void OnBreadcrumbItemClicked(FABreadcrumbBar sender, FABreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Item is T.UI.Models.PathSegment segment && Vm is { } vm)
            _ = vm.NavigateToPathAsync(segment.Path);
    }

    /// <summary>A click next to the path segments switches to typing a path (like Windows Explorer).</summary>
    private void OnAddressBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (PathBox.IsVisible || !e.GetCurrentPoint(AddressBar).Properties.IsLeftButtonPressed) return;
        if ((e.Source as Control)?.FindAncestorOfType<FABreadcrumbBarItem>(includeSelf: true) != null) return;

        BeginPathEdit();
        e.Handled = true;
    }

    private void BeginPathEdit()
    {
        if (Vm is not { IsConnected: true } vm) return;
        vm.PathInput = vm.CurrentPath;
        Breadcrumb.IsVisible = false;
        PathBox.IsVisible = true;
        Dispatcher.UIThread.Post(() =>
        {
            PathBox.Focus();
            PathBox.SelectAll();
        });
    }

    private void EndPathEdit()
    {
        PathBox.IsVisible = false;
        Breadcrumb.IsVisible = true;
    }

    private void OnPathBoxLostFocus(object? sender, RoutedEventArgs e) => EndPathEdit();

    private void OnPathKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;

        if (e.Key == Key.Enter)
        {
            if (vm.NavigateToInputCommand.CanExecute(null))
                vm.NavigateToInputCommand.Execute(null);
            EndPathEdit();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            EndPathEdit();
            e.Handled = true;
        }
    }

    // ── File list ────────────────────────────────────────────────────────

    private void OnFileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Vm is { } vm && FileAt(e.Source) is { } file)
            _ = vm.ActivateAsync(file);
    }

    /// <summary>With "double-click to open" disabled, a single click opens directories.</summary>
    private void OnFileTapped(object? sender, TappedEventArgs e)
    {
        if (Vm is { OpenDirectoriesWithSingleClick: true } vm && e.KeyModifiers == KeyModifiers.None &&
            FileAt(e.Source) is { } file && (file.IsDirectory || file.IsParentLink))
        {
            _ = vm.NavigateToAsync(file);
        }
    }

    private void OnFileKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;

        switch (e.Key)
        {
            case Key.Enter:
                _ = vm.ActivateAsync(vm.SelectedFile);
                e.Handled = true;
                break;
            case Key.Back:
                Execute(vm.NavigateUpCommand);
                e.Handled = true;
                break;
            case Key.Delete:
                Execute(vm.DeleteCommand);
                e.Handled = true;
                break;
            case Key.F2:
                OnRenameClick(sender, new RoutedEventArgs());
                e.Handled = true;
                break;
        }
    }

    /// <summary>Column headers sort like Explorer (folders stay on top), so the view model sorts instead of the grid.</summary>
    private void OnDetailsSorting(object? sender, DataGridColumnEventArgs e)
    {
        e.Handled = true;
        var key = e.Column.SortMemberPath switch
        {
            "LastModified" => "Date",
            "Extension" => "Type",
            { Length: > 0 } path => path,
            _ => null
        };
        if (key != null && Vm is { } vm)
            vm.SortExplorerCommand.Execute(key);
    }

    // ── Context menu (built for the current selection) ───────────────────

    private void OnFileContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (Vm is not { IsConnected: true } vm) return;

        var clicked = FileAt(e.Source);
        if (clicked is null)
        {
            // Empty area: the menu is about the folder itself.
            ApplySelection([]);
            vm.SetSelection([]);
        }

        var selection = vm.SelectedFiles;
        var items = new List<Control>();
        if (selection.Count > 0)
        {
            if (selection.Count == 1)
                items.Add(Item("Open", "open_regular", (_, _) => _ = vm.ActivateAsync(selection[0]), gesture: "Enter"));
            items.Add(Item("Download", "arrow_download_regular", vm.DownloadCommand));
            items.Add(Item("Download to…", null, OnDownloadToClick));
            items.Add(new Separator());
            items.Add(Item("Cut", "cut_regular", vm.CutCommand, "Ctrl+X"));
            items.Add(Item("Copy", "copy_regular", vm.CopyCommand, "Ctrl+C"));
            items.Add(Item("Paste", "clipboard_paste_regular", vm.PasteCommand, "Ctrl+V"));
            items.Add(Item("Move to…", "folder_arrow_right_regular", OnMoveToClick));
            items.Add(Item("Copy to…", null, OnCopyToClick));
            items.Add(new Separator());
            var rename = Item("Rename", "rename_regular", OnRenameClick, "F2");
            rename.IsEnabled = selection.Count == 1;
            items.Add(rename);
            items.Add(Item("Delete", "delete_regular", vm.DeleteCommand, "Del"));
            items.Add(new Separator());
            items.Add(Item(selection.Count == 1 ? "Copy path" : "Copy paths", "document_copy_regular", OnCopyPathClick));
            if (HostWindow?.DataContext is MainWindowViewModel main)
                items.Add(Item("Permissions…", "lock_closed_regular", main.ShowPermissionDialogCommand));
        }
        else
        {
            items.Add(Item("Paste", "clipboard_paste_regular", vm.PasteCommand, "Ctrl+V"));
            items.Add(new Separator());
            items.Add(Item("New folder…", "folder_add_regular", OnNewFolderClick));
            items.Add(Item("Upload…", "arrow_upload_regular", OnUploadClick));
            items.Add(Item("Select all", "select_all_on_regular", OnSelectAllClick, "Ctrl+A"));
            items.Add(Item("Refresh", "arrow_clockwise_regular", vm.RefreshDirectoryCommand, "F5"));
        }

        var menu = new ContextMenu { ItemsSource = items };
        var target = (e.Source as Control) ?? ActiveList;
        target.ContextMenu = menu;
        menu.Open(target);
        e.Handled = true;
    }

    private static MenuItem Item(string header, string? icon, System.Windows.Input.ICommand command, string? gesture = null)
    {
        var item = Item(header, icon, gesture);
        item.Command = command;
        return item;
    }

    private static MenuItem Item(string header, string? icon, EventHandler<RoutedEventArgs> click, string? gesture = null)
    {
        var item = Item(header, icon, gesture);
        item.Click += click;
        return item;
    }

    private static MenuItem Item(string header, string? icon, string? gesture)
    {
        var item = new MenuItem { Header = header };
        if (gesture != null)
            item.InputGesture = KeyGesture.Parse(gesture == "Del" ? "Delete" : gesture);
        if (icon != null && Application.Current?.TryGetResource(icon, null, out var geometry) == true && geometry is Geometry g)
            item.Icon = new PathIcon { Data = g, Width = 14, Height = 14 };
        return item;
    }

    // ── Command bar menus ────────────────────────────────────────────────

    private void OnSortClick(object? sender, RoutedEventArgs e) => ShowMenu("SortMenu", sender);
    private void OnViewClick(object? sender, RoutedEventArgs e) => ShowMenu("ViewMenu", sender);

    private void ShowMenu(string key, object? anchor)
    {
        if (anchor is Control control && Resources.TryGetValue(key, out var menu) && menu is MenuFlyout flyout)
            flyout.ShowAt(control);
    }

    // ── Commands with dialogs ────────────────────────────────────────────

    private async void OnCopyPathClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { HasSelection: true } vm || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return;
        var text = string.Join(Environment.NewLine, vm.SelectedFiles.Select(f => f.FullPath));
        await ClipboardExtensions.SetTextAsync(clipboard, text);
    }

    private async void OnUploadClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm)
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider == null)
            return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Files to Upload",
            AllowMultiple = true
        });

        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count > 0)
            await vm.UploadPathsAsync(paths);
    }

    private async void OnDownloadToClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { HasSelection: true } vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = $"Download {(vm.SelectedFiles.Count == 1 ? vm.SelectedFiles[0].Name : $"{vm.SelectedFiles.Count} items")} to",
            AllowMultiple = false
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } folder)
            await vm.DownloadToAsync(folder);
    }

    private void OnMoveToClick(object? sender, RoutedEventArgs e) => _ = MoveOrCopyToAsync(copy: false);
    private void OnCopyToClick(object? sender, RoutedEventArgs e) => _ = MoveOrCopyToAsync(copy: true);

    private async System.Threading.Tasks.Task MoveOrCopyToAsync(bool copy)
    {
        if (Vm is not { HasSelection: true } vm) return;

        var files = vm.SelectedFiles.ToList();
        var what = files.Count == 1 ? $"\"{files[0].Name}\"" : $"{files.Count} items";
        var verb = copy ? "Copy" : "Move";
        var target = await DialogService.PromptAsync(HostWindow, $"{verb} to", $"{verb} {what} to this folder on the server:",
            vm.CurrentPath, "/path/to/folder");
        if (!string.IsNullOrWhiteSpace(target))
            await vm.MoveOrCopyAsync(files, target, copy);
    }

    private async void OnNewFolderClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { IsConnected: true } vm)
            return;

        var name = await DialogService.PromptAsync(HostWindow, "New folder", "", placeholder: "Folder name");
        if (!string.IsNullOrWhiteSpace(name))
            await vm.CreateDirectoryAsync(name);
    }

    private async void OnRenameClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { SelectedFiles: [var file] } vm)
            return;

        var newName = await DialogService.PromptAsync(HostWindow, "Rename", "", file.Name, "New name");
        if (!string.IsNullOrWhiteSpace(newName) && newName != file.Name)
            await vm.RenameAsync(file, newName);
    }

    // ── Drag and drop: upload from outside, move / copy inside ───────────

    /// <summary>The folder a drop at <paramref name="source"/> goes into: a folder row or tree node, else the current folder.</summary>
    private (string Path, string Name, Control? Element) DropTargetAt(object? source, SessionViewModel vm)
    {
        var visual = source as Visual;
        if (visual?.FindAncestorOfType<TreeViewItem>(includeSelf: true) is { DataContext: DirectoryNode { IsPlaceholder: false } node } treeItem)
            return (node.FullPath, node.Name, treeItem);

        var container = (Control?)visual?.FindAncestorOfType<DataGridRow>(includeSelf: true)
                        ?? visual?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
        if (container?.DataContext is RemoteFile { } folder && (folder.IsDirectory || folder.IsParentLink) &&
            _draggedFiles?.Contains(folder) != true)
        {
            var path = folder.IsParentLink ? RemotePath.GetParent(vm.CurrentPath) : folder.FullPath;
            return (path, RemotePath.GetName(path), container);
        }

        return (vm.CurrentPath, vm.CurrentFolderName, null);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (Vm is not { IsConnected: true } vm)
        {
            e.DragEffects = DragDropEffects.None;
            HideDropFeedback();
            return;
        }

        var (path, name, element) = DropTargetAt(e.Source, vm);
        string? text = null;
        string icon = "arrow_upload_regular";

        if (_draggedFiles is { } dragged)
        {
            bool copy = e.KeyModifiers.HasFlag(KeyModifiers.Control);
            if (SessionViewModel.CanTransferInto(dragged, path, copy))
            {
                text = $"{(copy ? "Copy" : "Move")} {(dragged.Count == 1 ? dragged[0].Name : $"{dragged.Count} items")} to {name}";
                icon = copy ? "copy_regular" : "folder_arrow_right_regular";
                e.DragEffects = copy ? DragDropEffects.Copy : DragDropEffects.Move;
            }
            else
            {
                e.DragEffects = DragDropEffects.None;
            }
        }
        else if (e.DataTransfer.Contains(DataFormat.File))
        {
            text = $"Upload to {name}";
            e.DragEffects = DragDropEffects.Copy;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }

        if (text is null)
        {
            HideDropFeedback();
            return;
        }

        SetDropHighlight(element);
        DropTargetText.Text = text;
        if (Application.Current?.TryGetResource(icon, null, out var geometry) == true && geometry is Geometry g)
            DropTargetIcon.Data = g;
        DropTarget.Classes.Set("active", true);
    }

    private void OnDragLeave(object? sender, DragEventArgs e) => HideDropFeedback();

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        HideDropFeedback();
        if (Vm is not { IsConnected: true } vm)
            return;

        e.Handled = true;
        var (path, _, _) = DropTargetAt(e.Source, vm);

        if (_draggedFiles is { } dragged)
        {
            bool copy = e.KeyModifiers.HasFlag(KeyModifiers.Control);
            if (SessionViewModel.CanTransferInto(dragged, path, copy))
                await vm.MoveOrCopyAsync(dragged, path, copy);
            return;
        }

        if (e.DataTransfer.TryGetFiles() is not { Length: > 0 } items)
            return;
        var paths = items.Select(item => item.TryGetLocalPath()).OfType<string>().ToList();
        await vm.UploadPathsAsync(paths, path);
    }

    private void SetDropHighlight(Control? element)
    {
        if (ReferenceEquals(_dropHighlight, element)) return;
        _dropHighlight?.Classes.Remove("dropInto");
        _dropHighlight = element;
        _dropHighlight?.Classes.Add("dropInto");
    }

    private void HideDropFeedback()
    {
        SetDropHighlight(null);
        DropTarget.Classes.Set("active", false);
    }
}
