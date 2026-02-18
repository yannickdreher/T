using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace T.Models;

public partial class SessionTreeNode : ObservableObject, ITreeNode
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private ObservableCollection<ITreeNode> _children = [];

    public bool IsFolder => false;
    public required SshSession Session { get; init; }

    public static SessionTreeNode FromSession(SshSession session) => new()
    {
        Name = session.Name,
        Session = session
    };
}