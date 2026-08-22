namespace DeskPuff.Core.Devices;

public enum DeviceFamily
{
    Unknown = 0,
    PeakPro = 1,
    NewProxy = 2,
}

public enum ChamberKind
{
    None = 0,
    Classic = 1,
    ThreeDXL = 2,
    ThreeD = 3,
    Unknown = 255,
}

public enum DeviceConnectionState
{
    Disconnected = 0,
    Scanning = 1,
    Connecting = 2,
    Authenticating = 3,
    ConnectedReadOnly = 4,
    ConnectedControlEnabled = 5,
    Faulted = 6,
}

public enum DeviceOperatingState
{
    Unknown = -1,
    InitializingMemory = 0,
    InitializingVersionDisplay = 1,
    InitializingBatteryDisplay = 2,
    PoweredOff = 3,
    Sleeping = 4,
    Idle = 5,
    SelectingTemperature = 6,
    Preheating = 7,
    Active = 8,
    Fading = 9,
    ShowingVersion = 10,
    ShowingBattery = 11,
    FactoryTest = 12,
    Bonding = 13,
}

public enum VaporLevel
{
    Standard = 0,
    High = 1,
    Max = 2,
    XL = 3,
}

public sealed record DeviceCandidate(
    string PlatformId,
    string Name,
    short SignalStrength);

public sealed record DeviceIdentity(
    DeviceFamily Family,
    string Name,
    uint ModelCode,
    string FirmwareVersion,
    string? SerialNumber);

public sealed record DeviceLimits(
    double MinimumTemperatureCelsius,
    double MaximumTemperatureCelsius,
    TimeSpan MinimumDuration,
    TimeSpan MaximumDuration,
    double MaximumBoostTemperatureCelsius,
    TimeSpan MaximumBoostDuration)
{
    public bool IsSane =>
        MinimumTemperatureCelsius is >= 100 and < 400 &&
        MaximumTemperatureCelsius is > 100 and <= 400 &&
        MinimumTemperatureCelsius < MaximumTemperatureCelsius &&
        MinimumDuration > TimeSpan.Zero &&
        MinimumDuration < MaximumDuration &&
        MaximumDuration <= TimeSpan.FromMinutes(5) &&
        MaximumBoostTemperatureCelsius is >= 0 and <= 30 &&
        MaximumBoostDuration >= TimeSpan.Zero &&
        MaximumBoostDuration <= TimeSpan.FromMinutes(2);
}

public sealed record DeviceCapabilities(
    bool SupportsVaporControl,
    bool SupportsThreeDXL,
    bool SupportsIndependentBoost,
    bool SupportsLighting,
    int MaximumConsecutiveBoosts);

public sealed record HeatProfile(
    int Index,
    string Name,
    double TargetTemperatureCelsius,
    TimeSpan Duration,
    VaporLevel Vapor,
    double BoostTemperatureCelsius,
    TimeSpan BoostDuration,
    string ColorHex)
{
    public IReadOnlyList<string> ColorPalette { get; init; } = [ColorHex];

    public bool HasDeviceColor { get; init; }
}

public sealed record DeviceSnapshot(
    DeviceConnectionState ConnectionState,
    DeviceIdentity? Identity,
    DeviceLimits? Limits,
    DeviceCapabilities? Capabilities,
    ChamberKind Chamber,
    DeviceOperatingState OperatingState,
    int ActiveProfileIndex,
    string ActiveProfileName,
    VaporLevel Vapor,
    double BatteryPercent,
    bool IsCharging,
    double TargetTemperatureCelsius,
    double? CurrentTemperatureCelsius,
    TimeSpan SessionTotal,
    TimeSpan SessionElapsed,
    bool IsAuthenticated,
    bool IsFirmwareVerified,
    string? Fault)
{
    public TimeSpan SessionRemaining =>
        SessionTotal > SessionElapsed ? SessionTotal - SessionElapsed : TimeSpan.Zero;

    public bool IsHeating => OperatingState is
        DeviceOperatingState.Preheating or
        DeviceOperatingState.Active or
        DeviceOperatingState.Fading;

    public static DeviceSnapshot Disconnected { get; } = new(
        DeviceConnectionState.Disconnected,
        null,
        null,
        null,
        ChamberKind.None,
        DeviceOperatingState.Unknown,
        0,
        "No profile",
        VaporLevel.Standard,
        0,
        false,
        0,
        null,
        TimeSpan.Zero,
        TimeSpan.Zero,
        false,
        false,
        null);
}
