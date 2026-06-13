using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace T.UI.Models;

/// <summary>
/// Lazy-loading directory node for the SFTP folder tree.
/// A placeholder child is kept until the node is expanded for the first time.
/// </summary>
public partial class DirectoryNode : ObservableObject
{
    public static readonly DirectoryNode Placeholder = new() { Name = "…", FullPath = "" };

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _fullPath = "/";
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private ObservableCollection<DirectoryNode> _children = [];

    public bool IsPlaceholder => ReferenceEquals(this, Placeholder);
    public bool HasLoadedChildren { get; set; }

    public event Action<DirectoryNode>? ExpandRequested;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !HasLoadedChildren) ExpandRequested?.Invoke(this);
    }

    public DirectoryNode() { }

    public DirectoryNode(string name, string fullPath, bool withPlaceholder = true)
    {
        _name = name;
        _fullPath = fullPath;
        if (withPlaceholder) _children.Add(Placeholder);
    }

    public void SetChildren(IEnumerable<DirectoryNode> children)
    {
        Children = new ObservableCollection<DirectoryNode>(children);
        HasLoadedChildren = true;
    }
}
