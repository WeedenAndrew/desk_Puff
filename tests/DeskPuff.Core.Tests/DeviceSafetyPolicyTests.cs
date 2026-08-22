using DeskPuff.Core.Devices;
using DeskPuff.Core.Safety;

namespace DeskPuff.Core.Tests;

[TestClass]
public sealed class DeviceSafetyPolicyTests
{
    private readonly DeviceSafetyPolicy policy = new();

    [TestMethod]
    public void Read_IsBlocked_WhenDisconnected()
    {
        SafetyDecision decision = policy.Evaluate(DeviceAction.Read, DeviceSnapshot.Disconnected);

        Assert.IsFalse(decision.IsAllowed);
    }

    [TestMethod]
    public void EveryWrite_IsBlocked_WhenAuthenticationIsMissing()
    {
        DeviceSnapshot snapshot = SafeSnapshot() with { IsAuthenticated = false };

        foreach (DeviceAction action in WriteActions())
        {
            SafetyDecision decision = policy.Evaluate(action, snapshot);
            Assert.IsFalse(decision.IsAllowed, $"{action} unexpectedly passed authentication gating.");
        }
    }

    [TestMethod]
    public void EveryWrite_IsBlocked_WhenFirmwareIsUnverified()
    {
        DeviceSnapshot snapshot = SafeSnapshot() with { IsFirmwareVerified = false };

        foreach (DeviceAction action in WriteActions())
        {
            SafetyDecision decision = policy.Evaluate(action, snapshot);
            Assert.IsFalse(decision.IsAllowed, $"{action} unexpectedly passed firmware gating.");
        }
    }

    [TestMethod]
    public void EveryWrite_IsBlocked_WhenConnectionIsReadOnly()
    {
        DeviceSnapshot snapshot = SafeSnapshot() with
        {
            ConnectionState = DeviceConnectionState.ConnectedReadOnly,
        };

        foreach (DeviceAction action in WriteActions())
        {
            SafetyDecision decision = policy.Evaluate(action, snapshot);
            Assert.IsFalse(decision.IsAllowed, $"{action} unexpectedly passed read-only gating.");
        }
    }

    [TestMethod]
    public void EveryWrite_IsBlocked_ForUnknownDeviceFamily()
    {
        DeviceSnapshot snapshot = SafeSnapshot() with
        {
            Identity = SafeSnapshot().Identity! with { Family = DeviceFamily.Unknown },
        };

        foreach (DeviceAction action in WriteActions())
        {
            Assert.IsFalse(
                policy.Evaluate(action, snapshot).IsAllowed,
                $"{action} unexpectedly passed model gating.");
        }
    }

    [TestMethod]
    public void EveryWrite_IsBlocked_WhenDeviceLimitsAreNotSane()
    {
        DeviceSnapshot snapshot = SafeSnapshot() with
        {
            Limits = SafeSnapshot().Limits! with { MaximumDuration = TimeSpan.FromMinutes(10) },
        };

        foreach (DeviceAction action in WriteActions())
        {
            Assert.IsFalse(
                policy.Evaluate(action, snapshot).IsAllowed,
                $"{action} unexpectedly passed limit sanity gating.");
        }
    }

    [TestMethod]
    public void Start_IsBlocked_WithoutRecognizedChamber()
    {
        DeviceSnapshot snapshot = SafeSnapshot() with { Chamber = ChamberKind.Unknown };

        SafetyDecision decision = policy.Evaluate(DeviceAction.StartSession, snapshot);

        Assert.IsFalse(decision.IsAllowed);
    }

    [TestMethod]
    public void Start_IsBlocked_OutsideIdleState()
    {
        DeviceSnapshot snapshot = SafeSnapshot() with
        {
            OperatingState = DeviceOperatingState.Preheating,
        };

        SafetyDecision decision = policy.Evaluate(DeviceAction.StartSession, snapshot);

        Assert.IsFalse(decision.IsAllowed);
    }

    [TestMethod]
    public void Start_IsBlocked_WhenTemperatureSensorIsInvalid()
    {
        DeviceSnapshot snapshot = SafeSnapshot() with { CurrentTemperatureCelsius = double.NaN };

        SafetyDecision decision = policy.Evaluate(DeviceAction.StartSession, snapshot);

        Assert.IsFalse(decision.IsAllowed);
    }

    [TestMethod]
    public void Start_IsAllowed_OnlyForFullyVerifiedIdleDevice()
    {
        SafetyDecision decision = policy.Evaluate(DeviceAction.StartSession, SafeSnapshot());

        Assert.IsTrue(decision.IsAllowed, decision.Reason);
    }

    [TestMethod]
    public void Stop_IsAllowed_OnlyWhileHeating()
    {
        SafetyDecision idle = policy.Evaluate(DeviceAction.StopSession, SafeSnapshot());
        SafetyDecision active = policy.Evaluate(
            DeviceAction.StopSession,
            SafeSnapshot() with { OperatingState = DeviceOperatingState.Active });

        Assert.IsFalse(idle.IsAllowed);
        Assert.IsTrue(active.IsAllowed, active.Reason);
    }

    [TestMethod]
    public void Boost_IsBlocked_UnlessActiveAndExplicitlySupported()
    {
        DeviceSnapshot idle = SafeSnapshot();
        DeviceSnapshot unsupported = SafeSnapshot() with
        {
            OperatingState = DeviceOperatingState.Active,
            Capabilities = SafeSnapshot().Capabilities! with { SupportsIndependentBoost = false },
        };
        DeviceSnapshot supported = SafeSnapshot() with
        {
            OperatingState = DeviceOperatingState.Active,
        };

        Assert.IsFalse(policy.Evaluate(DeviceAction.BoostTime, idle).IsAllowed);
        Assert.IsFalse(policy.Evaluate(DeviceAction.BoostTime, unsupported).IsAllowed);
        Assert.IsTrue(policy.Evaluate(DeviceAction.BoostTime, supported).IsAllowed);
    }

    [TestMethod]
    public void Boost_IsCappedAtFourPerSession()
    {
        DeviceSnapshot active = SafeSnapshot() with
        {
            OperatingState = DeviceOperatingState.Active,
            Capabilities = SafeSnapshot().Capabilities! with { MaximumConsecutiveBoosts = 10 },
        };

        Assert.IsTrue(policy.ValidateBoost(DeviceAction.BoostTime, active, 3).IsAllowed);
        Assert.IsFalse(policy.ValidateBoost(DeviceAction.BoostTime, active, 4).IsAllowed);
    }

    [TestMethod]
    public void ConfigurableBoostAmounts_MustStayWithinDeviceLimits()
    {
        DeviceLimits limits = SafeSnapshot().Limits!;

        Assert.IsTrue(DeviceSafetyPolicy.ValidateBoostConfiguration(
            5,
            TimeSpan.FromSeconds(10),
            limits).IsAllowed);
        Assert.IsFalse(DeviceSafetyPolicy.ValidateBoostConfiguration(
            0,
            TimeSpan.FromSeconds(10),
            limits).IsAllowed);
        Assert.IsFalse(DeviceSafetyPolicy.ValidateBoostConfiguration(
            5,
            TimeSpan.FromSeconds(31),
            limits).IsAllowed);
    }

    [TestMethod]
    public void TemperatureBoost_CannotRaiseTargetPastAbsoluteLimit()
    {
        DeviceSnapshot active = SafeSnapshot() with
        {
            OperatingState = DeviceOperatingState.Active,
            TargetTemperatureCelsius = 325,
        };

        Assert.IsFalse(policy.ValidateTemperatureBoost(active, 0, 5).IsAllowed);
        Assert.IsTrue(policy.ValidateTemperatureBoost(active, 0, 2).IsAllowed);
    }

    [TestMethod]
    public void TimeBoost_CannotRaiseSessionPastAbsoluteLimit()
    {
        DeviceSnapshot active = SafeSnapshot() with
        {
            OperatingState = DeviceOperatingState.Active,
            SessionTotal = TimeSpan.FromSeconds(115),
        };

        Assert.IsFalse(policy.ValidateTimeBoost(active, 0, TimeSpan.FromSeconds(10)).IsAllowed);
        Assert.IsTrue(policy.ValidateTimeBoost(active, 0, TimeSpan.FromSeconds(5)).IsAllowed);
    }

    [TestMethod]
    public void Profile_IsBlocked_OutsideConservativeTemperatureBounds()
    {
        HeatProfile tooCold = SafeProfile() with { TargetTemperatureCelsius = 189 };
        HeatProfile tooHot = SafeProfile() with { TargetTemperatureCelsius = 328 };

        Assert.IsFalse(policy.ValidateProfile(tooCold, SafeSnapshot()).IsAllowed);
        Assert.IsFalse(policy.ValidateProfile(tooHot, SafeSnapshot()).IsAllowed);
    }

    [TestMethod]
    public void Profile_IsBlocked_OutsideConservativeDurationBounds()
    {
        HeatProfile tooShort = SafeProfile() with { Duration = TimeSpan.FromSeconds(9) };
        HeatProfile tooLong = SafeProfile() with { Duration = TimeSpan.FromSeconds(121) };

        Assert.IsFalse(policy.ValidateProfile(tooShort, SafeSnapshot()).IsAllowed);
        Assert.IsFalse(policy.ValidateProfile(tooLong, SafeSnapshot()).IsAllowed);
    }

    [TestMethod]
    public void XlVapor_IsBlocked_WithoutThreeDxlChamber()
    {
        HeatProfile xlProfile = SafeProfile() with { Vapor = VaporLevel.XL };
        DeviceSnapshot threeD = SafeSnapshot() with { Chamber = ChamberKind.ThreeD };
        DeviceSnapshot threeDxl = SafeSnapshot() with { Chamber = ChamberKind.ThreeDXL };

        Assert.IsFalse(policy.ValidateProfile(xlProfile, threeD).IsAllowed);
        Assert.IsTrue(policy.ValidateProfile(xlProfile, threeDxl).IsAllowed);
    }

    [TestMethod]
    public void Profile_IsBlocked_WhileHeating()
    {
        DeviceSnapshot active = SafeSnapshot() with
        {
            OperatingState = DeviceOperatingState.Active,
        };

        Assert.IsFalse(policy.ValidateProfile(SafeProfile(), active).IsAllowed);
    }

    [TestMethod]
    public void Profile_AcceptsOnlyRgbHexColors()
    {
        Assert.IsTrue(policy.ValidateProfile(SafeProfile(), SafeSnapshot()).IsAllowed);
        Assert.IsFalse(policy.ValidateProfile(
            SafeProfile() with { ColorHex = "red", ColorPalette = ["red"] },
            SafeSnapshot()).IsAllowed);
        Assert.IsFalse(policy.ValidateProfile(
            SafeProfile() with { ColorHex = "#12GG56", ColorPalette = ["#12GG56"] },
            SafeSnapshot()).IsAllowed);
    }

    [TestMethod]
    public void ProfilePalette_IsBoundedAndPrimaryColorMustMatch()
    {
        Assert.IsTrue(policy.ValidateProfile(
            SafeProfile() with { ColorPalette = ["#007AFF", "#FFFFFF"] },
            SafeSnapshot()).IsAllowed);
        Assert.IsFalse(policy.ValidateProfile(
            SafeProfile() with { ColorPalette = [] },
            SafeSnapshot()).IsAllowed);
        Assert.IsFalse(policy.ValidateProfile(
            SafeProfile() with { ColorPalette = ["#FFFFFF"] },
            SafeSnapshot()).IsAllowed);
        Assert.IsFalse(policy.ValidateProfile(
            SafeProfile() with { ColorPalette = ["#007AFF", "#FFFFFF", "#F80B00", "#6EE916", "#101010"] },
            SafeSnapshot()).IsAllowed);
    }

    [TestMethod]
    public void LocalProfileConfiguration_UsesTheSameBoundedValidationWithoutAWriteState()
    {
        DeviceSnapshot disconnected = SafeSnapshot() with
        {
            ConnectionState = DeviceConnectionState.Disconnected,
            IsAuthenticated = false,
            IsFirmwareVerified = false,
        };

        Assert.IsTrue(DeviceSafetyPolicy.ValidateProfileConfiguration(
            SafeProfile(),
            disconnected.Limits,
            disconnected.Chamber).IsAllowed);
        Assert.IsFalse(policy.ValidateProfile(SafeProfile(), disconnected).IsAllowed);
    }

    [TestMethod]
    public void ProfileConfiguration_RejectsNonFiniteTemperatures()
    {
        DeviceSnapshot snapshot = SafeSnapshot();

        Assert.IsFalse(DeviceSafetyPolicy.ValidateProfileConfiguration(
            SafeProfile() with { TargetTemperatureCelsius = double.NaN },
            snapshot.Limits,
            snapshot.Chamber).IsAllowed);
        Assert.IsFalse(DeviceSafetyPolicy.ValidateProfileConfiguration(
            SafeProfile() with { BoostTemperatureCelsius = double.PositiveInfinity },
            snapshot.Limits,
            snapshot.Chamber).IsAllowed);
    }

    [TestMethod]
    public void ProfileSelection_AcceptsOnlyFourBoundedSlots()
    {
        DeviceSnapshot snapshot = SafeSnapshot();

        Assert.IsTrue(policy.ValidateProfileSelection(0, snapshot).IsAllowed);
        Assert.IsTrue(policy.ValidateProfileSelection(3, snapshot).IsAllowed);
        Assert.IsFalse(policy.ValidateProfileSelection(-1, snapshot).IsAllowed);
        Assert.IsFalse(policy.ValidateProfileSelection(4, snapshot).IsAllowed);
    }

    private static IEnumerable<DeviceAction> WriteActions() =>
        Enum.GetValues<DeviceAction>().Where(action => action != DeviceAction.Read);

    private static DeviceSnapshot SafeSnapshot() => new(
        DeviceConnectionState.ConnectedControlEnabled,
        new DeviceIdentity(DeviceFamily.PeakPro, "Test Peak", 0, "verified", "serial"),
        new DeviceLimits(
            180,
            340,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMinutes(3),
            20,
            TimeSpan.FromSeconds(30)),
        new DeviceCapabilities(true, true, true, true, 3),
        ChamberKind.ThreeDXL,
        DeviceOperatingState.Idle,
        0,
        "Blue",
        VaporLevel.Standard,
        80,
        false,
        260,
        25,
        TimeSpan.FromSeconds(40),
        TimeSpan.Zero,
        true,
        true,
        null);

    private static HeatProfile SafeProfile() => new(
        0,
        "Blue",
        260,
        TimeSpan.FromSeconds(40),
        VaporLevel.Standard,
        5,
        TimeSpan.FromSeconds(10),
        "#007AFF");
}
