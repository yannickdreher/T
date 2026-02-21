using CommunityToolkit.Mvvm.ComponentModel;

namespace T.Models;

public partial class ExplorerSettings : ObservableObject
{
    [ObservableProperty] private bool _showHiddenFiles;
    [ObservableProperty] private bool _confirmDelete = true;
    [ObservableProperty] private string _defaultDownloadPath = "";
    [ObservableProperty] private bool _doubleClickToOpen = true;
    [ObservableProperty] private string _sortBy = "Name";
}