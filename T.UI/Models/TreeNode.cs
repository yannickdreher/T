using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using T.Models;

namespace T.UI.Models;

public partial class TreeNode : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isFolder;
    [ObservableProperty] private ObservableCollection<TreeNode> _children = [];

    /// <summary>
    /// The underlying Folder (if this node represents a folder).
    /// </summary>
    public Folder? Folder { get; init; }

    /// <summary>
    /// The underlying SshSession (if this node represents a host).
    /// </summary>
    public SshSession? Session { get; init; }

    public static TreeNode FromFolder(Folder folder) => new()
    {
        Name = folder.Name,
        IsFolder = true,
        IsExpanded = folder.IsExpanded,
        Folder = folder
    };

    public static TreeNode FromSession(SshSession session) => new()
    {
        Name = session.Name,
        IsFolder = false,
        Session = session
    };
}