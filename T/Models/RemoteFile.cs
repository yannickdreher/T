using CommunityToolkit.Mvvm.ComponentModel;
using FluentAvalonia.UI.Controls;
using System;

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
    
    public string SizeDisplay => IsDirectory ? "" : FormatSize(Size);
    public Symbol IconSymbol => IsDirectory ? Symbol.Folder : GetFileIconSymbol(Name);
    
    private static string FormatSize(long bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB", "TB"];
        int order = 0;
        double size = bytes;
        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size:0.##} {sizes[order]}";
    }
    
    private static Symbol GetFileIconSymbol(string name)
    {
        var ext = System.IO.Path.GetExtension(name).ToLower();
        return ext switch
        {
            ".txt" or ".log" or ".md" => Symbol.Document,
            ".sh" or ".bash" => Symbol.Code,
            ".conf" or ".cfg" or ".ini" or ".yaml" or ".yml" or ".json" => Symbol.Settings,
            ".zip" or ".tar" or ".gz" or ".7z" => Symbol.ZipFolder,
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" => Symbol.Image,
            ".pdf" => Symbol.Page,
            ".exe" or ".dll" => Symbol.Library,
            _ => Symbol.Document
        };
    }
}