using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using T.Models;

namespace T.Services;

public static class SettingsService
{
    private static readonly string SettingsPath;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Globale Settings-Instanz, von überall zugreifbar
    /// </summary>
    public static AppSettings Current { get; private set; } = new();

    static SettingsService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var appFolder = Path.Combine(appData, "T");
        Directory.CreateDirectory(appFolder);
        SettingsPath = Path.Combine(appFolder, "settings.json");
    }

    public static void Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                Current = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new();
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

    public static void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(Current, JsonOptions);
            File.WriteAllText(SettingsPath, json);
        }
        catch { }
    }

    public static async Task SaveAsync()
    {
        try
        {
            var json = JsonSerializer.Serialize(Current, JsonOptions);
            await File.WriteAllTextAsync(SettingsPath, json);
        }
        catch { }
    }
}