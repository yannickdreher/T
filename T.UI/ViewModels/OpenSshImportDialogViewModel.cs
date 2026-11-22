using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using T.Models;
using T.Services;

namespace T.UI.ViewModels;

public partial class OpenSshImportDialogViewModel : ViewModelBase
{
    private readonly IReadOnlyList<SshSession> _existing;

    [ObservableProperty] private OpenSshImportFolder? _destination;
    [ObservableProperty] private OpenSshImportRow? _selectedRow;

    public IReadOnlyList<OpenSshImportRow> Rows { get; }
    public IReadOnlyList<OpenSshImportFolder> Destinations { get; }

    /// <summary>Design-time constructor with a small sample configuration.</summary>
    public OpenSshImportDialogViewModel() : this(
        OpenSshConfigImport.Parse("""
            Host *
              User deploy
            Host bastion
              HostName bastion.example.com
            Host db
              HostName 10.0.0.5
              ProxyJump bastion
            Host legacy
              HostName legacy.example.com
              ProxyCommand nc -X 5 -x proxy:1080 %h %p
            Host web
              HostName web.example.com
              Port 2222
              StrictHostKeyChecking no
            """, "/home/deploy", "deploy"),
        [], [], null)
    {
    }

    public OpenSshImportDialogViewModel(
        IReadOnlyList<OpenSshConfigImport.Entry> entries,
        IReadOnlyList<Folder> folders,
        IReadOnlyList<SshSession> existing,
        string? initialFolderId)
    {
        _existing = existing;
        var foldersById = folders.ToDictionary(folder => folder.Id);
        Destinations =
        [
            new(null, "Top level"),
            .. folders.Select(folder => new OpenSshImportFolder(folder.Id, FolderPath(folder, foldersById)))
                .OrderBy(folder => folder.Label, StringComparer.CurrentCultureIgnoreCase)
        ];
        Rows = entries.Select(entry => new OpenSshImportRow(entry)).ToArray();
        foreach (var row in Rows)
            row.Target.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(SshSession.Name))
                    UpdateCollision(row);
            };

        Destination = Destinations.FirstOrDefault(folder => folder.Id == initialFolderId) ?? Destinations[0];
        SelectedRow = Rows.Count > 0 ? Rows[0] : null;
    }

    partial void OnDestinationChanged(OpenSshImportFolder? value)
    {
        foreach (var row in Rows)
            UpdateCollision(row);
    }

    private void UpdateCollision(OpenSshImportRow row)
    {
        var name = row.Target.Name.Trim();
        row.NameCollision = _existing.Any(saved =>
            saved.FolderId == Destination?.Id &&
            saved.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (row.NameCollision)
            row.IsSelected = false;
    }

    public string? TryBuildImport(out IReadOnlyList<SshImportProfile> profiles)
    {
        profiles = [];
        var selected = Rows.Where(row => row.IsSelected).ToArray();
        if (selected.Length == 0)
            return "Select at least one profile to import.";

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<SshImportProfile>();
        foreach (var row in selected)
        {
            var name = row.Target.Name.Trim();
            if (name.Length == 0 || name.Any(char.IsControl))
                return $"{row.Alias}: enter a valid profile name.";
            if (row.NameCollision || !names.Add(name))
                return $"{row.Alias}: choose a profile name that is unique in the destination folder.";
            if (row.Entry.RouteError is { } routeError)
                return $"{row.Alias}: {routeError}";
            if (row.Entry.RequiresReview && !row.Reviewed)
                return $"{row.Alias}: review the displayed settings and confirm them.";

            var target = Normalize(row.Target);
            if (OpenSshConfigImport.GetConnectionError(target) is { } targetError)
                return $"{row.Alias}: {targetError}";

            var hops = row.JumpHosts.Select(Normalize).ToArray();
            foreach (var hop in hops)
            {
                if (OpenSshConfigImport.GetConnectionError(hop) is { } hopError)
                    return $"{row.Alias}, jump host {hop.Name}: {hopError}";
            }

            result.Add(new(target, hops));
        }

        profiles = result;
        return null;
    }

    private static SshSession Normalize(SshSession source)
    {
        var copy = source.Clone();
        copy.Name = copy.Name.Trim();
        copy.Host = copy.Host.Trim();
        copy.Username = copy.Username.Trim();
        copy.PrivateKeyPath = copy.PrivateKeyPath.Trim();
        return copy;
    }

    private static string FolderPath(Folder folder, IReadOnlyDictionary<string, Folder> folders)
    {
        var names = new Stack<string>();
        var visited = new HashSet<string>();
        for (Folder? current = folder; current != null && visited.Add(current.Id);
             current = current.ParentId is { } id ? folders.GetValueOrDefault(id) : null)
            names.Push(current.Name);

        return string.Join(" / ", names);
    }
}

public sealed record OpenSshImportFolder(string? Id, string Label);

public partial class OpenSshImportRow : ObservableObject
{
    [ObservableProperty] private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Badge), nameof(HasBadge))]
    private bool _reviewed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Badge), nameof(HasBadge))]
    private bool _nameCollision;
    [ObservableProperty] private bool _wasEdited;

    public OpenSshConfigImport.Entry Entry { get; }
    public string Alias => Entry.Alias;
    public SshSession Target { get; }
    public IReadOnlyList<SshSession> JumpHosts { get; }
    public bool HasJumpHosts => JumpHosts.Count > 0;
    public bool CanSelect => Entry.RouteError is null;
    public bool IsBlocked => !CanSelect;
    public string? RouteError => Entry.RouteError;
    public bool RequiresReview => Entry.RequiresReview;

    /// <summary>Short state for the list: why a profile cannot or should not be imported as is.</summary>
    public string? Badge => IsBlocked ? "Blocked"
        : NameCollision ? "Exists"
        : RequiresReview && !Reviewed ? "Review"
        : null;
    public bool HasBadge => Badge != null;
    public bool BadgeIsError => IsBlocked;
    public string? Warnings { get; }
    public bool HasWarnings => Warnings is not null;
    public string Defaults { get; }

    public OpenSshImportRow(OpenSshConfigImport.Entry entry)
    {
        Entry = entry;
        Target = entry.Profile.Session.Clone();
        JumpHosts = entry.Profile.JumpHosts.Select(hop => hop.Clone()).ToArray();
        IsSelected = CanSelect && !RequiresReview &&
            OpenSshConfigImport.GetConnectionError(Target) is null &&
            JumpHosts.All(hop => OpenSshConfigImport.GetConnectionError(hop) is null);
        // The route error is shown on its own (it blocks the import); these are hints.
        Warnings = entry.Warnings.Count == 0 ? null : string.Join("\n", entry.Warnings);

        var defaults = new List<string>();
        if (entry.DefaultHost) defaults.Add("host from alias");
        if (entry.DefaultUser) defaults.Add("local user name");
        if (entry.DefaultPort) defaults.Add("port 22");
        Defaults = defaults.Count == 0
            ? "Host, user and port are defined in the configuration."
            : $"Defaults: {string.Join(", ", defaults)}.";

        Target.PropertyChanged += (_, _) => OnEdited();
        foreach (var hop in JumpHosts)
            hop.PropertyChanged += (_, _) => OnEdited();
    }

    private void OnEdited()
    {
        WasEdited = true;
        Reviewed = false;
    }
}
