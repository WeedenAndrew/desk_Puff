using DeskPuff.Core.Devices;
using DeskPuff.Core.Profiles;

namespace DeskPuff.Core.Tests;

[TestClass]
public sealed class NamedVaporJsonTests
{
    [TestMethod]
    public async Task ExplicitModeRoundTripsWithoutReinterpretingLegacyField()
    {
        string root = Path.Combine(Path.GetTempPath(), "desk-puff-named-vapor", Guid.NewGuid().ToString("N"));
        try
        {
            LocalProfileLibrary library = new(root);
            LocalHeatingProfile original = new()
            {
                Name = "test",
                DeviceProfileName = "test",
                TargetTemperatureCelsius = 220,
                DurationSeconds = 60,
                Vapor = VaporLevel.Max,
                VaporMode = SessionVaporMode.Intense,
                BoostTemperatureCelsius = 5,
                BoostDurationSeconds = 10,
                ColorProfileName = "blue",
                Colors = ["#0000FF"],
            };
            await library.SaveHeatingAsync(original, null, CancellationToken.None);
            LocalHeatingProfile loaded = (await library.LoadHeatingAsync(CancellationToken.None))[0].Profile;
            Assert.AreEqual(VaporLevel.Max, loaded.Vapor);
            Assert.AreEqual(SessionVaporMode.Intense, loaded.VaporMode);
            await library.SaveHeatingAsync(original with { Name = "legacy", VaporMode = null }, null, CancellationToken.None);
            Assert.IsNull((await library.LoadHeatingAsync(CancellationToken.None)).Single(profile => profile.Profile.Name == "legacy").Profile.VaporMode);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => library.SaveHeatingAsync(original with { VaporMode = (SessionVaporMode)99 }, null, CancellationToken.None));
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, true); } }
    }
}
