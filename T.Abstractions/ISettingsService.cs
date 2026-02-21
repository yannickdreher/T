using T.Models;

namespace T.Abstractions;

/// <summary>
/// DI-injectable settings service.
/// Lives in T because AppSettings aggregates TerminalSettings (Avalonia.Media dep).
/// </summary>
public interface ISettingsService
{
    AppSettings Current { get; }
    event Action<AppSettings>? SettingsChanged;
    void Save();
    Task SaveAsync();
}