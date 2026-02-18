using System.Collections.ObjectModel;

namespace T.Models;

public interface ITreeNode
{
    string Name { get; set; }
    bool IsExpanded { get; set; }
    bool IsFolder { get; }
    ObservableCollection<ITreeNode> Children { get; }
}