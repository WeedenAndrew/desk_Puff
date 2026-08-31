using DeskPuff.Core.Devices;

namespace DeskPuff.Bluetooth.Windows.Compatibility;

internal static class CompatibilityCatalog
{
    private static readonly Dictionary<uint, string> PeakProModels =
        new Dictionary<uint, string>
        {
            [0] = "Peak Pro",
            [1] = "Opal Peak Pro",
            [2] = "Indiglow Peak Pro",
            [4] = "Guardian Peak Pro",
            [12] = "Pearl Peak Pro",
            [13] = "Onyx Peak Pro",
            [15] = "Desert Peak Pro",
            [17] = "Flourish Peak Pro",
            [19] = "Storm Peak Pro",
            [23] = "Daybreak Peak Pro",
            [uint.MaxValue] = "Peak Pro",
        };

    // A firmware appears here only after completing docs/SAFETY.md, and every
    // entry has to name the evidence that admitted it.
    //
    // Peak Pro model 13, firmware AN, is the sole entry. Admitted 2026-08-28 on
    // the strength of a repeatable read-only session against a PEAKSHI V2 on
    // 2026-08-27/28: scan, bond, authenticate, every device and profile read,
    // live state tracking and clean disconnect, all correct and repeatable.
    //
    // "AN" is not a typo for 39. LoraxValueCodec.RevisionNumberToString encodes
    // the firmware byte as an Excel-style column, so 39 becomes "AN". An entry
    // keyed on "39" would silently never match.
    private static readonly HashSet<(DeviceFamily Family, uint Model, string Firmware)>
        HardwareVerifiedFirmware =
        [
            (DeviceFamily.PeakPro, 13u, "AN"),
        ];

    // These limits were stated by the device's owner from the Puffco app interface
    // on 2026-08-31. They are NOT read from the device. Lorax GetLimits (0x02)
    // returned 125, 40, and 8 as u16 values, which do not describe these thermal
    // limits, so their wire location remains unknown. The +15 F boost value is a
    // temperature delta, not an absolute temperature ceiling.
    //
    // Device reads corroborate the owner-stated envelope without establishing it:
    // profile temperatures were 450, 420, 500, and 480 F; durations were 90 and
    // 120 seconds; temperature boost was +5 F; and time boost was 10 seconds.
    private static readonly Dictionary<
        (DeviceFamily Family, uint Model, string Firmware),
        DeviceLimits> HardwareVerifiedLimits =
        new Dictionary<(DeviceFamily Family, uint Model, string Firmware), DeviceLimits>
        {
            [(DeviceFamily.PeakPro, 13u, "AN")] = new(
                MinimumTemperatureCelsius: (400 - 32) * 5.0 / 9.0,
                MaximumTemperatureCelsius: (600 - 32) * 5.0 / 9.0,
                MinimumDuration: TimeSpan.FromSeconds(30),
                MaximumDuration: TimeSpan.FromMinutes(2),
                MaximumBoostTemperatureCelsius: 15 * 5.0 / 9.0,
                MaximumBoostDuration: TimeSpan.FromSeconds(30)),
        };

    internal static DeviceIdentity Identify(
        string advertisedName,
        string deviceName,
        uint modelCode,
        string firmware,
        string? serialNumber)
    {
        DeviceFamily family;
        string resolvedName;

        if (PeakProModels.TryGetValue(modelCode, out string? peakName))
        {
            family = DeviceFamily.PeakPro;
            resolvedName = string.IsNullOrWhiteSpace(deviceName) ? peakName : deviceName;
        }
        else if (advertisedName.Contains("PROXY", StringComparison.OrdinalIgnoreCase) ||
                 deviceName.Contains("PROXY", StringComparison.OrdinalIgnoreCase))
        {
            family = DeviceFamily.NewProxy;
            resolvedName = string.IsNullOrWhiteSpace(deviceName) ? "New Proxy" : deviceName;
        }
        else
        {
            family = DeviceFamily.Unknown;
            resolvedName = string.IsNullOrWhiteSpace(deviceName) ? advertisedName : deviceName;
        }

        return new DeviceIdentity(family, resolvedName, modelCode, firmware, serialNumber);
    }

    internal static bool IsHardwareVerified(DeviceIdentity identity) =>
        HardwareVerifiedFirmware.Contains((identity.Family, identity.ModelCode, identity.FirmwareVersion));

    internal static DeviceCapabilities CapabilitiesFor(DeviceIdentity identity) =>
        identity.Family switch
        {
            DeviceFamily.PeakPro => new(true, true, true, true, 4),
            DeviceFamily.NewProxy => new(true, false, true, true, 4),
            _ => new(false, false, false, false, 0),
        };

    internal static DeviceLimits? LimitsFor(DeviceIdentity identity) =>
        HardwareVerifiedLimits.TryGetValue(
            (identity.Family, identity.ModelCode, identity.FirmwareVersion),
            out DeviceLimits? limits)
                ? limits
                : null;
}
