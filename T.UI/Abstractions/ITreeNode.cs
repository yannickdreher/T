using System.Collections.ObjectModel;

namespace T.UI.Abstractions;

public interface ITreeNode
{
    string Name { get; set; }
    bool IsExpanded { get; set; }
    bool IsFolder { get; }
    ObservableCollection<ITreeNode> Children { get; }
}