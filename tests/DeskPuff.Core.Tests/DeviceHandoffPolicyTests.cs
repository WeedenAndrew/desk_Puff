using DeskPuff.Core.Devices;
using DeskPuff.Core.Safety;

namespace DeskPuff.Core.Tests;

[TestClass]
public sealed class DeviceHandoffPolicyTests
{
    [TestMethod]
    public void Source_IsBlocked_DuringHeat()
    {
        DeviceSnapshot snapshot = SafeSnapshot() with
        {
            OperatingState = DeviceOperatingState.Preheating,
        };

        Assert.IsFalse(DeviceHandoffPolicy.EvaluateSource(snapshot).IsAllowed);
    }

    [TestMethod]
    public void Source_IsAllowed_WhenAuthenticatedAndIdle()
    {
        Assert.IsTrue(DeviceHandoffPolicy.EvaluateSource(SafeSnapshot()).IsAllowed);
    }

    [TestMethod]
    public void Candidate_RejectsProxyAndNonErigNames()
    {
        Assert.IsFalse(DeviceHandoffPolicy.EvaluateCandidate(new(1, "New Proxy", -40)).IsAllowed);
        Assert.IsFalse(DeviceHandoffPolicy.EvaluateCandidate(new(2, "Headphones", -40)).IsAllowed);
        Assert.IsTrue(DeviceHandoffPolicy.EvaluateCandidate(new(3, "Puffco Peak Pro", -40)).IsAllowed);
    }

    [TestMethod]
    public void Destination_MustAuthenticateAsPeakPro()
    {
        DeviceSnapshot proxy = SafeSnapshot() with
        {
            Identity = new(DeviceFamily.NewProxy, "Proxy", 0, "1.0", null),
        };
        DeviceSnapshot unauthenticated = SafeSnapshot() with { IsAuthenticated = false };

        Assert.IsFalse(DeviceHandoffPolicy.EvaluateDestination(proxy).IsAllowed);
        Assert.IsFalse(DeviceHandoffPolicy.EvaluateDestination(unauthenticated).IsAllowed);
        Assert.IsTrue(DeviceHandoffPolicy.EvaluateDestination(SafeSnapshot()).IsAllowed);
    }

    private static DeviceSnapshot SafeSnapshot() => new(
        DeviceConnectionState.ConnectedControlEnabled,
        new DeviceIdentity(DeviceFamily.PeakPro, "Peak", 0, "1.0", null),
        new DeviceLimits(190, 315, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(2), 20, TimeSpan.FromSeconds(30)),
        new DeviceCapabilities(true, true, true, true, 4),
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
}
