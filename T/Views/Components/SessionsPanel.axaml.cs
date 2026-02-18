using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.VisualTree;
using System;
using T.Models;
using T.ViewModels;

namespace T.Views.Components;

public partial class SessionsPanel : UserControl
{
    private ITreeNode? _draggedNode;
    private TreeViewItem? _draggedItem;
    private TreeViewItem? _lastDropTarget;
    private Point _dragStartPoint;
    private bool _isDragging;
    private const double DragThreshold = 8;

    public SessionsPanel()
    {
        InitializeComponent();

        SessionTreeView.AddHandler(
            PointerPressedEvent,
            OnTreeViewPointerPressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);

        SessionTreeView.AddHandler(
            PointerMovedEvent,
            OnTreeViewPointerMoved,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);

        SessionTreeView.AddHandler(
            PointerReleasedEvent,
            OnTreeViewPointerReleased,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    private async void OnSessionDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (vm.SelectedTreeNode is SessionTreeNode)
            await vm.ConnectCommand.ExecuteAsync(null);
    }

    private void OnEmptyAreaPressed(object? sender, PointerPressedEventArgs e)
    {
        Vm?.SelectedTreeNode = null;
    }

    // ── Container lifecycle ─────────────────────────────────────────────

    private void OnTreeViewContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container is TreeViewItem item && item.DataContext is ITreeNode node)
        {
            item.Expanded -= OnTreeViewItemExpanded;
            item.Collapsed -= OnTreeViewItemCollapsed;

            item.Expanded += OnTreeViewItemExpanded;
            item.Collapsed += OnTreeViewItemCollapsed;

            if (node.IsFolder)
            {
                item.IsExpanded = node.IsExpanded;
            }
        }
    }

    private void OnTreeViewContainerClearing(object? sender, ContainerClearingEventArgs e)
    {
        if (e.Container is TreeViewItem item)
        {
            item.Expanded -= OnTreeViewItemExpanded;
            item.Collapsed -= OnTreeViewItemCollapsed;
        }
    }

    // ── Pointer events ──────────────────────────────────────────────────

    private void OnTreeViewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);

        if (point.Properties.IsRightButtonPressed)
        {
            HandleRightClick(e);
            return;
        }

        if (point.Properties.IsLeftButtonPressed)
        {
            var item = FindTreeViewItem(e.Source as Control);
            if (item is { DataContext: ITreeNode node })
            {
                _draggedNode = node;
                _draggedItem = item;
                _dragStartPoint = point.Position;
                _isDragging = false;
            }
        }
    }

    private async void OnTreeViewPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_draggedNode is null || _draggedItem is null) return;

        var currentPoint = e.GetCurrentPoint(this);
        if (!currentPoint.Properties.IsLeftButtonPressed)
        {
            ResetDrag();
            return;
        }

        var delta = currentPoint.Position - _dragStartPoint;
        if (!_isDragging && Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold)
            return;

        _isDragging = true;
        _draggedItem.Classes.Add("drag-source");

        await DragDrop.DoDragDropAsync(e, new DataTransfer(), DragDropEffects.Move);

        _draggedItem.Classes.Remove("drag-source");
        ClearDropTarget();
        ResetDrag();
    }

    private void OnTreeViewPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDragging) ResetDrag();
    }

    // ── Context menu ────────────────────────────────────────────────────

    private void HandleRightClick(PointerPressedEventArgs e)
    {
        if (Vm is null) return;

        var item = FindTreeViewItem(e.Source as Control);
        if (item is not { DataContext: ITreeNode node }) return;

        e.Handled = true;
        Vm.SelectedTreeNode = node;

        var menu = new ContextMenu();

        if (node.IsFolder)
        {
            menu.Items.Add(new MenuItem { Header = "Add new folder", Command = Vm.NewFolderCommand });
            menu.Items.Add(new MenuItem { Header = "Add new session", Command = Vm.NewSessionCommand });
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "Edit folder", Command = Vm.EditFolderCommand });
            menu.Items.Add(new MenuItem { Header = "Delete folder", Command = Vm.DeleteSelectedCommand });
        }
        else
        {
            menu.Items.Add(new MenuItem { Header = "Connect", Command = Vm.ConnectCommand });
            menu.Items.Add(new MenuItem { Header = "Edit session", Command = Vm.EditSessionCommand });
            menu.Items.Add(new MenuItem { Header = "Delete session", Command = Vm.DeleteSelectedCommand });
        }

        item.ContextMenu = menu;
        menu.Open(item);
    }

    // ── Drag & Drop visuals ─────────────────────────────────────────────

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (_draggedNode is null)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        var targetItem = FindTreeViewItem(e.Source as Control);
        var targetNode = targetItem?.DataContext as ITreeNode;

        if (targetNode == _draggedNode ||
            (_draggedNode.IsFolder && targetNode != null && MainWindowViewModel.IsDescendantOf(targetNode, _draggedNode)))
        {
            e.DragEffects = DragDropEffects.None;
            ClearDropTarget();
            return;
        }

        e.DragEffects = DragDropEffects.Move;

        if (targetItem != _lastDropTarget)
        {
            ClearDropTarget();
            _lastDropTarget = targetItem;
            _lastDropTarget?.Classes.Add("drag-over");
        }
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        ClearDropTarget();
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        ClearDropTarget();

        if (Vm is null || _draggedNode is null) return;

        var targetItem = FindTreeViewItem(e.Source as Control);
        ITreeNode? target = targetItem?.DataContext as ITreeNode;

        if (target == _draggedNode) return;

        Vm.MoveNodeCommand.Execute((_draggedNode, target));
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static TreeViewItem? FindTreeViewItem(Control? source)
    {
        while (source is not null and not TreeViewItem)
            source = source.GetVisualParent() as Control;

        return source as TreeViewItem;
    }

    private void ClearDropTarget()
    {
        _lastDropTarget?.Classes.Remove("drag-over");
        _lastDropTarget = null;
    }

    private void ResetDrag()
    {
        _draggedNode = null;
        _draggedItem = null;
        _isDragging = false;
    }

    private void OnTreeViewItemExpanded(object? sender, RoutedEventArgs e)
    {
        if (sender is TreeViewItem { DataContext: ITreeNode node })
        {
            node.IsExpanded = true;
            e.Handled = true;
        }
    }

    private void OnTreeViewItemCollapsed(object? sender, RoutedEventArgs e)
    {
        if (sender is TreeViewItem { DataContext: ITreeNode node })
        {
            node.IsExpanded = false;
            e.Handled = true;
        }
    }
}