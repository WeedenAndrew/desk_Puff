using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeskPuff.Core.Devices;

namespace DeskPuff.Core.Profiles;

public sealed record LocalColorProfile
{
    public int SchemaVersion { get; init; } = 1;

    public string Name { get; init; } = string.Empty;

    public string[] Colors { get; init; } = [];
}

public sealed record LocalHeatingProfile
{
    public int SchemaVersion { get; init; } = 1;

    public string Name { get; init; } = string.Empty;

    public string DeviceProfileName { get; init; } = string.Empty;

    public double TargetTemperatureCelsius { get; init; }

    public double DurationSeconds { get; init; }

    public VaporLevel Vapor { get; init; }

    public double BoostTemperatureCelsius { get; init; }

    public double BoostDurationSeconds { get; init; }

    public string ColorProfileName { get; init; } = string.Empty;

    public string[] Colors { get; init; } = [];
}

public sealed record StoredLocalProfile<T>(string FileName, T Profile);

public sealed class LocalProfileLibrary(string rootPath)
{
    private const int MaximumDocumentBytes = 16 * 1024;
    private const int MaximumNameLength = 64;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly string colorDirectory = Path.Combine(
        Path.GetFullPath(rootPath),
        "colors");
    private readonly string heatingDirectory = Path.Combine(
        Path.GetFullPath(rootPath),
        "heating");

    public static string DefaultRootPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "desk_Puff",
        "profiles");

    public Task<IReadOnlyList<StoredLocalProfile<LocalColorProfile>>> LoadColorsAsync(
        CancellationToken cancellationToken) =>
        LoadAsync<LocalColorProfile>(colorDirectory, IsValid, cancellationToken);

    public Task<IReadOnlyList<StoredLocalProfile<LocalHeatingProfile>>> LoadHeatingAsync(
        CancellationToken cancellationToken) =>
        LoadAsync<LocalHeatingProfile>(heatingDirectory, IsValid, cancellationToken);

    public Task<StoredLocalProfile<LocalColorProfile>> SaveColorAsync(
        LocalColorProfile profile,
        string? existingFileName,
        CancellationToken cancellationToken)
    {
        if (!IsValid(profile))
        {
            throw new InvalidDataException("The local color profile is invalid.");
        }

        return SaveAsync(colorDirectory, profile.Name, profile, existingFileName, cancellationToken);
    }

    public Task<StoredLocalProfile<LocalHeatingProfile>> SaveHeatingAsync(
        LocalHeatingProfile profile,
        string? existingFileName,
        CancellationToken cancellationToken)
    {
        if (!IsValid(profile))
        {
            throw new InvalidDataException("The local heating profile is invalid.");
        }

        return SaveAsync(heatingDirectory, profile.Name, profile, existingFileName, cancellationToken);
    }

    public Task DeleteColorAsync(string fileName, CancellationToken cancellationToken) =>
        DeleteAsync(colorDirectory, fileName, cancellationToken);

    public Task DeleteHeatingAsync(string fileName, CancellationToken cancellationToken) =>
        DeleteAsync(heatingDirectory, fileName, cancellationToken);

    private static async Task<IReadOnlyList<StoredLocalProfile<T>>> LoadAsync<T>(
        string directory,
        Func<T, bool> validator,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        List<StoredLocalProfile<T>> profiles = [];
        foreach (string path in Directory
                     .EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                FileInfo file = new(path);
                if (file.Length is <= 0 or > MaximumDocumentBytes ||
                    file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                await using FileStream stream = new(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 4096,
                    useAsync: true);
                T? profile = await JsonSerializer.DeserializeAsync<T>(
                    stream,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                if (profile is not null && validator(profile))
                {
                    profiles.Add(new StoredLocalProfile<T>(file.Name, profile));
                }
            }
            catch (Exception exception) when (exception is
                IOException or
                UnauthorizedAccessException or
                JsonException)
            {
            }
        }

        return profiles;
    }

    private static async Task<StoredLocalProfile<T>> SaveAsync<T>(
        string directory,
        string profileName,
        T profile,
        string? existingFileName,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        string fileName = ResolveFileName(profileName, existingFileName);
        string destinationPath = ResolveContainedPath(directory, fileName);
        if (File.Exists(destinationPath) &&
            File.GetAttributes(destinationPath).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("Local profile links cannot be overwritten.");
        }

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
                    profile,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (stream.Length > MaximumDocumentBytes)
                {
                    throw new InvalidDataException("The local profile document is too large.");
                }
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
            return new StoredLocalProfile<T>(fileName, profile);
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

    private static Task DeleteAsync(
        string directory,
        string fileName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = ResolveContainedPath(directory, fileName);
        if (File.Exists(path))
        {
            FileInfo file = new(path);
            if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new IOException("Local profile links cannot be deleted by desk_Puff.");
            }

            file.Delete();
        }

        return Task.CompletedTask;
    }

    private static string ResolveFileName(string profileName, string? existingFileName)
    {
        if (!string.IsNullOrWhiteSpace(existingFileName))
        {
            string existing = Path.GetFileName(existingFileName);
            if (!string.Equals(existing, existingFileName, StringComparison.Ordinal) ||
                !string.Equals(Path.GetExtension(existing), ".json", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The local profile filename is invalid.");
            }

            return existing;
        }

        string normalizedName = profileName.Trim().Normalize(NormalizationForm.FormKC);
        StringBuilder slug = new();
        foreach (char character in normalizedName)
        {
            if (char.IsLetterOrDigit(character) || character is '-' or '_')
            {
                slug.Append(character);
            }
            else if (char.IsWhiteSpace(character))
            {
                slug.Append('-');
            }

            if (slug.Length == 48)
            {
                break;
            }
        }

        if (slug.Length == 0)
        {
            slug.Append("profile");
        }

        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedName)))[..12]
            .ToLowerInvariant();
        return $"{slug}-{hash}.json";
    }

    private static string ResolveContainedPath(string directory, string fileName)
    {
        string root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(directory, fileName));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The local profile path escaped its profile folder.");
        }

        return path;
    }

    private static bool IsValid(LocalColorProfile profile) =>
        profile.SchemaVersion == 1 &&
        IsName(profile.Name) &&
        IsColors(profile.Colors);

    private static bool IsValid(LocalHeatingProfile profile) =>
        profile.SchemaVersion == 1 &&
        IsName(profile.Name) &&
        !string.IsNullOrWhiteSpace(profile.DeviceProfileName) &&
        profile.DeviceProfileName.Length <= 31 &&
        IsName(profile.ColorProfileName) &&
        double.IsFinite(profile.TargetTemperatureCelsius) &&
        profile.TargetTemperatureCelsius is >= 190 and <= 327 &&
        double.IsFinite(profile.DurationSeconds) &&
        profile.DurationSeconds is >= 10 and <= 120 &&
        Enum.IsDefined(profile.Vapor) &&
        double.IsFinite(profile.BoostTemperatureCelsius) &&
        profile.BoostTemperatureCelsius is >= 0 and <= 30 &&
        double.IsFinite(profile.BoostDurationSeconds) &&
        profile.BoostDurationSeconds is >= 0 and <= 120 &&
        IsColors(profile.Colors);

    private static bool IsName(string name) =>
        !string.IsNullOrWhiteSpace(name) &&
        name.Trim().Length <= MaximumNameLength &&
        !name.Any(char.IsControl);

    private static bool IsColors(string[] colors) =>
        colors is { Length: >= 1 and <= 4 } && colors.All(IsColor);

    private static bool IsColor(string color) =>
        color is { Length: 7 } &&
        color[0] == '#' &&
        uint.TryParse(
            color.AsSpan(1),
            NumberStyles.AllowHexSpecifier,
            CultureInfo.InvariantCulture,
            out _);
}
