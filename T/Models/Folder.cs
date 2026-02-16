using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace T.Models;

public partial class Folder : ObservableObject
{
    [ObservableProperty] private string _id = Guid.NewGuid().ToString();
    [ObservableProperty] private string _name = "New folder";
    [ObservableProperty] private string? _parentId;

    [JsonIgnore]
    [ObservableProperty] private ObservableCollection<Folder> _children = [];

    [JsonIgnore]
    [ObservableProperty] private ObservableCollection<SshSession> _hosts = [];

    [JsonIgnore]
    [ObservableProperty] private bool _isExpanded;
}