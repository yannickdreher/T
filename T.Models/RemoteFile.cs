using CommunityToolkit.Mvvm.ComponentModel;

namespace T.Models;

public partial class RemoteFile : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _fullPath = "";
    [ObservableProperty] private bool _isDirectory;
    [ObservableProperty] private long _size;
    [ObservableProperty] private DateTime _lastModified;
    [ObservableProperty] private string _permissions = "";
    [ObservableProperty] private string _owner = "";
    [ObservableProperty] private string _group = "";
    [ObservableProperty] private bool _isSelected;

    public string SizeDisplay => IsDirectory ? "-" : FormatSize(Size);

    public string IconSymbol => IsDirectory ? "Folder" : "Document";
    public string IconColor => IsDirectory ? "#FFC107" : "#9E9E9E";

    partial void OnSizeChanged(long value) => OnPropertyChanged(nameof(SizeDisplay));
    partial void OnIsDirectoryChanged(bool value)
    {
        OnPropertyChanged(nameof(SizeDisplay));
        OnPropertyChanged(nameof(IconSymbol));
        OnPropertyChanged(nameof(IconColor));
    }

    private static string FormatSize(long bytes)
    {
        const long kb = 1024;
        const long mb = kb * 1024;
        const long gb = mb * 1024;

        return bytes switch
        {
            < 0 => "0 B",
            < kb => $"{bytes} B",
            < mb => $"{bytes / (double)kb:0.0} KB",
            < gb => $"{bytes / (double)mb:0.0} MB",
            _ => $"{bytes / (double)gb:0.0} GB"
        };
    }
}