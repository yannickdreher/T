using System.Diagnostics;
using System.Text.Json;
using T.Abstractions;
using T.Models;

namespace T.Services;

public sealed class SettingsService : ISettingsService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _settingsPath;
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    /// <summary>
    /// Application wide settings instance. Child objects are mutated in place
    /// (see SettingsDialogViewModel.ApplyTo) so bindings stay intact.
    /// </summary>
    public AppSettings Current { get; private set; }

    public event Action<AppSettings>? SettingsChanged;

    public SettingsService()
        : this(Path.Combine(AppPaths.DataDirectory, "settings.json"))
    {
    }

    public SettingsService(string settingsPath)
    {
        _settingsPath = settingsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        Current = Load();
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath), JsonOptions);
                if (settings != null)
                    return Sanitize(settings);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Keep the broken file for inspection instead of overwriting it silently.
            Debug.WriteLine($"[SettingsService] Could not read settings: {ex.Message}");
            try { File.Copy(_settingsPath, _settingsPath + ".bak", overwrite: true); }
            catch (IOException) { }
        }

        var defaults = new AppSettings();
        TryWrite(defaults);
        return defaults;
    }

    /// <summary>Clamps values that would break the app when the file was edited by hand.</summary>
    private static AppSettings Sanitize(AppSettings settings)
    {
        settings.General ??= new GeneralSettings();
        settings.Update ??= new UpdateSettings();
        settings.Explorer ??= new ExplorerSettings();
        settings.Terminal ??= new TerminalSettings();

        var g = settings.General;
        g.ConnectionTimeout = Math.Clamp(g.ConnectionTimeout, 5, 120);
        g.KeepAliveInterval = Math.Clamp(g.KeepAliveInterval, 10, 120);
        g.LastOpenSessionIds ??= [];

        var t = settings.Terminal;
        t.TerminalFontSize = Math.Clamp(t.TerminalFontSize, 6, 72);
        t.ScrollbackLines = Math.Clamp(t.ScrollbackLines, 1000, 100_000);
        t.TerminalPadding = Math.Clamp(t.TerminalPadding, 0, 32);

        // Profiles are validated again before step runs; here only nulls are removed.
        var step = settings.Step ??= new StepSettings();
        step.ExecutablePath ??= "";
        step.Profiles = [.. (step.Profiles ?? []).OfType<StepCaProfile>().Select(SanitizeProfile)
            .DistinctBy(p => p.Id, StringComparer.OrdinalIgnoreCase)];
        return settings;
    }

    private static StepCaProfile SanitizeProfile(StepCaProfile profile)
    {
        profile.Id ??= "";
        profile.Name ??= "";
        profile.Identity ??= "";
        profile.CaUrl ??= "";
        profile.RootCertificatePath ??= "";
        profile.Context ??= "";
        profile.Provisioner ??= "";
        profile.Principals ??= "";
        return profile;
    }

    public void Save()
    {
        _saveLock.Wait();
        try { TryWrite(Current); }
        finally { _saveLock.Release(); }
        SettingsChanged?.Invoke(Current);
    }

    public async Task SaveAsync()
    {
        await _saveLock.WaitAsync();
        try
        {
            var json = JsonSerializer.Serialize(Current, JsonOptions);
            var tempPath = _settingsPath + ".tmp";
            await File.WriteAllTextAsync(tempPath, json);
            File.Move(tempPath, _settingsPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[SettingsService] Could not save settings: {ex.Message}");
        }
        finally
        {
            _saveLock.Release();
        }
        SettingsChanged?.Invoke(Current);
    }

    public void Dispose() => _saveLock.Dispose();

    /// <summary>Writes atomically (temp file + rename) so a crash never leaves a truncated file.</summary>
    private void TryWrite(AppSettings settings)
    {
        try
        {
            var tempPath = _settingsPath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(tempPath, _settingsPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[SettingsService] Could not save settings: {ex.Message}");
        }
    }
}
