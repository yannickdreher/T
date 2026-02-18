using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace T.Models;

public partial class FolderTreeNode : ObservableObject, ITreeNode
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private ObservableCollection<ITreeNode> _children = [];

    public bool IsFolder => true;
    public required Folder Folder { get; init; }

    public static FolderTreeNode FromFolder(Folder folder) => new()
    {
        Name = folder.Name,
        IsExpanded = folder.IsExpanded,
        Folder = folder
    };
}