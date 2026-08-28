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
}
