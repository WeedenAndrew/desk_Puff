using DeskPuff.Bluetooth.Windows.Protocol;
using DeskPuff.Core.Devices;
using DeskPuff.Core.Safety;
using DeskPuff.Core.Sessions;

namespace DeskPuff.Bluetooth.Windows.Tests;

public sealed partial class ThcProbeTests
{
    [TestMethod]
    [DataRow(SessionVaporMode.Smooth, 0f)]
    [DataRow(SessionVaporMode.Bold, 0.5f)]
    [DataRow(SessionVaporMode.Intense, 1f)]
    [DataRow(SessionVaporMode.Extreme, 1.5f)]
    public async Task RuntimeSession_RealManagedWire_VerifiesAllModesAndRestores(SessionVaporMode mode, float value)
    {
        RecordingLog log = new(); ProbeTransport transport = RuntimeWire(log);
        LoraxDeviceClient client = new(transport, log);
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await controller.ConnectAsync(new("same-owner", "PUFFCO PEAK", -40), CancellationToken.None);
        await controller.ApplySessionOverrideAsync(RuntimeRequest(mode), CancellationToken.None);
        Assert.AreEqual(255, controller.Snapshot.ActiveProfileIndex);
        Assert.IsTrue(transport.WriteFlags.All(flags => flags == 4), "Actual emitted named-session frames must match captured flags04.");
        CollectionAssert.AreEqual(new[] { LoraxPaths.ActiveProfileTime, LoraxPaths.ActiveProfileTemperature, "/p/app/thc/intn", LoraxPaths.ActiveProfile }, transport.Writes.Select(write => write.Path).ToArray());
        Assert.AreEqual(value, Number(transport.Values["/p/app/thc/intn"]));
        await controller.ClearSessionOverrideAsync(CancellationToken.None);
        Assert.AreEqual(0, controller.Snapshot.ActiveProfileIndex);
        Assert.IsTrue(transport.WriteFlags.All(flags => flags == 4), "Restoration frames must use the same narrow flags04 context.");
        Assert.AreEqual(1.5f, Number(transport.Values["/p/app/thc/intn"]));
        Assert.IsFalse(transport.Writes.Any(write => write.Path.StartsWith("/u/", StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    public async Task RuntimeSession_LostReplyAtEveryWrite_RestoresWithoutHeat(int boundary)
    {
        RecordingLog log = new(); ProbeTransport transport = RuntimeWire(log);
        IOException expected = new("lost reply");
        transport.AfterWrite = (_, _) => { if (transport.Writes.Count == boundary) { throw expected; } };
        LoraxDeviceClient client = new(transport, log);
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await controller.ConnectAsync(new("owner", "PUFFCO PEAK", -40), CancellationToken.None);
        Assert.AreSame(expected, await Assert.ThrowsExactlyAsync<IOException>(() => controller.ApplySessionOverrideAsync(RuntimeRequest(SessionVaporMode.Bold), CancellationToken.None)));
        Assert.AreEqual(0, transport.Values[LoraxPaths.ActiveProfile][0]);
        Assert.AreEqual(90f, Number(transport.Values[LoraxPaths.ActiveProfileTime]));
        Assert.AreEqual(260f, Number(transport.Values[LoraxPaths.ActiveProfileTemperature]));
        Assert.AreEqual(1.5f, Number(transport.Values["/p/app/thc/intn"]));
        AssertNoHeat(log);
    }

    [TestMethod]
    public async Task RuntimeSession_UnknownFirmwareRefusesBeforeWrites()
    {
        RecordingLog log = new(); ProbeTransport transport = RuntimeWire(log);
        transport.Values[LoraxPaths.FirmwareVersion] = "AW-1d90ae1"u8.ToArray();
        await using LoraxDeviceClient client = new(transport, log);
        await client.ConnectAsync(new("owner", "PUFFCO PEAK", -40), CancellationToken.None);
        Assert.AreEqual("AW-1d90ae1", client.Snapshot.Identity!.FirmwareVersion);
        Assert.IsFalse(client.Snapshot.IsFirmwareVerified);
        await Assert.ThrowsExactlyAsync<DeviceSafetyException>(() => client.ApplySessionOverrideAsync(RuntimeRequest(SessionVaporMode.Intense), CancellationToken.None));
        Assert.HasCount(0, transport.Writes);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    public async Task RuntimeSession_CancellationAtEveryApplyBoundary_UsesIndependentRecovery(int boundary)
    {
        RecordingLog log = new(); ProbeTransport transport = RuntimeWire(log);
        using CancellationTokenSource cancellation = new();
        transport.AfterWrite = (_, _) => { if (transport.Writes.Count == boundary) { cancellation.Cancel(); } };
        LoraxDeviceClient client = new(transport, log);
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await controller.ConnectAsync(new("owner", "PUFFCO PEAK", -40), CancellationToken.None);
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => controller.ApplySessionOverrideAsync(RuntimeRequest(SessionVaporMode.Bold), cancellation.Token));
        Assert.IsFalse(client.HasPendingRecovery);
        Assert.AreEqual(0, transport.Values[LoraxPaths.ActiveProfile][0]);
        Assert.AreEqual(1.5f, Number(transport.Values["/p/app/thc/intn"]));
        AssertNoHeat(log);
    }

    [TestMethod]
    public async Task RuntimeSession_ReconnectDuringHeat_DoesNotReuseOldStartProof()
    {
        RecordingLog log = new(); ProbeTransport transport = RuntimeWire(log);
        LoraxDeviceClient client = new(transport, log);
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        DeviceCandidate candidate = new("owner", "PUFFCO PEAK", -40);
        await controller.ConnectAsync(candidate, CancellationToken.None);
        await controller.ApplySessionOverrideAsync(RuntimeRequest(SessionVaporMode.Intense), CancellationToken.None);
        transport.Values[LoraxPaths.OperatingState] = [8]; await controller.RefreshAsync(CancellationToken.None);
        await controller.DisconnectAsync(CancellationToken.None);
        Assert.IsTrue(client.HasPendingRecovery);
        await controller.ConnectAsync(candidate, CancellationToken.None);
        transport.Values[LoraxPaths.OperatingState] = [5];
        await client.RefreshAsync(CancellationToken.None);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => client.StartSessionAsync(CancellationToken.None));
        await controller.RefreshAsync(CancellationToken.None);
        Assert.IsFalse(client.HasPendingRecovery);
        Assert.AreEqual(0, controller.Snapshot.ActiveProfileIndex);
        AssertNoHeat(log);
    }

    [TestMethod]
    public async Task RuntimeSession_BusyFreshPreflightRefusesBeforeHeat()
    {
        RecordingLog log = new(); ProbeTransport transport = RuntimeWire(log);
        LoraxDeviceClient client = new(transport, log);
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await controller.ConnectAsync(new("owner", "PUFFCO PEAK", -40), CancellationToken.None);
        await controller.ApplySessionOverrideAsync(RuntimeRequest(SessionVaporMode.Bold), CancellationToken.None);
        transport.Values[LoraxPaths.OperatingState] = [8];
        await Assert.ThrowsExactlyAsync<DeviceSafetyException>(() => controller.StartAsync(CancellationToken.None));
        AssertNoHeat(log);
        transport.Values[LoraxPaths.OperatingState] = [5];
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    public async Task RuntimeSession_CleanupLostReplyRetainsRecoveryAndStop(int boundary)
    {
        RecordingLog log = new(); ProbeTransport transport = RuntimeWire(log);
        LoraxDeviceClient client = new(transport, log);
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await controller.ConnectAsync(new("owner", "PUFFCO PEAK", -40), CancellationToken.None);
        await controller.ApplySessionOverrideAsync(RuntimeRequest(SessionVaporMode.Bold), CancellationToken.None);
        transport.AfterWrite = (_, _) => { if (transport.Writes.Count == 4 + boundary) { throw new IOException("cleanup reply lost"); } };
        await Assert.ThrowsExactlyAsync<IOException>(() => controller.ClearSessionOverrideAsync(CancellationToken.None));
        Assert.IsTrue(client.HasPendingRecovery);
        await Assert.ThrowsExactlyAsync<DeviceSafetyException>(() => controller.StartAsync(CancellationToken.None));
        transport.Values[LoraxPaths.OperatingState] = [8]; await client.RefreshAsync(CancellationToken.None);
        transport.AfterWrite = (path, value) => { if (path == LoraxPaths.ModeCommand && value[0] == 8) { transport.Values[LoraxPaths.OperatingState] = [5]; } };
        await controller.StopAsync(CancellationToken.None);
        Assert.AreEqual(8, transport.Writes.Last(write => write.Path == LoraxPaths.ModeCommand).Value[0]);
        await controller.ClearSessionOverrideAsync(CancellationToken.None);
        Assert.IsFalse(client.HasPendingRecovery);
    }

    [TestMethod]
    public async Task RuntimeSession_BoostUsesOriginalSlotAndRestoresCapturedZeroOffset()
    {
        RecordingLog log = new(); ProbeTransport transport = RuntimeWire(log);
        LoraxDeviceClient client = new(transport, log);
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await controller.ConnectAsync(new("owner", "PUFFCO PEAK", -40), CancellationToken.None);
        await controller.ApplySessionOverrideAsync(RuntimeRequest(SessionVaporMode.Intense), CancellationToken.None);
        transport.Values[LoraxPaths.OperatingState] = [8]; await controller.RefreshAsync(CancellationToken.None);
        await controller.BoostTemperatureAsync(2.7777777, CancellationToken.None);
        Assert.IsTrue(Number(transport.Values[LoraxPaths.TemperatureOverride]) > 0);
        transport.Values[LoraxPaths.OperatingState] = [5]; await controller.RefreshAsync(CancellationToken.None);
        Assert.AreEqual(0f, Number(transport.Values[LoraxPaths.TemperatureOverride]));
        Assert.AreEqual(1.5f, Number(transport.Values["/p/app/thc/intn"]));
        Assert.IsFalse(client.HasPendingRecovery);
    }

    private static SessionOverride RuntimeRequest(SessionVaporMode mode) =>
        new("named", 220, TimeSpan.FromSeconds(60), VaporLevel.Max, ["#FF0000"]) { VaporMode = mode };

    private static ProbeTransport RuntimeWire(RecordingLog log)
    {
        ProbeTransport transport = new(log) { AllowModeCommand = true };
        transport.Values["/p/sys/fw/api"] = [39, 0, 0, 0];
        transport.Values["/p/app/thc/intn"] = Float(1.5f);
        transport.Values["/p/app/thc/colr"] = [0xA0];
        for (int slot = 0; slot < 4; slot++)
        {
            transport.Values[$"/u/app/hc/{slot}/intn"] = Float(1.5f);
            transport.Values[$"/u/app/hc/{slot}/name"] = "TEST"u8.ToArray();
            transport.Values[$"/u/app/hc/{slot}/colr"] = [0xA0];
            transport.Values[LoraxPaths.ProfileBoostTemperature(slot)] = Float(2.7777777f);
            transport.Values[LoraxPaths.ProfileBoostTime(slot)] = Float(10f);
        }
        byte[] longOpaque = new byte[799];
        longOpaque[0] = 0xA1; longOpaque[1] = 0x61; longOpaque[2] = (byte)'x';
        longOpaque[3] = 0x59; longOpaque[4] = 0x03; longOpaque[5] = 0x19;
        transport.Values["/u/app/hc/3/colr"] = longOpaque;
        return transport;
    }
}

