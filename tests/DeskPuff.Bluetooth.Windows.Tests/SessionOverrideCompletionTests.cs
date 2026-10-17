using DeskPuff.Bluetooth.Windows.Protocol;
using DeskPuff.Core.Safety;
using DeskPuff.Core.Sessions;

namespace DeskPuff.Bluetooth.Windows.Tests;

public sealed partial class ThcProbeTests
{
    [TestMethod]
    public async Task NaturalCompletion_ReloadsVerifiedSlotThroughLiveClientContractWithoutEditingIt()
    {
        RecordingLog log = new();
        ProbeTransport transport = new(log) { AllowModeCommand = true };
        LoraxDeviceClient client = new(transport, log);
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await controller.ConnectAsync(new("test", "PUFFCO PEAK", -40), CancellationToken.None);
        byte[] originalTemperature = transport.Values[LoraxPaths.ProfileTemperature(0)].ToArray();
        byte[] originalTime = transport.Values[LoraxPaths.ProfileTime(0)].ToArray();
        await controller.ApplySessionOverrideAsync(OverrideRequest(), CancellationToken.None);
        transport.Values[LoraxPaths.OperatingState] = [8];
        await controller.RefreshAsync(CancellationToken.None);

        transport.Values[LoraxPaths.OperatingState] = [5];
        await controller.RefreshAsync(CancellationToken.None);

        CollectionAssert.AreEqual(new[]
        {
            LoraxPaths.ActiveProfileTime, LoraxPaths.ActiveProfileTemperature, LoraxPaths.ActiveProfile,
        }, transport.Writes.Select(write => write.Path).ToArray());
        CollectionAssert.AreEqual(originalTemperature, transport.Values[LoraxPaths.ProfileTemperature(0)]);
        CollectionAssert.AreEqual(originalTime, transport.Values[LoraxPaths.ProfileTime(0)]);
        CollectionAssert.AreEqual(originalTemperature, transport.Values[LoraxPaths.ActiveProfileTemperature]);
        CollectionAssert.AreEqual(originalTime, transport.Values[LoraxPaths.ActiveProfileTime]);
        Assert.IsNull(client.AppliedSessionOverride);
        StringAssert.Contains(log.Text, "clear=verified; both THC parameters match the slot");
        AssertNoHeat(log);
    }
}
