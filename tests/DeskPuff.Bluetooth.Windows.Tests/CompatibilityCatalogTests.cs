using DeskPuff.Bluetooth.Windows.Compatibility;
using DeskPuff.Core.Devices;

namespace DeskPuff.Bluetooth.Windows.Tests;

[TestClass]
public sealed class CompatibilityCatalogTests
{
    [TestMethod]
    public void KnownPeakProModelCode_IsRecognized()
    {
        DeviceIdentity identity = CompatibilityCatalog.Identify(
            "PUFFCO PEAK",
            string.Empty,
            13,
            "AG",
            null);

        Assert.AreEqual(DeviceFamily.PeakPro, identity.Family);
        Assert.AreEqual("Onyx Peak Pro", identity.Name);
    }

    [TestMethod]
    public void ProductCode_IsNotMistakenForHardwareModelCode()
    {
        DeviceIdentity identity = CompatibilityCatalog.Identify(
            "UNKNOWN",
            string.Empty,
            71,
            "AG",
            null);

        Assert.AreEqual(DeviceFamily.Unknown, identity.Family);
    }

    [TestMethod]
    public void HardwareVerifiedAllowlist_RejectsDifferentFirmware()
    {
        DeviceIdentity identity = new(DeviceFamily.PeakPro, "Test", 13, "AG", null);

        Assert.IsFalse(CompatibilityCatalog.IsHardwareVerified(identity));
    }

    [TestMethod]
    public void HardwareVerifiedAllowlist_MatchesOnlyPeakProModel13FirmwareAn()
    {
        DeviceIdentity verified = new(DeviceFamily.PeakPro, "PEAKSHI V2", 13, "AN", null);

        Assert.IsTrue(CompatibilityCatalog.IsHardwareVerified(verified));
        Assert.IsFalse(CompatibilityCatalog.IsHardwareVerified(verified with { ModelCode = 12 }));
        Assert.IsFalse(CompatibilityCatalog.IsHardwareVerified(verified with { FirmwareVersion = "AO" }));
        Assert.IsFalse(CompatibilityCatalog.IsHardwareVerified(verified with { Family = DeviceFamily.NewProxy }));
    }

    [TestMethod]
    public void CharacterisedPeakProLimits_AreSaneAndMatchOwnerStatedAppValues()
    {
        DeviceIdentity verified = new(DeviceFamily.PeakPro, "PEAKSHI V2", 13, "AN", null);

        DeviceLimits? limits = CompatibilityCatalog.LimitsFor(verified);

        Assert.IsNotNull(limits);
        Assert.IsTrue(limits.IsSane, "Characterised limits must pass the safety model's sanity gate.");
        Assert.AreEqual((400 - 32) * 5.0 / 9.0, limits.MinimumTemperatureCelsius, 0.000001);
        Assert.AreEqual((600 - 32) * 5.0 / 9.0, limits.MaximumTemperatureCelsius, 0.000001);
        Assert.AreEqual(TimeSpan.FromSeconds(30), limits.MinimumDuration);
        Assert.AreEqual(TimeSpan.FromMinutes(2), limits.MaximumDuration);
        Assert.AreEqual(15 * 5.0 / 9.0, limits.MaximumBoostTemperatureCelsius, 0.000001);
        Assert.AreEqual(TimeSpan.FromSeconds(30), limits.MaximumBoostDuration);
    }

    [TestMethod]
    public void LimitsCatalog_ReturnsNullForEveryUncharacterisedTupleVariant()
    {
        DeviceIdentity verified = new(DeviceFamily.PeakPro, "PEAKSHI V2", 13, "AN", null);

        Assert.IsNull(CompatibilityCatalog.LimitsFor(verified with { Family = DeviceFamily.NewProxy }));
        Assert.IsNull(CompatibilityCatalog.LimitsFor(verified with { ModelCode = 12 }));
        Assert.IsNull(CompatibilityCatalog.LimitsFor(verified with { FirmwareVersion = "AO" }));
    }
}
