using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskPuff.App.Infrastructure;

internal sealed record SavedColorPalettePreference
{
    public string Name { get; init; } = string.Empty;

    public string[] Colors { get; init; } = [];
}

internal sealed record SavedHeatingProfilePreference
{
    public string Name { get; init; } = string.Empty;

    public string DeviceProfileName { get; init; } = string.Empty;

    public double TargetTemperatureCelsius { get; init; }

    public double DurationSeconds { get; init; }

    public string Vapor { get; init; } = string.Empty;

    public double BoostTemperatureCelsius { get; init; }

    public double BoostDurationSeconds { get; init; }

    public string ColorProfileName { get; init; } = string.Empty;

    public string[] Colors { get; init; } = [];
}

internal sealed record UserPreferences
{
    public string AppAccentHex { get; init; } = "#BB376A";

    public string PreviousProfileKey { get; init; } = "Left";

    public string NextProfileKey { get; init; } = "Right";

    public string TemperatureBoostKey { get; init; } = "Up";

    public string TimeBoostKey { get; init; } = "Down";

    public double QuickHitTemperatureCelsius { get; init; } = 5;

    public double QuickHitTimeSeconds { get; init; } = 10;

    public Dictionary<int, string> ProfileMacros { get; init; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SavedColorPalettePreference[]? SavedColorPalettes { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SavedHeatingProfilePreference[]? SavedHeatingProfiles { get; init; }
}

internal static class UserPreferencesStore
{
    private const long MaximumPreferencesBytes = 16 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    public static async Task<UserPreferences> LoadAsync(CancellationToken cancellationToken)
    {
        string path = PreferencesPath();
        try
        {
            FileInfo file = new(path);
            if (!file.Exists || file.Length is <= 0 or > MaximumPreferencesBytes)
            {
                return new UserPreferences();
            }

            await using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);
            return await JsonSerializer.DeserializeAsync<UserPreferences>(
                stream,
                SerializerOptions,
                cancellationToken) ?? new UserPreferences();
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            JsonException)
        {
            return new UserPreferences();
        }
    }

    public static async Task SaveAsync(
        UserPreferences preferences,
        CancellationToken cancellationToken)
    {
        string path = PreferencesPath();
        string directory = Path.GetDirectoryName(path) ??
            throw new InvalidOperationException("The preferences path has no directory.");
        Directory.CreateDirectory(directory);

        string temporaryPath = Path.Combine(directory, $"{Path.GetRandomFileName()}.tmp");
        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    preferences,
                    SerializerOptions,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
            }
        }
    }

    private static string PreferencesPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "desk_Puff",
        "preferences.json");
}
