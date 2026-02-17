using CommunityToolkit.Mvvm.ComponentModel;
using FluentAvalonia.UI.Controls;
using System;
using System.Collections.Generic;

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
    public string IconColor => IsDirectory ? "#FFD700" : GetFileIconColor(Name);
    
    private static readonly Dictionary<string, (Symbol Symbol, string Color)> FileTypeMap = new()
    {
        // Documents
        [".txt"] = (Symbol.Document, "#6C757D"),
        [".log"] = (Symbol.Document, "#6C757D"),
        [".md"] = (Symbol.Document, "#6C757D"),
        [".pdf"] = (Symbol.Page, "#DC3545"),
        
        // Code & Scripts
        [".sh"] = (Symbol.Code, "#28A745"),
        [".bash"] = (Symbol.Code, "#28A745"),
        [".py"] = (Symbol.Code, "#3776AB"),
        [".js"] = (Symbol.Code, "#F7DF1E"),
        [".ts"] = (Symbol.Code, "#3178C6"),
        [".cs"] = (Symbol.Code, "#239120"),
        [".cpp"] = (Symbol.Code, "#00599C"),
        [".c"] = (Symbol.Code, "#A8B9CC"),
        [".h"] = (Symbol.Code, "#A8B9CC"),
        [".java"] = (Symbol.Code, "#007396"),
        [".go"] = (Symbol.Code, "#00ADD8"),
        [".rs"] = (Symbol.Code, "#CE422B"),
        [".php"] = (Symbol.Code, "#777BB4"),
        [".rb"] = (Symbol.Code, "#CC342D"),
        
        // Configuration
        [".conf"] = (Symbol.Settings, "#FFC107"),
        [".cfg"] = (Symbol.Settings, "#FFC107"),
        [".ini"] = (Symbol.Settings, "#FFC107"),
        [".yaml"] = (Symbol.Settings, "#FFC107"),
        [".yml"] = (Symbol.Settings, "#FFC107"),
        [".json"] = (Symbol.Settings, "#FFC107"),
        [".xml"] = (Symbol.Settings, "#FFC107"),
        [".toml"] = (Symbol.Settings, "#FFC107"),
        [".env"] = (Symbol.Settings, "#FFC107"),
        
        // Archives
        [".zip"] = (Symbol.ZipFolder, "#9C27B0"),
        [".tar"] = (Symbol.ZipFolder, "#9C27B0"),
        [".gz"] = (Symbol.ZipFolder, "#9C27B0"),
        [".7z"] = (Symbol.ZipFolder, "#9C27B0"),
        [".rar"] = (Symbol.ZipFolder, "#9C27B0"),
        [".bz2"] = (Symbol.ZipFolder, "#9C27B0"),
        [".xz"] = (Symbol.ZipFolder, "#9C27B0"),
        
        // Images
        [".jpg"] = (Symbol.Image, "#FF6B6B"),
        [".jpeg"] = (Symbol.Image, "#FF6B6B"),
        [".png"] = (Symbol.Image, "#FF6B6B"),
        [".gif"] = (Symbol.Image, "#FF6B6B"),
        [".bmp"] = (Symbol.Image, "#FF6B6B"),
        [".svg"] = (Symbol.Image, "#FF6B6B"),
        [".webp"] = (Symbol.Image, "#FF6B6B"),
        [".ico"] = (Symbol.Image, "#FF6B6B"),
        
        // Video
        [".mp4"] = (Symbol.Video, "#E91E63"),
        [".avi"] = (Symbol.Video, "#E91E63"),
        [".mkv"] = (Symbol.Video, "#E91E63"),
        [".mov"] = (Symbol.Video, "#E91E63"),
        [".webm"] = (Symbol.Video, "#E91E63"),
        
        // Audio
        [".mp3"] = (Symbol.Audio, "#9C27B0"),
        [".wav"] = (Symbol.Audio, "#9C27B0"),
        [".flac"] = (Symbol.Audio, "#9C27B0"),
        [".ogg"] = (Symbol.Audio, "#9C27B0"),
        
        // Executables
        [".exe"] = (Symbol.Library, "#DC3545"),
        [".dll"] = (Symbol.Library, "#DC3545"),
        [".so"] = (Symbol.Library, "#DC3545"),
        [".dylib"] = (Symbol.Library, "#DC3545"),
        
        // Database
        [".db"] = (Symbol.Library, "#17A2B8"),
        [".sqlite"] = (Symbol.Library, "#17A2B8"),
        [".sql"] = (Symbol.Library, "#17A2B8"),
    };
    
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
        return FileTypeMap.TryGetValue(ext, out var info) ? info.Symbol : Symbol.Document;
    }
    
    private static string GetFileIconColor(string name)
    {
        var ext = System.IO.Path.GetExtension(name).ToLower();
        return FileTypeMap.TryGetValue(ext, out var info) ? info.Color : "#6C757D";
    }
}