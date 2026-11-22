using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentAvalonia.UI.Controls;
using Microsoft.Extensions.DependencyInjection;
using T.Abstractions;
using T.Models;
using T.UI.Abstractions;
using T.UI.Models;
using T.UI.Services;
using T.UI.Views.Dialogs;

namespace T.UI.ViewModels;

/// <summary>
/// The tunnels pane: saved local port forwardings. Each tunnel connects through a terminal profile
/// (credentials, keys, step-ca and jump hosts come from there) with its own SSH connection,
/// independent of terminal tabs. Tunnels connect and disconnect individually.
/// </summary>
public sealed partial class TunnelsWorkspaceViewModel : ViewModelBase, IDisposable
{
    private readonly SessionsTreeViewModel? _terminals;
    private readonly ILocalTunnelStore? _store;
    private readonly IServiceProvider? _services;
    private readonly Dictionary<string, SessionViewModel> _connections = [];
    private readonly DispatcherTimer _sampler = new() { Interval = TimeSpan.FromSeconds(1) };
    private long _lastSample;
    private bool _opened;

    [ObservableProperty] private LocalTunnelRow? _selected;

    /// <summary>Loading the saved tunnels failed (shown in the pane).</summary>
    [ObservableProperty] private string? _loadError;

    public ObservableCollection<LocalTunnelRow> Tunnels { get; } = [];
    public bool HasTunnels => Tunnels.Count > 0;

    /// <summary>Number of tunnels with a bound listener (badge on the activity bar).</summary>
    public int ActiveCount => Tunnels.Count(t => t.IsActive);
    public bool HasActiveTunnels => ActiveCount > 0;

    /// <summary>"2 active · 5 tunnels"</summary>
    public string Summary => Tunnels.Count == 0
        ? "No tunnels"
        : $"{ActiveCount} active · {Tunnels.Count} {(Tunnels.Count == 1 ? "tunnel" : "tunnels")}";

    public bool HasSelection => Selected != null;

    /// <summary>Design-time constructor.</summary>
    public TunnelsWorkspaceViewModel()
    {
        var host = new SshSession { Name = "bastion", Host = "bastion.example.com", Username = "admin" };
        LocalTunnelRow Row(string name, int local, string target, int port, LocalTunnelStatus status) =>
            new(new LocalTunnelRule(name, host.Id, name, local, target, port, false), host) { Status = status };

        Tunnels.Add(Row("PostgreSQL", 5432, "db.internal", 5432, new(LocalTunnelState.Active)));
        Tunnels.Add(Row("Grafana", 3000, "localhost", 3000, new(LocalTunnelState.Starting)));
        Tunnels.Add(Row("Redis", 6379, "cache.internal", 6379, new(LocalTunnelState.Failed, "Local port 6379 is already in use.")));
        Tunnels.Add(Row("Admin UI", 8443, "10.0.0.12", 443, default));

        // Five minutes of traffic for the active one.
        var now = DateTimeOffset.Now;
        var active = Tunnels[0];
        active.BeginTraffic(now.AddMinutes(-12));
        long received = 0, sent = 0;
        for (int i = 0; i < 300; i += 2)
        {
            double down = 400_000 + 350_000 * Math.Sin(i / 17.0) + (i % 40 == 0 ? 900_000 : 0);
            double up = 60_000 + 40_000 * Math.Cos(i / 11.0);
            received += (long)(down * 2);
            sent += (long)(up * 2);
            active.ApplySample(new LocalTunnelStatistics(received, sent, 3, 42), now.AddSeconds(i - 300), 2);
        }
        Selected = active;
    }

    public TunnelsWorkspaceViewModel(SessionsTreeViewModel terminals, ISessionStorageService store, IServiceProvider services)
    {
        _terminals = terminals;
        _store = store as ILocalTunnelStore;
        _services = services;
        Tunnels.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasTunnels));
            OnPropertyChanged(nameof(Summary));
        };
        _sampler.Tick += (_, _) => SampleTraffic();

        // Profiles are added, edited, deleted (deleting one deletes its tunnels) or reloaded.
        terminals.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(SessionsTreeViewModel.Sessions)) return;
            WatchProfiles();
            ReloadIfOpen();
        };
        WatchProfiles();
    }

    private INotifyCollectionChanged? _watchedProfiles;

    private void WatchProfiles()
    {
        if (_watchedProfiles != null) _watchedProfiles.CollectionChanged -= OnProfilesChanged;
        _watchedProfiles = _terminals?.Sessions;
        if (_watchedProfiles != null) _watchedProfiles.CollectionChanged += OnProfilesChanged;
    }

    private void OnProfilesChanged(object? sender, NotifyCollectionChangedEventArgs e) => ReloadIfOpen();

    private void ReloadIfOpen()
    {
        if (_opened) _ = LoadAsync(Selected?.Rule.Id);
    }

    private Window? Host => _services?.GetService<IWindowProvider>()?.MainWindow;

    /// <summary>Loads the saved tunnels the first time the pane is shown.</summary>
    public void Open()
    {
        if (_opened) return;
        _opened = true;
        _ = LoadAsync(null);
    }

    partial void OnSelectedChanged(LocalTunnelRow? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        NotifyCommands();
    }

    private void NotifyCommands()
    {
        EditCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        ToggleCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ActiveCount));
        OnPropertyChanged(nameof(HasActiveTunnels));
        OnPropertyChanged(nameof(Summary));
    }

    // ── Traffic ──────────────────────────────────────────────────────────

    /// <summary>Reads the byte counters of all running tunnels (once per second while one runs).</summary>
    private void SampleTraffic()
    {
        long stamp = System.Diagnostics.Stopwatch.GetTimestamp();
        double seconds = _lastSample == 0 ? 1 : System.Diagnostics.Stopwatch.GetElapsedTime(_lastSample, stamp).TotalSeconds;
        _lastSample = stamp;

        var now = DateTimeOffset.Now;
        foreach (var row in Tunnels)
        {
            if (row.IsActive && _connections.TryGetValue(row.Rule.Id, out var vm) &&
                vm.CreateTunnelTransport() is ILocalTunnelConnection forwarding)
                row.ApplySample(forwarding.GetLocalTunnelStatistics(row.Rule.Id), now, seconds);
        }
    }

    private void UpdateSampler()
    {
        if (_connections.Count > 0 && !_sampler.IsEnabled)
        {
            _lastSample = 0;
            _sampler.Start();
        }
        else if (_connections.Count == 0)
        {
            _sampler.Stop();
        }
    }

    private void SetStatus(LocalTunnelRow row, LocalTunnelStatus status)
    {
        row.Status = status;
        NotifyCommands();
    }

    // ── Editing ──────────────────────────────────────────────────────────

    // A running tunnel has to be disconnected before it can be changed or deleted.
    private bool CanChange(LocalTunnelRow? row) => _store != null && (row ?? Selected) is { IsRunning: false };

    [RelayCommand]
    private async Task AddAsync()
    {
        if (_store is null) return;
        // The profile selected in the terminals pane is the likely one.
        var preselected = _terminals?.SelectedTreeNode is SessionTreeNode { Session: { } session } ? session : null;
        var editor = new TunnelEditorViewModel(_terminals?.Sessions ?? [], null, preselected);
        if (await ShowEditorAsync(editor, "New tunnel"))
            await SaveAsync(editor, null);
    }

    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task EditAsync(LocalTunnelRow? row)
    {
        if ((row ?? Selected) is not { } target || !CanChange(target)) return;
        var editor = new TunnelEditorViewModel(_terminals?.Sessions ?? [], target.Rule, null);
        if (await ShowEditorAsync(editor, "Edit tunnel"))
            await SaveAsync(editor, target.Rule);
    }

    private async Task<bool> ShowEditorAsync(TunnelEditorViewModel editor, string title)
    {
        if (Host is not { } owner) return false;
        var content = new TunnelEditorDialog { DataContext = editor };
        var dialog = new FAContentDialog
        {
            Title = title,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = FAContentDialogButton.Primary,
            Content = content
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            var error = editor.Validate();
            content.ValidationError = error;
            args.Cancel = error != null;
        };
        return await dialog.ShowAsync(owner) == FAContentDialogResult.Primary;
    }

    private async Task SaveAsync(TunnelEditorViewModel editor, LocalTunnelRule? existing)
    {
        if (_store is null || editor.Profile is not { } profile) return;
        try
        {
            var rule = editor.CreateRule(existing?.Id ?? Guid.NewGuid().ToString(), profile.Id);
            await _store.SaveLocalTunnelAsync(rule, creating: existing is null);
            await LoadAsync(rule.Id);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Saving the tunnel failed", ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task DeleteAsync(LocalTunnelRow? row)
    {
        if ((row ?? Selected) is not { } target || !CanChange(target) || _store is null || Host is not { } owner) return;
        if (!await DialogService.ConfirmAsync(owner, "Delete tunnel",
                $"Delete the tunnel \"{target.Rule.Name}\" ({target.Route})?"))
            return;

        try
        {
            await _store.DeleteLocalTunnelAsync(target.Rule);
            await LoadAsync(null);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Deleting the tunnel failed", ex.Message);
        }
    }

    // ── Connecting ───────────────────────────────────────────────────────

    // Starting or stopping: the click must not start a second listener for the same rule.
    private bool CanToggle(LocalTunnelRow? row) => _services != null && (row ?? Selected) is { IsBusy: false };

    [RelayCommand(CanExecute = nameof(CanToggle))]
    private async Task ToggleAsync(LocalTunnelRow? row)
    {
        if ((row ?? Selected) is not { IsBusy: false } target) return;
        if (target.IsRunning) await DisconnectAsync(target);
        else await ConnectAsync(target);
    }

    private async Task ConnectAsync(LocalTunnelRow row)
    {
        if (_services is null || _connections.ContainsKey(row.Rule.Id)) return;

        var vm = ActivatorUtilities.CreateInstance<SessionViewModel>(_services, row.Profile);
        var transport = vm.CreateTunnelTransport();
        var forwarding = (ILocalTunnelConnection)transport;
        _connections.Add(row.Rule.Id, vm);
        row.BeginTraffic(DateTimeOffset.Now);
        UpdateSampler();
        SetStatus(row, new(LocalTunnelState.Starting));

        bool IsCurrent() => _connections.TryGetValue(row.Rule.Id, out var current) && current == vm;

        transport.StatusChanged += (state, error) => Dispatcher.UIThread.Post(() =>
        {
            if (state != ConnectionStatus.Disconnected || !IsCurrent()) return;
            Release(row);
            SetStatus(row, error == null ? default : new(LocalTunnelState.Failed, error));
        });
        forwarding.LocalTunnelsChanged += () => Dispatcher.UIThread.Post(() =>
        {
            if (!IsCurrent()) return;
            var status = forwarding.GetLocalTunnelStatus(row.Rule.Id);
            if (status.State != LocalTunnelState.Inactive) SetStatus(row, status);
        });

        try
        {
            await transport.ConnectAsync();
            await forwarding.StartLocalTunnelAsync(row.Rule);
            var status = forwarding.GetLocalTunnelStatus(row.Rule.Id);
            if (status.State != LocalTunnelState.Active) Release(row);
            SetStatus(row, status);
        }
        catch (OperationCanceledException)
        {
            Release(row);
            SetStatus(row, default);
        }
        catch (Exception ex)
        {
            Release(row);
            SetStatus(row, new(LocalTunnelState.Failed, ex.Message));
        }
    }

    private async Task DisconnectAsync(LocalTunnelRow row)
    {
        if (!_connections.TryGetValue(row.Rule.Id, out var vm)) return;
        SetStatus(row, new(LocalTunnelState.Stopping));
        try
        {
            var forwarding = (ILocalTunnelConnection)vm.CreateTunnelTransport();
            await forwarding.StopLocalTunnelAsync(row.Rule.Id);
        }
        catch (Exception ex)
        {
            // The connection is closed below anyway; the listener goes with it.
            System.Diagnostics.Debug.WriteLine($"[Tunnels] Stopping {row.Rule.Name} failed: {ex.Message}");
        }
        finally
        {
            Release(row);
            SetStatus(row, default);
        }
    }

    private void Release(LocalTunnelRow row)
    {
        if (_connections.Remove(row.Rule.Id, out var connection))
        {
            connection.Dispose();
            row.EndTraffic(DateTimeOffset.Now);
        }
        UpdateSampler();
    }

    // ── Loading ──────────────────────────────────────────────────────────

    private async Task LoadAsync(string? selectedId)
    {
        if (_store is null) return;
        try
        {
            var saved = await _store.LoadAllLocalTunnelsAsync();
            var profiles = (_terminals?.Sessions ?? []).ToDictionary(p => p.Id);
            var oldRows = Tunnels.ToDictionary(row => row.Rule.Id);
            Tunnels.Clear();
            foreach (var rule in saved.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                if (!profiles.TryGetValue(rule.SessionId, out var profile)) continue;

                // Running tunnels keep their row (and state); everything else shows the saved values.
                Tunnels.Add(_connections.ContainsKey(rule.Id) && oldRows.TryGetValue(rule.Id, out var row)
                    ? row
                    : new LocalTunnelRow(rule, profile));
            }

            // Tunnels whose profile was deleted (their rules went with it) are disconnected.
            var kept = Tunnels.Select(t => t.Rule.Id).ToHashSet();
            foreach (var orphan in oldRows.Values.Where(r => !kept.Contains(r.Rule.Id)))
                Release(orphan);

            Selected = Tunnels.FirstOrDefault(row => row.Rule.Id == selectedId) ?? Selected switch
            {
                { } previous => Tunnels.FirstOrDefault(row => row.Rule.Id == previous.Rule.Id),
                _ => null
            } ?? Tunnels.FirstOrDefault();
            LoadError = null;
        }
        catch (Exception ex)
        {
            LoadError = $"The saved tunnels could not be loaded: {ex.Message}";
        }
        NotifyCommands();
    }

    private async Task ShowErrorAsync(string title, string message)
    {
        if (Host is { } owner)
            await DialogService.ShowMessageAsync(owner, title, message);
    }

    public void Dispose()
    {
        _sampler.Stop();
        foreach (var connection in _connections.Values)
            connection.Dispose();
        _connections.Clear();
    }
}
