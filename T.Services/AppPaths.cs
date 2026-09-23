namespace T.Services;

/// <summary>
/// Location of the application data (settings, session database, master key).
/// Defaults to %APPDATA%/T (Windows) or ~/.config/T; the environment variable
/// T_DATA_DIR overrides it (portable installations, testing).
/// </summary>
public static class AppPaths
{
    public const string DataDirectoryVariable = "T_DATA_DIR";

    public static string DataDirectory
    {
        get
        {
            var overridePath = Environment.GetEnvironmentVariable(DataDirectoryVariable);
            return !string.IsNullOrWhiteSpace(overridePath)
                ? Path.GetFullPath(Environment.ExpandEnvironmentVariables(overridePath))
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "T");
        }
    }
}
