using DeskPuff.Bluetooth.Windows.Protocol;
using DeskPuff.Core.Safety;

namespace DeskPuff.Bluetooth.Windows.Tests;

public sealed partial class ThcProbeTests
{
    [TestMethod]
    [DataRow(true, true)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    public async Task Drive_CompletedRunHasExactlyTwoWrites_AndEvidencePrecedesRestore(bool clears, bool drives)
    {
        RecordingLog log = new();
        ProbeTransport transport = DriveTransport(log);
        int tick = 0;
        transport.BeforeRead = path =>
        {
            if (transport.Writes.Count != 1 || path != LoraxPaths.OperatingState) { return; }
            byte state = tick++ == 0 ? (byte)7 : (byte)5;
            transport.Values[LoraxPaths.OperatingState] = [state];
            transport.Values[LoraxPaths.HeaterTargetTemperature] = drives ? transport.Writes[0].Value : Float(240);
            if (state == 5 && clears) { transport.Values[LoraxPaths.ActiveProfileTemperature] = Float(248.88889f); }
        };
        await using LoraxDeviceClient client = new(transport, log);
        await client.ConnectAsync(new("test", "PUFFCO PEAK", -40), CancellationToken.None);
        await client.RunThcDriveProbeAsync(TimeSpan.Zero, 5, CancellationToken.None);

        Assert.HasCount(2, transport.Writes, "Completed drive probe must have only one THC write and one hcs restore.");
        Assert.AreEqual(LoraxPaths.ActiveProfileTemperature, transport.Writes[0].Path);
        Assert.AreEqual((float)(248.88889f - 16.666667), Number(transport.Writes[0].Value));
        Assert.IsTrue(Number(transport.Writes[0].Value) < 248.88889f);
        Assert.AreEqual(LoraxPaths.ActiveProfile, transport.Writes[1].Path);
        Assert.AreEqual(3, transport.Writes[1].Value[0]);

        int evidence = log.Lines.FindIndex(line => line.StartsWith("THC DRIVE phase=5 complete", StringComparison.Ordinal));
        int restore = log.Lines.FindIndex(line => line.StartsWith("LORAX WRITE FRAME", StringComparison.Ordinal) &&
            line.Contains("path=\"/p/app/hcs\"", StringComparison.Ordinal));
        Assert.IsTrue(evidence >= 0 && restore > evidence,
            "FATAL EXPERIMENT ORDER: restoring hcs before BOTH phase-5 reads destroys the clearing evidence.");
        int finalThc = log.Lines.FindIndex(line => line.StartsWith("THC DRIVE tick=-1 path=\"/p/app/thc/temp\" hex=", StringComparison.Ordinal));
        int finalSlot = log.Lines.FindIndex(line => line.StartsWith("THC DRIVE tick=-1 path=\"/u/app/hc/3/temp\" hex=", StringComparison.Ordinal));
        Assert.IsTrue(finalThc >= 0 && finalSlot >= 0 && evidence > finalThc && evidence > finalSlot,
            "FATAL EXPERIMENT ORDER: phase 5 cannot complete before both THC and slot evidence is logged.");
        StringAssert.Contains(log.Text, $"THC DRIVE SUMMARY drive={(drives ? "observed" : "inconclusive")} clearing={(clears ? "cleared-to-slot" : "persists-at-session-end")}");
        AssertNoHeat(log);
    }

    [TestMethod]
    [DataRow(7)]
    [DataRow(8)]
    public async Task Drive_StartupHeatingRefusesWithoutWrites(int state)
    {
        RecordingLog log = new();
        ProbeTransport transport = DriveTransport(log);
        transport.Values[LoraxPaths.OperatingState] = [(byte)state];
        await using LoraxDeviceClient client = new(transport, log);
        await client.ConnectAsync(new("test", "PUFFCO PEAK", -40), CancellationToken.None);
        await Assert.ThrowsExactlyAsync<DeviceSafetyException>(() => client.RunThcDriveProbeAsync(TimeSpan.Zero, 3, CancellationToken.None));
        Assert.HasCount(0, transport.Writes);
        AssertNoHeat(log);
    }

    [TestMethod]
    [DataRow(LoraxPaths.TemperatureOverride)]
    [DataRow(LoraxPaths.TimeOverride)]
    public async Task Drive_NonzeroOverrideRefusesWithoutWrites(string path)
    {
        RecordingLog log = new();
        ProbeTransport transport = DriveTransport(log);
        transport.Values[path] = Float(1);
        await using LoraxDeviceClient client = new(transport, log);
        await client.ConnectAsync(new("test", "PUFFCO PEAK", -40), CancellationToken.None);
        await Assert.ThrowsExactlyAsync<DeviceSafetyException>(() => client.RunThcDriveProbeAsync(TimeSpan.Zero, 3, CancellationToken.None));
        Assert.HasCount(0, transport.Writes);
        AssertNoHeat(log);
    }

    [TestMethod]
    public async Task Drive_TargetBelowMinimumRefusesWithoutWrites()
    {
        RecordingLog log = new();
        ProbeTransport transport = DriveTransport(log);
        transport.Values[LoraxPaths.ActiveProfileTemperature] = Float(210);
        await using LoraxDeviceClient client = new(transport, log);
        await client.ConnectAsync(new("test", "PUFFCO PEAK", -40), CancellationToken.None);
        await Assert.ThrowsExactlyAsync<DeviceSafetyException>(() => client.RunThcDriveProbeAsync(TimeSpan.Zero, 3, CancellationToken.None));
        Assert.HasCount(0, transport.Writes);
        AssertNoHeat(log);
    }

    [TestMethod]
    public async Task Drive_NeverStarts_TimeoutHasOneWriteAndNoFailureToDriveClaim()
    {
        RecordingLog log = new();
        ProbeTransport transport = DriveTransport(log);
        await using LoraxDeviceClient client = new(transport, log);
        await client.ConnectAsync(new("test", "PUFFCO PEAK", -40), CancellationToken.None);
        await client.RunThcDriveProbeAsync(TimeSpan.Zero, 3, CancellationToken.None);
        AssertOnlyDriveWrite(transport, log);
        StringAssert.Contains(log.Text, "no cycle observed");
        StringAssert.Contains(log.Text, "SUMMARY drive=inconclusive clearing=unobserved");
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Drive_PowerOffOrReadFailuresEndWithOneWrite_AndPreserveDriveEvidence(bool poweredOff)
    {
        RecordingLog log = new();
        ProbeTransport transport = DriveTransport(log);
        int reads = 0;
        transport.BeforeRead = path =>
        {
            if (transport.Writes.Count != 1 || path != LoraxPaths.OperatingState) { return; }
            if (reads++ == 0)
            {
                transport.Values[LoraxPaths.OperatingState] = [8];
                transport.Values[LoraxPaths.HeaterTargetTemperature] = transport.Writes[0].Value;
            }
            else if (poweredOff) { transport.Values[LoraxPaths.OperatingState] = [3]; }
            else { throw new IOException("RO_E_CLOSED simulated link failure"); }
        };
        await using LoraxDeviceClient client = new(transport, log);
        await client.ConnectAsync(new("test", "PUFFCO PEAK", -40), CancellationToken.None);
        await client.RunThcDriveProbeAsync(TimeSpan.Zero, 8, CancellationToken.None);
        AssertOnlyDriveWrite(transport, log);
        StringAssert.Contains(log.Text, poweredOff ? "outcome=powered-off" : "outcome=disconnected-or-unavailable");
        Assert.AreEqual(poweredOff ? 0 : 3, log.Lines.Count(line => line.Contains("observation=gap", StringComparison.Ordinal)));
        StringAssert.Contains(log.Text, "SUMMARY drive=observed clearing=unobserved");
    }

    [TestMethod]
    public async Task Drive_TransientReadFailuresResetAfterSuccess_AndSentinelsRemainRaw()
    {
        RecordingLog log = new();
        ProbeTransport transport = DriveTransport(log);
        int tick = 0;
        transport.BeforeRead = path =>
        {
            if (transport.Writes.Count != 1 || path != LoraxPaths.OperatingState) { return; }
            int current = tick++;
            if (current is 0 or 2 or 3) { throw new IOException("transient"); }
            transport.Values[LoraxPaths.OperatingState] = [current >= 5 ? (byte)5 : (byte)8];
            transport.Values[LoraxPaths.HeaterTargetTemperature] = transport.Writes[0].Value;
            transport.Values[LoraxPaths.StateElapsedTime] = [0, 0, 128, 127];
            transport.Values[LoraxPaths.HeaterTemperature] = [0, 0, 192, 127];
        };
        await using LoraxDeviceClient client = new(transport, log);
        await client.ConnectAsync(new("test", "PUFFCO PEAK", -40), CancellationToken.None);
        await client.RunThcDriveProbeAsync(TimeSpan.Zero, 8, CancellationToken.None);
        Assert.HasCount(2, transport.Writes);
        StringAssert.Contains(log.Text, "path=\"/p/app/stat/elap\" hex=0000807F");
        StringAssert.Contains(log.Text, "path=\"/p/app/stat/elap\" observation=PositiveInfinity finite=False");
        StringAssert.Contains(log.Text, "path=\"/p/app/htr/temp\" hex=0000C07F");
        StringAssert.Contains(log.Text, "path=\"/p/app/htr/temp\" observation=NaN finite=False");
        Assert.IsFalse(log.Text.Contains("consecutiveFailures=3", StringComparison.Ordinal), "Successful ticks must reset the failure counter.");
        AssertNoHeat(log);
    }

    [TestMethod]
    public async Task Drive_Phase5ReadFailureNeverRestoresOrClaimsClearing()
    {
        RecordingLog log = new();
        ProbeTransport transport = DriveTransport(log);
        int tick = 0;
        transport.BeforeRead = path =>
        {
            if (transport.Writes.Count != 1) { return; }
            if (path == LoraxPaths.OperatingState) { transport.Values[path] = [tick++ == 0 ? (byte)8 : (byte)5]; }
            if (tick >= 2 && path == LoraxPaths.ProfileTemperature(3)) { throw new IOException("link lost during phase 5"); }
        };
        await using LoraxDeviceClient client = new(transport, log);
        await client.ConnectAsync(new("test", "PUFFCO PEAK", -40), CancellationToken.None);
        await Assert.ThrowsExactlyAsync<IOException>(() => client.RunThcDriveProbeAsync(TimeSpan.Zero, 4, CancellationToken.None));
        AssertOnlyDriveWrite(transport, log);
        Assert.IsFalse(log.Text.Contains("phase=5 complete", StringComparison.Ordinal));
        StringAssert.Contains(log.Text, "clearing=unobserved");
    }

    [TestMethod]
    public async Task Drive_TraceConstructsOnlyDownwardFrame_WithoutHardwareClaims()
    {
        RecordingLog log = new();
        ProbeTransport transport = DriveTransport(log, trace: true);
        await using LoraxDeviceClient client = new(transport, log, traceWrites: true);
        await client.ConnectAsync(new("test", "PUFFCO PEAK", -40), CancellationToken.None);
        await client.RunThcDriveProbeAsync(TimeSpan.Zero, 4, CancellationToken.None);
        Assert.HasCount(0, transport.Writes);
        Assert.AreEqual(1, log.Lines.Count(line => line.StartsWith("LORAX WRITE FRAME", StringComparison.Ordinal)));
        StringAssert.Contains(log.Text, "outcome=trace-only hardwareEvidence=none");
        AssertNoHeat(log);
    }

    private static ProbeTransport DriveTransport(RecordingLog log, bool trace = false)
    {
        ProbeTransport transport = new(log, trace);
        transport.Values[LoraxPaths.ActiveProfile] = [3];
        transport.Values[LoraxPaths.ActiveProfileTemperature] = Float(248.88889f);
        transport.Values[LoraxPaths.ProfileTemperature(3)] = Float(248.88889f);
        return transport;
    }

    private static void AssertOnlyDriveWrite(ProbeTransport transport, RecordingLog log)
    {
        Assert.HasCount(1, transport.Writes, "Interrupted/never-started run MUST NOT execute hcs cleanup.");
        Assert.AreEqual(LoraxPaths.ActiveProfileTemperature, transport.Writes[0].Path);
        Assert.IsFalse(log.Text.Contains("phase=6 begin", StringComparison.Ordinal));
        AssertNoHeat(log);
    }
}
