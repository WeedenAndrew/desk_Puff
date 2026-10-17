using DeskPuff.Core.Devices;
using DeskPuff.Core.Safety;

namespace DeskPuff.Core.Tests;

[TestClass]
public sealed class SessionOverrideSafetyTests
{
    private readonly DeviceSafetyPolicy policy = new();

    [TestMethod]
    [DataRow(209.999)]
    [DataRow(300.001)]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    [DataRow(double.NegativeInfinity)]
    public void TemperatureOutsideDeviceEnvelopeOrNonFinite_IsRefused(double temperature)
    {
        SafetyDecision decision = policy.ValidateSessionOverride(
            ValidOverride() with { TargetTemperatureCelsius = temperature }, ValidSnapshot());

        AssertDenied(decision, "Session override temperature is outside verified safe limits.");
    }

    [TestMethod]
    [DataRow(29.999)]
    [DataRow(100.001)]
    [DataRow(-1)]
    public void DurationOutsideDeviceEnvelope_IsRefused(double seconds)
    {
        SafetyDecision decision = policy.ValidateSessionOverride(
            ValidOverride() with { Duration = TimeSpan.FromSeconds(seconds) }, ValidSnapshot());

        AssertDenied(decision, "Session override duration is outside verified safe limits.");
    }

    [TestMethod]
    [DataRow(VaporLevel.High)]
    [DataRow(VaporLevel.Max)]
    [DataRow(VaporLevel.XL)]
    [DataRow((VaporLevel)999)]
    public void NonStandardVapor_IsExplicitlyRefusedEvenWithThreeDxl(VaporLevel vapor)
    {
        AssertDenied(policy.ValidateSessionOverride(ValidOverride() with { Vapor = vapor }, ValidSnapshot()),
            "Session override vapor must be Standard; no session-scoped vapor control is known.");
    }

    [TestMethod]
    public void XlOnThreeDxl_IsDeliberatelyAllowedInSlotButNotSessionOverride()
    {
        DeviceSnapshot snapshot = ValidSnapshot();
        SessionOverride requested = ValidOverride() with { Vapor = VaporLevel.XL };

        Assert.IsTrue(policy.ValidateProfile(ProfileFor(requested), snapshot).IsAllowed,
            "The slot's existing 3DXL/XL rule must not be weakened or changed.");
        AssertDenied(policy.ValidateSessionOverride(requested, snapshot),
            "Session override vapor must be Standard; no session-scoped vapor control is known.");
    }

    [TestMethod]
    [DataRow("red")]
    [DataRow("#12GG56")]
    [DataRow("123456")]
    [DataRow("#12345678")]
    [DataRow("")]
    [DataRow(null)]
    public void MalformedDisplayColour_IsRefused(string? colour)
    {
        AssertDenied(policy.ValidateSessionOverride(ValidOverride() with { Colors = ["#123456", colour!] }, ValidSnapshot()),
            "Session override colours must be six-digit RGB values.");
    }

    [TestMethod]
    public void MissingColourCollection_IsRefusedAsInvalidFormat()
    {
        AssertDenied(policy.ValidateSessionOverride(ValidOverride() with { Colors = null! }, ValidSnapshot()),
            "Session override colours must be six-digit RGB values.");
    }

    [TestMethod]
    public void DisplayColours_HaveNoSlotCountLimit_AndAreNotChanged()
    {
        string[] colours = Enumerable.Repeat("#aB12fF", 25).ToArray();
        string[] original = colours.ToArray();
        SessionOverride requested = ValidOverride() with { Colors = colours };

        Assert.IsTrue(policy.ValidateSessionOverride(requested, ValidSnapshot()).IsAllowed);
        CollectionAssert.AreEqual(original, colours, "Validation must not modify the local display palette.");
        Assert.AreSame(colours, requested.Colors);
        Assert.IsTrue(policy.ValidateSessionOverride(requested with { Colors = [] }, ValidSnapshot()).IsAllowed,
            "No colours is valid for a display-only list; do not import the slot's minimum palette count.");
    }

    [TestMethod]
    public void NameIsNotSubjectToDeviceSlotConstraints()
    {
        foreach (string name in new[] { string.Empty, " ", new string('n', 100) })
        {
            Assert.IsTrue(policy.ValidateSessionOverride(ValidOverride() with { Name = name }, ValidSnapshot()).IsAllowed,
                "The session name is local display text and is never written.");
        }
    }

    [TestMethod]
    public void HotterThanActiveSlot_IsAllowed_ButAboveEnvelopeIsRefused()
    {
        DeviceSnapshot snapshot = ValidSnapshot() with { TargetTemperatureCelsius = 215 };
        SessionOverride hotter = ValidOverride() with { TargetTemperatureCelsius = 280 };

        Assert.IsTrue(hotter.TargetTemperatureCelsius > snapshot.TargetTemperatureCelsius);
        Assert.IsTrue(policy.ValidateSessionOverride(hotter, snapshot).IsAllowed,
            "Policy permits upward overrides within the envelope; this is NOT hardware validation of upward writes.");
        AssertDenied(policy.ValidateSessionOverride(hotter with { TargetTemperatureCelsius = 301 }, snapshot),
            "Session override temperature is outside verified safe limits.");
        Assert.IsTrue(policy.ValidateSessionOverride(hotter with { TargetTemperatureCelsius = 210 }, snapshot).IsAllowed,
            "The active slot is not a lower bound either.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ProfileAndSessionEnvelopesAgree_AtBothBoundariesAndJustOutside(bool absoluteBoundsBind)
    {
        DeviceSnapshot snapshot = ValidSnapshot();
        if (absoluteBoundsBind)
        {
            snapshot = snapshot with
            {
                Limits = snapshot.Limits! with
                {
                    MinimumTemperatureCelsius = 180,
                    MaximumTemperatureCelsius = 340,
                    MinimumDuration = TimeSpan.FromSeconds(5),
                    MaximumDuration = TimeSpan.FromMinutes(3),
                },
            };
        }
        // Explicit expected values, not a copy of the production bound formula.
        double minimum = absoluteBoundsBind ? 190 : 210;
        double maximum = absoluteBoundsBind ? 327 : 300;
        TimeSpan shortest = TimeSpan.FromSeconds(absoluteBoundsBind ? 10 : 30);
        TimeSpan longest = TimeSpan.FromSeconds(absoluteBoundsBind ? 120 : 100);
        (double Temperature, TimeSpan Duration, bool Allowed)[] cases =
        [
            (minimum, shortest, true),
            (maximum, longest, true),
            (minimum - 0.001, shortest, false),
            (maximum + 0.001, shortest, false),
            (minimum, shortest - TimeSpan.FromTicks(1), false),
            (minimum, longest + TimeSpan.FromTicks(1), false),
        ];

        foreach ((double temperature, TimeSpan duration, bool allowed) in cases)
        {
            SessionOverride requested = ValidOverride() with { TargetTemperatureCelsius = temperature, Duration = duration };
            SafetyDecision slot = DeviceSafetyPolicy.ValidateProfileConfiguration(ProfileFor(requested), snapshot.Limits, snapshot.Chamber);
            SafetyDecision session = policy.ValidateSessionOverride(requested, snapshot);
            Assert.AreEqual(allowed, slot.IsAllowed, $"Slot boundary: {temperature}, {duration}; {slot.Reason}");
            Assert.AreEqual(allowed, session.IsAllowed, $"Session boundary: {temperature}, {duration}; {session.Reason}");
            Assert.AreEqual(slot.IsAllowed, session.IsAllowed, "Profile and session envelope drifted.");
        }
    }

    [TestMethod]
    public void ApplySessionOverride_HasOwnIdleGate_NotUpdateProfileGate()
    {
        foreach (DeviceOperatingState state in Enum.GetValues<DeviceOperatingState>().Append((DeviceOperatingState)999))
        {
            DeviceSnapshot snapshot = ValidSnapshot() with { OperatingState = state };
            SafetyDecision evaluation = policy.Evaluate(DeviceAction.ApplySessionOverride, snapshot);
            SafetyDecision validation = policy.ValidateSessionOverride(ValidOverride(), snapshot);
            if (state == DeviceOperatingState.Idle)
            {
                Assert.IsTrue(evaluation.IsAllowed);
                Assert.IsTrue(validation.IsAllowed);
            }
            else
            {
                AssertDenied(evaluation, "Session overrides can be applied only from the idle state.");
                Assert.AreEqual(evaluation, validation);
            }
        }
        DeviceSnapshot sleeping = ValidSnapshot() with { OperatingState = DeviceOperatingState.Sleeping };
        Assert.IsTrue(policy.Evaluate(DeviceAction.UpdateProfile, sleeping).IsAllowed,
            "Existing UpdateProfile behavior stays unchanged; it is not the override gate.");
    }

    [TestMethod]
    [DataRow(ChamberKind.None)]
    [DataRow(ChamberKind.Unknown)]
    [DataRow((ChamberKind)123)]
    public void UnrecognisedChamber_IsRefused(ChamberKind chamber)
    {
        DeviceSnapshot snapshot = ValidSnapshot() with { Chamber = chamber };
        AssertDenied(policy.Evaluate(DeviceAction.ApplySessionOverride, snapshot),
            "A recognized chamber must be attached before applying a session override.");
        AssertDenied(policy.ValidateSessionOverride(ValidOverride(), snapshot),
            "A recognized chamber must be attached before applying a session override.");
    }

    [TestMethod]
    [DataRow(ChamberKind.Classic)]
    [DataRow(ChamberKind.ThreeD)]
    [DataRow(ChamberKind.ThreeDXL)]
    public void RecognisedIdleChamber_PassesOverrideGate(ChamberKind chamber)
    {
        Assert.IsTrue(policy.ValidateSessionOverride(ValidOverride(), ValidSnapshot() with { Chamber = chamber }).IsAllowed);
    }

    [TestMethod]
    public void CommonPrerequisitesAreEnforced_NoFallbackForMissingOrInvalidLimits()
    {
        DeviceSnapshot safe = ValidSnapshot();
        (DeviceSnapshot Snapshot, string Reason)[] cases =
        [
            (safe with { IsAuthenticated = false }, "Device writes require an authenticated connection."),
            (safe with { IsFirmwareVerified = false }, "This firmware has not passed hardware safety verification."),
            (safe with { ConnectionState = DeviceConnectionState.ConnectedReadOnly }, "The connection is read-only or unavailable."),
            (safe with { Identity = safe.Identity! with { Family = DeviceFamily.Unknown } }, "The connected device is not an allowlisted app-enabled model."),
            (safe with { Limits = null }, "Verified device limits are unavailable."),
            (safe with { Limits = safe.Limits! with { MaximumDuration = TimeSpan.FromMinutes(10) } }, "Verified device limits are unavailable."),
        ];
        foreach ((DeviceSnapshot snapshot, string reason) in cases)
        {
            AssertDenied(policy.Evaluate(DeviceAction.ApplySessionOverride, snapshot), reason);
            AssertDenied(policy.ValidateSessionOverride(ValidOverride(), snapshot), reason);
        }
    }

    private static void AssertDenied(SafetyDecision decision, string reason)
    {
        Assert.IsFalse(decision.IsAllowed);
        Assert.AreEqual(reason, decision.Reason);
    }

    private static SessionOverride ValidOverride() => new("Local saved profile", 260, TimeSpan.FromSeconds(60), VaporLevel.Standard, ["#007AFF"]);

    private static HeatProfile ProfileFor(SessionOverride requested) => new(
        0, "Slot", requested.TargetTemperatureCelsius, requested.Duration, requested.Vapor, 0, TimeSpan.Zero, "#007AFF");

    // Synthetic device limits intentionally tighter than the absolute envelope.
    // These tests establish policy behavior, not firmware compatibility or wire behavior.
    private static DeviceSnapshot ValidSnapshot() => DeviceSnapshot.Disconnected with
    {
        ConnectionState = DeviceConnectionState.ConnectedControlEnabled,
        Identity = new DeviceIdentity(DeviceFamily.PeakPro, "Synthetic device", 0, "test", null),
        Limits = new DeviceLimits(210, 300, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(100), 15, TimeSpan.FromSeconds(30)),
        Chamber = ChamberKind.ThreeDXL,
        OperatingState = DeviceOperatingState.Idle,
        TargetTemperatureCelsius = 215,
        IsAuthenticated = true,
        IsFirmwareVerified = true,
    };
}
