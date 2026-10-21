using DeskPuff.Bluetooth.Windows.Protocol;
using DeskPuff.Core.Devices;

namespace DeskPuff.Bluetooth.Windows.Tests;

[TestClass]
public sealed class RuntimeVaporWireTests
{
    [TestMethod]
    [DataRow(SessionVaporMode.Smooth, "00000000")]
    [DataRow(SessionVaporMode.Bold, "0000003F")]
    [DataRow(SessionVaporMode.Intense, "0000803F")]
    [DataRow(SessionVaporMode.Extreme, "0000C03F")]
    public void ExplicitModesHaveObservedPayloads(SessionVaporMode mode, string expected) =>
        Assert.AreEqual(expected, Convert.ToHexString(RuntimeVaporCodec.Encode(mode)));

    [TestMethod]
    public void ActualPaddedLegacyRevisionRetainsVerifiedIdentityWithoutAliasingOtherFormats()
    {
        Assert.AreEqual("AN", LoraxValueCodec.ReadFirmwareIdentity([0x27, 0, 0, 0]));
        Assert.AreEqual("AN", LoraxValueCodec.ReadFirmwareIdentity([0x27]));
        Assert.AreEqual("Unknown", LoraxValueCodec.ReadFirmwareIdentity([0x27, 1, 0, 0]));
        Assert.AreEqual("AW-1d90ae1", LoraxValueCodec.ReadFirmwareIdentity("AW-1d90ae1"u8));
    }

    [TestMethod]
    public void LegacyOrdinalsAreNotSessionModeChoices()
    {
        SessionOverride legacy = new("old", 220, TimeSpan.FromSeconds(60), VaporLevel.Max, []);
        Assert.IsNull(legacy.VaporMode);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => RuntimeVaporCodec.Encode((SessionVaporMode)99));
    }
}
