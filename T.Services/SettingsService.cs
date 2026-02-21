using System.Text.Json;
using T.Abstractions;
using T.Models;

namespace T.Services;

public class SettingsService : ISettingsService
{
    private readonly string _settingsPath;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private AppSettings _current = new();

    /// <summary>
    /// Globale Settings-Instanz, von überall zugreifbar
    /// </summary>
    public AppSettings Current
    {
        get => _current;
        private set
        {
            _current = value;
            SettingsChanged?.Invoke(_current);
        }
    }

    public event Action<AppSettings>? SettingsChanged;

    public SettingsService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var appFolder = Path.Combine(appData, "T");
        Directory.CreateDirectory(appFolder);
        _settingsPath = Path.Combine(appFolder, "settings.json");
        Load();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                Current = JsonSerializer.Deserialize<AppSettings>(json, _jsonOptions) ?? new();
            }
            else
            {
                Current = new AppSettings();
                Save();
            }
        }
        catch
        {
            Current = new AppSettings();
            Save();
        }
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(Current, _jsonOptions);
            File.WriteAllText(_settingsPath, json);
            SettingsChanged?.Invoke(Current);
        }
        catch { }
    }

    public async Task SaveAsync()
    {
        try
        {
            var json = JsonSerializer.Serialize(Current, _jsonOptions);
            await File.WriteAllTextAsync(_settingsPath, json);
            SettingsChanged?.Invoke(Current);
        }
        catch { }
    }
}