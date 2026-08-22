using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using DeskPuff.Core.Devices;

namespace DeskPuff.Core.Safety;

public enum DeviceAction
{
    Read = 0,
    SelectProfile = 1,
    UpdateProfile = 2,
    StartSession = 3,
    StopSession = 4,
    BoostTemperature = 5,
    BoostTime = 6,
    SetStealthMode = 7,
    SetLanternMode = 8,
}

public readonly record struct SafetyDecision(bool IsAllowed, string Reason)
{
    public static SafetyDecision Allow() => new(true, string.Empty);

    public static SafetyDecision Deny(string reason) => new(false, reason);

    public void ThrowIfDenied()
    {
        if (!IsAllowed)
        {
            throw new DeviceSafetyException(Reason);
        }
    }
}

public sealed class DeviceSafetyException(string message) : InvalidOperationException(message);

public sealed class DeviceSafetyPolicy
{
    private const double AbsoluteMinimumTemperatureCelsius = 190;
    private const double AbsoluteMaximumTemperatureCelsius = 327;
    private static readonly TimeSpan AbsoluteMinimumDuration = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan AbsoluteMaximumDuration = TimeSpan.FromMinutes(2);
    private static readonly SearchValues<char> HexCharacters =
        SearchValues.Create("0123456789abcdefABCDEF");

    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "The policy is an injected service and intentionally has instance semantics.")]
    public SafetyDecision Evaluate(DeviceAction action, DeviceSnapshot snapshot)
    {
        if (action == DeviceAction.Read)
        {
            return snapshot.ConnectionState == DeviceConnectionState.Disconnected
                ? SafetyDecision.Deny("The device is disconnected.")
                : SafetyDecision.Allow();
        }

        if (!snapshot.IsAuthenticated)
        {
            return SafetyDecision.Deny("Device writes require an authenticated connection.");
        }

        if (!snapshot.IsFirmwareVerified)
        {
            return SafetyDecision.Deny("This firmware has not passed hardware safety verification.");
        }

        if (snapshot.ConnectionState != DeviceConnectionState.ConnectedControlEnabled)
        {
            return SafetyDecision.Deny("The connection is read-only or unavailable.");
        }

        if (snapshot.Identity?.Family is not (DeviceFamily.PeakPro or DeviceFamily.NewProxy))
        {
            return SafetyDecision.Deny("The connected device is not an allowlisted app-enabled model.");
        }

        if (snapshot.Limits is not { IsSane: true })
        {
            return SafetyDecision.Deny("Verified device limits are unavailable.");
        }

        return action switch
        {
            DeviceAction.StartSession => EvaluateStart(snapshot),
            DeviceAction.StopSession => EvaluateStop(snapshot),
            DeviceAction.BoostTemperature or DeviceAction.BoostTime => EvaluateBoost(snapshot),
            DeviceAction.SelectProfile or DeviceAction.UpdateProfile => snapshot.IsHeating
                ? SafetyDecision.Deny("Profiles cannot change during a heat cycle.")
                : SafetyDecision.Allow(),
            DeviceAction.SetStealthMode or DeviceAction.SetLanternMode => SafetyDecision.Allow(),
            _ => SafetyDecision.Deny("The requested operation is not allowlisted."),
        };
    }

    public SafetyDecision ValidateProfile(HeatProfile profile, DeviceSnapshot snapshot)
    {
        SafetyDecision operation = Evaluate(DeviceAction.UpdateProfile, snapshot);
        if (!operation.IsAllowed)
        {
            return operation;
        }

        return ValidateProfileConfiguration(profile, snapshot.Limits, snapshot.Chamber);
    }

    public static SafetyDecision ValidateProfileConfiguration(
        HeatProfile profile,
        DeviceLimits? deviceLimits,
        ChamberKind chamber)
    {
        if (deviceLimits is not { IsSane: true } limits)
        {
            return SafetyDecision.Deny("Verified device limits are unavailable.");
        }

        double minimumTemperature = Math.Max(
            limits.MinimumTemperatureCelsius,
            AbsoluteMinimumTemperatureCelsius);
        double maximumTemperature = Math.Min(
            limits.MaximumTemperatureCelsius,
            AbsoluteMaximumTemperatureCelsius);
        TimeSpan minimumDuration = limits.MinimumDuration > AbsoluteMinimumDuration
            ? limits.MinimumDuration
            : AbsoluteMinimumDuration;
        TimeSpan maximumDuration = limits.MaximumDuration < AbsoluteMaximumDuration
            ? limits.MaximumDuration
            : AbsoluteMaximumDuration;

        if (profile.Index is < 0 or > 3)
        {
            return SafetyDecision.Deny("Profile index must be between 0 and 3.");
        }

        if (string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 31)
        {
            return SafetyDecision.Deny("Profile names must contain 1 to 31 characters.");
        }

        if (!double.IsFinite(profile.TargetTemperatureCelsius) ||
            profile.TargetTemperatureCelsius < minimumTemperature ||
            profile.TargetTemperatureCelsius > maximumTemperature)
        {
            return SafetyDecision.Deny("Profile temperature is outside verified safe limits.");
        }

        if (profile.Duration < minimumDuration || profile.Duration > maximumDuration)
        {
            return SafetyDecision.Deny("Profile duration is outside verified safe limits.");
        }

        if (!double.IsFinite(profile.BoostTemperatureCelsius) ||
            profile.BoostTemperatureCelsius < 0 ||
            profile.BoostTemperatureCelsius > limits.MaximumBoostTemperatureCelsius)
        {
            return SafetyDecision.Deny("Temperature boost is outside verified safe limits.");
        }

        if (profile.BoostDuration < TimeSpan.Zero ||
            profile.BoostDuration > limits.MaximumBoostDuration)
        {
            return SafetyDecision.Deny("Time boost is outside verified safe limits.");
        }

        if (!IsColor(profile.ColorHex))
        {
            return SafetyDecision.Deny("Profile color must be a six-digit RGB value.");
        }

        if (profile.ColorPalette.Count is < 1 or > 4 ||
            !profile.ColorPalette.All(IsColor) ||
            !string.Equals(profile.ColorHex, profile.ColorPalette[0], StringComparison.OrdinalIgnoreCase))
        {
            return SafetyDecision.Deny("Profile palettes require one to four RGB colors with the primary color first.");
        }

        if (profile.Vapor == VaporLevel.XL && chamber != ChamberKind.ThreeDXL)
        {
            return SafetyDecision.Deny("XL vapor requires a detected 3DXL chamber.");
        }

        return SafetyDecision.Allow();
    }

    public SafetyDecision ValidateProfileSelection(int profileIndex, DeviceSnapshot snapshot)
    {
        SafetyDecision operation = Evaluate(DeviceAction.SelectProfile, snapshot);
        if (!operation.IsAllowed)
        {
            return operation;
        }

        return profileIndex is >= 0 and <= 3
            ? SafetyDecision.Allow()
            : SafetyDecision.Deny("Profile index must be between 0 and 3.");
    }

    public SafetyDecision ValidateBoost(
        DeviceAction action,
        DeviceSnapshot snapshot,
        int boostsAlreadyApplied)
    {
        if (action is not (DeviceAction.BoostTemperature or DeviceAction.BoostTime))
        {
            return SafetyDecision.Deny("The requested operation is not a boost.");
        }

        SafetyDecision operation = Evaluate(action, snapshot);
        if (!operation.IsAllowed)
        {
            return operation;
        }

        if (boostsAlreadyApplied < 0)
        {
            return SafetyDecision.Deny("The boost counter is invalid.");
        }

        int deviceLimit = snapshot.Capabilities!.MaximumConsecutiveBoosts;
        const int applicationLimit = 4;
        int allowedBoosts = Math.Min(deviceLimit, applicationLimit);
        return allowedBoosts > 0 && boostsAlreadyApplied < allowedBoosts
            ? SafetyDecision.Allow()
            : SafetyDecision.Deny("The safe per-session boost limit has been reached.");
    }

    public static SafetyDecision ValidateBoostConfiguration(
        double temperatureCelsius,
        TimeSpan duration,
        DeviceLimits? limits)
    {
        if (limits is not { IsSane: true })
        {
            return SafetyDecision.Deny("Verified device limits are unavailable.");
        }

        if (!double.IsFinite(temperatureCelsius) ||
            temperatureCelsius <= 0 ||
            temperatureCelsius > limits.MaximumBoostTemperatureCelsius)
        {
            return SafetyDecision.Deny("The quick-hit temperature increase is outside verified limits.");
        }

        if (duration <= TimeSpan.Zero || duration > limits.MaximumBoostDuration)
        {
            return SafetyDecision.Deny("The quick-hit time increase is outside verified limits.");
        }

        return SafetyDecision.Allow();
    }

    public SafetyDecision ValidateTemperatureBoost(
        DeviceSnapshot snapshot,
        int boostsAlreadyApplied,
        double temperatureCelsius)
    {
        SafetyDecision operation = ValidateBoost(
            DeviceAction.BoostTemperature,
            snapshot,
            boostsAlreadyApplied);
        if (!operation.IsAllowed)
        {
            return operation;
        }

        DeviceLimits limits = snapshot.Limits!;
        if (!double.IsFinite(temperatureCelsius) ||
            temperatureCelsius <= 0 ||
            temperatureCelsius > limits.MaximumBoostTemperatureCelsius)
        {
            return SafetyDecision.Deny("The requested temperature increase is outside verified limits.");
        }

        double maximumTemperature = Math.Min(
            limits.MaximumTemperatureCelsius,
            AbsoluteMaximumTemperatureCelsius);
        return snapshot.TargetTemperatureCelsius + temperatureCelsius <= maximumTemperature
            ? SafetyDecision.Allow()
            : SafetyDecision.Deny("The requested temperature increase would exceed the verified target limit.");
    }

    public SafetyDecision ValidateTimeBoost(
        DeviceSnapshot snapshot,
        int boostsAlreadyApplied,
        TimeSpan duration)
    {
        SafetyDecision operation = ValidateBoost(
            DeviceAction.BoostTime,
            snapshot,
            boostsAlreadyApplied);
        if (!operation.IsAllowed)
        {
            return operation;
        }

        DeviceLimits limits = snapshot.Limits!;
        if (duration <= TimeSpan.Zero || duration > limits.MaximumBoostDuration)
        {
            return SafetyDecision.Deny("The requested time increase is outside verified limits.");
        }

        TimeSpan maximumDuration = limits.MaximumDuration < AbsoluteMaximumDuration
            ? limits.MaximumDuration
            : AbsoluteMaximumDuration;
        return snapshot.SessionTotal + duration <= maximumDuration
            ? SafetyDecision.Allow()
            : SafetyDecision.Deny("The requested time increase would exceed the verified session limit.");
    }

    private static SafetyDecision EvaluateStart(DeviceSnapshot snapshot)
    {
        if (snapshot.Chamber is ChamberKind.None or ChamberKind.Unknown)
        {
            return SafetyDecision.Deny("A recognized chamber must be attached before heating.");
        }

        if (snapshot.OperatingState != DeviceOperatingState.Idle)
        {
            return SafetyDecision.Deny("Heating can start only from the idle state.");
        }

        if (snapshot.CurrentTemperatureCelsius is not { } currentTemperature ||
            !double.IsFinite(currentTemperature) ||
            currentTemperature is < -20 or > AbsoluteMaximumTemperatureCelsius)
        {
            return SafetyDecision.Deny("The chamber temperature sensor is unavailable or invalid.");
        }

        return SafetyDecision.Allow();
    }

    private static SafetyDecision EvaluateStop(DeviceSnapshot snapshot) =>
        snapshot.IsHeating
            ? SafetyDecision.Allow()
            : SafetyDecision.Deny("There is no active heat cycle to stop.");

    private static SafetyDecision EvaluateBoost(DeviceSnapshot snapshot)
    {
        if (snapshot.OperatingState != DeviceOperatingState.Active)
        {
            return SafetyDecision.Deny("Boost is available only during an active session.");
        }

        return snapshot.Capabilities?.SupportsIndependentBoost == true
            ? SafetyDecision.Allow()
            : SafetyDecision.Deny("Independent boost is not verified for this device.");
    }

    private static bool IsColor(string color) =>
        color.Length == 7 &&
        color[0] == '#' &&
        color.AsSpan(1).IndexOfAnyExcept(HexCharacters) < 0;
}
