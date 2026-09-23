using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using T.Models;
using T.UI.Abstractions;

namespace T.UI.Models;

public partial class SessionTreeNode : ObservableObject, ITreeNode
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private ObservableCollection<ITreeNode> _children = [];

    /// <summary>Number of tabs in which this saved session is open (0 = none).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOpenTabs))]
    private int _openTabs;

    public bool HasOpenTabs => OpenTabs > 0;

    public bool IsFolder => false;
    public required SshSession Session { get; init; }

    public static SessionTreeNode FromSession(SshSession session) => new()
    {
        Name = session.Name,
        Session = session
    };
}
