using System.Text.Json;
using System.Text.Json.Serialization;

namespace HomeworkValidator.App.Services;

/// <summary>The connection details the student used last. The password is never saved.</summary>
public sealed class AppSettings
{
    public string? Server { get; set; }
    public bool UseWindowsAuthentication { get; set; } = OperatingSystem.IsWindows();
    public string? User { get; set; }

    private static string FilePath => Path.Combine(AppDataFolder.Root, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize(File.ReadAllText(FilePath), AppJsonContext.Default.AppSettings) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A damaged settings file shouldn't stop the app; start from defaults.
            return new AppSettings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(AppDataFolder.Root);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, AppJsonContext.Default.AppSettings));
    }
}

/// <summary>Where the app keeps settings and saved answers. HV_APP_DATA overrides it (used by tests).</summary>
public static class AppDataFolder
{
    public static string Root =>
        Environment.GetEnvironmentVariable("HV_APP_DATA") is { Length: > 0 } overridden
            ? overridden
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HomeworkValidator");
}

// Source-generated JSON, so serialization keeps working when the app is trimmed for publishing.
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(Dictionary<string, QuestionStatus>))]
internal sealed partial class AppJsonContext : JsonSerializerContext;
