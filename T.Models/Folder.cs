using CommunityToolkit.Mvvm.ComponentModel;

namespace T.Models;

public partial class Folder : ObservableObject
{
    [ObservableProperty] private string _id = Guid.NewGuid().ToString();
    [ObservableProperty] private string _name = "New folder";
    [ObservableProperty] private string? _parentId;
    [ObservableProperty] private bool _isExpanded;

    public Folder Clone() => new() { Id = Id, Name = Name, ParentId = ParentId, IsExpanded = IsExpanded };
}
