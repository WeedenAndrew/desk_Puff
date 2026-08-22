using DeskPuff.Core.Devices;
using DeskPuff.Core.Profiles;

namespace DeskPuff.Core.Tests;

[TestClass]
public sealed class LocalProfileLibraryTests
{
    private static readonly string[] AuroraColors = ["#581CFF", "#20DCE5", "#6BFF8F"];
    private static readonly string[] OceanColors = ["#2878FF", "#39DCE2"];

    private string rootPath = string.Empty;
    private LocalProfileLibrary library = null!;

    [TestInitialize]
    public void Initialize()
    {
        rootPath = Path.Combine(Path.GetTempPath(), "desk-puff-profile-tests", Guid.NewGuid().ToString("N"));
        library = new LocalProfileLibrary(rootPath);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(rootPath))
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [TestMethod]
    public async Task ColorProfiles_HaveNoArtificialCountLimit()
    {
        for (int index = 0; index < 20; index++)
        {
            await library.SaveColorAsync(
                new LocalColorProfile
                {
                    Name = $"Color {index}",
                    Colors = [$"#{index:X6}"],
                },
                existingFileName: null,
                CancellationToken.None);
        }

        IReadOnlyList<StoredLocalProfile<LocalColorProfile>> profiles =
            await library.LoadColorsAsync(CancellationToken.None);

        Assert.HasCount(20, profiles);
        Assert.IsTrue(Directory.Exists(Path.Combine(rootPath, "colors")));
    }

    [TestMethod]
    public async Task HeatingProfile_RoundTripsAndDeletesInsideItsOwnFolder()
    {
        StoredLocalProfile<LocalHeatingProfile> saved = await library.SaveHeatingAsync(
            SafeHeatingProfile(),
            existingFileName: null,
            CancellationToken.None);

        IReadOnlyList<StoredLocalProfile<LocalHeatingProfile>> loaded =
            await library.LoadHeatingAsync(CancellationToken.None);
        Assert.HasCount(1, loaded);
        Assert.AreEqual("Daily", loaded[0].Profile.Name);
        Assert.AreEqual("Ocean", loaded[0].Profile.ColorProfileName);
        CollectionAssert.AreEqual(
            OceanColors,
            loaded[0].Profile.Colors);

        await library.DeleteHeatingAsync(saved.FileName, CancellationToken.None);
        Assert.IsEmpty(await library.LoadHeatingAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task ExistingFilename_CannotEscapeTheProfileFolder()
    {
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => library.SaveColorAsync(
            new LocalColorProfile { Name = "Blue", Colors = ["#2878FF"] },
            $"..{Path.DirectorySeparatorChar}escape.json",
            CancellationToken.None));

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            library.DeleteColorAsync(
                $"..{Path.DirectorySeparatorChar}escape.json",
                CancellationToken.None));
    }

    [TestMethod]
    public async Task Loader_SkipsMalformedAndOversizedManualFiles()
    {
        string directory = Path.Combine(rootPath, "colors");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "malformed.json"), "{ not-json");
        await File.WriteAllBytesAsync(
            Path.Combine(directory, "oversized.json"),
            new byte[(16 * 1024) + 1]);

        Assert.IsEmpty(await library.LoadColorsAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task Loader_AcceptsAValidManuallyWrittenColorProfile()
    {
        string directory = Path.Combine(rootPath, "colors");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "aurora.json"),
            """
            {
              "schemaVersion": 1,
              "name": "Aurora",
              "colors": ["#581CFF", "#20DCE5", "#6BFF8F"]
            }
            """);

        IReadOnlyList<StoredLocalProfile<LocalColorProfile>> profiles =
            await library.LoadColorsAsync(CancellationToken.None);

        Assert.HasCount(1, profiles);
        Assert.AreEqual("Aurora", profiles[0].Profile.Name);
        CollectionAssert.AreEqual(
            AuroraColors,
            profiles[0].Profile.Colors);
    }

    private static LocalHeatingProfile SafeHeatingProfile() => new()
    {
        Name = "Daily",
        DeviceProfileName = "BLUE",
        TargetTemperatureCelsius = 260,
        DurationSeconds = 40,
        Vapor = VaporLevel.Standard,
        BoostTemperatureCelsius = 5,
        BoostDurationSeconds = 10,
        ColorProfileName = "Ocean",
        Colors = OceanColors,
    };
}
