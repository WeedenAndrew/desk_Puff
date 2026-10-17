using DeskPuff.Bluetooth.Windows.Protocol;
using DeskPuff.Core.Safety;

namespace DeskPuff.Bluetooth.Windows.Tests;

public sealed partial class ThcProbeTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task Cycle_ClearingRequiresTargetThroughLastHeatingObservation_InBothModes(bool mc, bool leftDuringCycle)
    {
        RecordingLog log = new();
        ProbeTransport transport = McTransport(log);
        int stateReads = 0;
        transport.BeforeRead = path =>
        {
            if (transport.Writes.Count != (mc ? 2 : 1) || path != LoraxPaths.OperatingState) { return; }
            if (mc && stateReads++ == 0) { return; } // StartSessionAsync's post-write refresh, not a probe tick.
            if (!mc) { stateReads++; }
            int tick = stateReads - (mc ? 2 : 1);
            transport.Values[path] = [tick < 2 ? (byte)7 : (byte)5];
            transport.Values[LoraxPaths.StateElapsedTime] = Float(tick == 0 ? 0.21f : tick == 1 ? 89.8f : 90);
            transport.Values[LoraxPaths.ActiveProfileTemperature] =
                leftDuringCycle || tick >= 2 ? Float(248.88889f) : transport.Writes[0].Value;
            transport.Values[LoraxPaths.HeaterTargetTemperature] = transport.Values[LoraxPaths.ActiveProfileTemperature];
        };
        await using LoraxDeviceClient client = new(transport, log);
        await client.ConnectAsync(new("test", "PUFFCO PEAK", -40), CancellationToken.None);
        if (mc) { await client.RunThcMcProbeAsync(TimeSpan.Zero, 6, CancellationToken.None); }
        else { await client.RunThcDriveProbeAsync(TimeSpan.Zero, 6, CancellationToken.None); }

        Assert.HasCount(mc ? 3 : 2, transport.Writes);
        CollectionAssert.AreEqual(mc
            ? new[] { LoraxPaths.ActiveProfileTemperature, LoraxPaths.ModeCommand, LoraxPaths.ActiveProfile }
            : new[] { LoraxPaths.ActiveProfileTemperature, LoraxPaths.ActiveProfile }, transport.Writes.Select(w => w.Path).ToArray());
        Assert.IsTrue(Number(transport.Writes[0].Value) < 248.88889f);
        if (mc) { CollectionAssert.AreEqual(new byte[] { 7 }, transport.Writes[1].Value); }
        CollectionAssert.AreEqual(new byte[] { 3 }, transport.Writes[^1].Value);
        AssertBudget(transport, log);
        StringAssert.Contains(log.Text, leftDuringCycle
            ? "clearing=unobserved reason=thc-left-target-during-cycle"
            : "clearing=cleared-to-slot reason=last-heating-target-observed");
        StringAssert.Contains(log.Text, "lastHeatingStat/elap=89.8");
        if (mc)
        {
            StringAssert.Contains(log.Text, $"finding=first-seven survivedMcStart={(leftDuringCycle ? "reverted-to-slot" : "survived")}");
            StringAssert.Contains(log.Text, "stat/elap=0.21; firmware reload cause remains a hypothesis");
        }
        int evidence = log.Lines.FindIndex(l => l.StartsWith($"THC DRIVE phase={(mc ? 7 : 5)} complete", StringComparison.Ordinal));
        int thc = log.Lines.FindIndex(l => l.StartsWith("THC DRIVE tick=-1 path=\"/p/app/thc/temp\" hex=", StringComparison.Ordinal));
        int slot = log.Lines.FindIndex(l => l.StartsWith("THC DRIVE tick=-1 path=\"/u/app/hc/3/temp\" hex=", StringComparison.Ordinal));
        int restore = log.Lines.FindIndex(l => l.StartsWith("LORAX WRITE FRAME", StringComparison.Ordinal) && l.Contains("path=\"/p/app/hcs\"", StringComparison.Ordinal));
        Assert.IsTrue(thc >= 0 && slot >= 0 && evidence > thc && evidence > slot && restore > evidence,
            "FATAL EXPERIMENT ORDER: both post-cycle evidence reads MUST precede hcs restore; write counts alone cannot prove this.");
        if (mc)
        {
            int reread = log.Lines.FindIndex(l => l.StartsWith("THC MC phase=4", StringComparison.Ordinal));
            int start = log.Lines.FindIndex(l => l.StartsWith("LORAX WRITE FRAME", StringComparison.Ordinal) && l.Contains("path=\"/p/app/mc\"", StringComparison.Ordinal));
            Assert.IsTrue(reread >= 0 && start > reread && thc > start, "No heat before phase-4 target evidence.");
        }
    }

    [TestMethod]
    [DataRow(LoraxPaths.OperatingState, 7)]
    [DataRow(LoraxPaths.OperatingState, 8)]
    [DataRow(LoraxPaths.TemperatureOverride, 1)]
    [DataRow(LoraxPaths.TimeOverride, 1)]
    [DataRow(LoraxPaths.ActiveProfileTemperature, 210)]
    [DataRow(LoraxPaths.ActiveProfileTemperature, int.MaxValue)]
    public async Task Mc_PreconditionsRefuseBeforeAnyWrite(string path, int value)
    {
        RecordingLog log = new();
        ProbeTransport transport = McTransport(log);
        // This is a raw device response, not a value admitted by the write encoder.
        transport.Values[path] = path == LoraxPaths.OperatingState ? [(byte)value]
            : value == int.MaxValue ? new byte[] { 0, 0, 0, 0x4F } : Float(value);
        await using LoraxDeviceClient client = new(transport, log);
        await client.ConnectAsync(new("test", "PUFFCO PEAK", -40), CancellationToken.None);
        await Assert.ThrowsExactlyAsync<DeviceSafetyException>(() => client.RunThcMcProbeAsync(TimeSpan.Zero, 4, CancellationToken.None));
        Assert.HasCount(0, transport.Writes);
        AssertNoHeat(log);
    }

    [TestMethod]
    public async Task Mc_Phase4MismatchNeverConstructsStart_EvenAfterVerifiedWrite()
    {
        RecordingLog log = new();
        ProbeTransport transport = McTransport(log);
        int reads = 0;
        transport.BeforeRead = path =>
        {
            if (transport.Writes.Count == 1 && path == LoraxPaths.ActiveProfileTemperature && ++reads == 2)
            { transport.Values[path] = Float(248.88889f); }
        };
        await using LoraxDeviceClient client = new(transport, log);
        await client.ConnectAsync(new("test", "PUFFCO PEAK", -40), CancellationToken.None);
        DeviceSafetyException exception = await Assert.ThrowsExactlyAsync<DeviceSafetyException>(() => client.RunThcMcProbeAsync(TimeSpan.Zero, 4, CancellationToken.None));
        StringAssert.Contains(exception.Message, "phase-4 re-read");
        Assert.HasCount(1, transport.Writes);
        StringAssert.Contains(log.Text, "result=verified");
        StringAssert.Contains(log.Text, "matchesTarget=False");
        AssertNoHeat(log);
    }

    [TestMethod]
    [DataRow(LoraxPaths.HeaterTemperature)]
    [DataRow(LoraxPaths.ChamberType)]
    [DataRow(LoraxPaths.OperatingState)]
    public async Task Mc_FreshTelemetryNotConnectCacheControlsStart(string changedPath)
    {
        RecordingLog log = new();
        ProbeTransport transport = McTransport(log);
        transport.BeforeRead = path =>
        {
            if (transport.Writes.Count == 1 && path == changedPath)
            {
                transport.Values[path] = path == LoraxPaths.HeaterTemperature ? new byte[] { 0, 0, 192, 127 }
                    : path == LoraxPaths.ChamberType ? new byte[] { 0 } : new byte[] { 8 };
            }
        };
        await using LoraxDeviceClient client = new(transport, log);
        await client.ConnectAsync(new("test", "PUFFCO PEAK", -40), CancellationToken.None);
        Assert.AreEqual(25, client.Snapshot.CurrentTemperatureCelsius);
        await Assert.ThrowsExactlyAsync<DeviceSafetyException>(() => client.RunThcMcProbeAsync(TimeSpan.Zero, 4, CancellationToken.None));
        Assert.HasCount(1, transport.Writes);
        StringAssert.Contains(log.Text, "WRITE BLOCKED action=StartSession");
        AssertNoHeat(log);
    }

    [TestMethod]
    [DataRow("success")]
    [DataRow("lost-reply")]
    [DataRow("rejected")]
    [DataRow("post-write-refresh")]
    public async Task Mc_HighTcmdAttemptsAbortOnce_UnknownOutcomeInstructsPhysicalEscape(string outcome)
    {
        RecordingLog log = new();
        ProbeTransport transport = McTransport(log, rejectWrite: outcome == "rejected" ? 3 : 0);
        transport.AfterWrite = (path, value) =>
        {
            if (path != LoraxPaths.ModeCommand) { return; }
            if (value[0] == 7)
            {
                transport.Values[LoraxPaths.OperatingState] = [7];
                transport.Values[LoraxPaths.HeaterTargetTemperature] = Float(249);
            }
            else if (outcome == "lost-reply") { throw new IOException("abort reply lost; may have acted"); }
            else { transport.Values[LoraxPaths.OperatingState] = [5]; }
        };
        transport.BeforeRead = path =>
        {
            if (transport.Writes.Count == 3 && outcome == "post-write-refresh") { throw new IOException("refresh lost after abort accepted"); }
        };
        await using LoraxDeviceClient client = new(transport, log);
        await client.ConnectAsync(new("test", "PUFFCO PEAK", -40), CancellationToken.None);
        await client.RunThcMcProbeAsync(TimeSpan.Zero, 9, CancellationToken.None);
        Assert.HasCount(3, transport.Writes);
        CollectionAssert.AreEqual(new[] { LoraxPaths.ActiveProfileTemperature, LoraxPaths.ModeCommand, LoraxPaths.ModeCommand }, transport.Writes.Select(w => w.Path).ToArray());
        CollectionAssert.AreEqual(new byte[] { 7 }, transport.Writes[1].Value);
        CollectionAssert.AreEqual(new byte[] { 8 }, transport.Writes[2].Value);
        AssertBudget(transport, log);
        Assert.IsFalse(log.Text.Contains("phase=8 begin", StringComparison.Ordinal));
        StringAssert.Contains(log.Text, "abort-trigger tcmd=249 original=248.88889");
        StringAssert.Contains(log.Text, outcome == "success" ? "abort-request-completed" :
            "abort-outcome=unknown; no retry; a missing reply does not mean the device did not act; press-and-hold the device now to abort physically.");
    }

    [TestMethod]
    [DataRow("rejected")]
    [DataRow("lost-reply")]
    [DataRow("post-write-refresh")]
    public async Task Mc_StartFailureIsNeverRetried(string outcome)
    {
        RecordingLog log = new();
        ProbeTransport transport = McTransport(log, rejectWrite: outcome == "rejected" ? 2 : 0);
        transport.AfterWrite = (path, _) =>
        {
            if (path == LoraxPaths.ModeCommand && outcome == "lost-reply") { throw new IOException("start reply lost"); }
        };
        transport.BeforeRead = _ =>
        {
            if (transport.Writes.Count == 2 && outcome == "post-write-refresh") { throw new IOException("start refresh lost"); }
        };
        await using LoraxDeviceClient client = new(transport, log);
        await client.ConnectAsync(new("test", "PUFFCO PEAK", -40), CancellationToken.None);
        await Assert.ThrowsExactlyAsync<IOException>(() => client.RunThcMcProbeAsync(TimeSpan.Zero, 9, CancellationToken.None));
        Assert.HasCount(2, transport.Writes);
        AssertBudget(transport, log);
    }

    [TestMethod]
    [DataRow("timeout")]
    [DataRow("powered-off")]
    [DataRow("disconnected")]
    public async Task Mc_IncompleteRunHasOnlyThcAndStart_NoRestore(string outcome)
    {
        RecordingLog log = new();
        ProbeTransport transport = McTransport(log);
        int states = 0;
        transport.BeforeRead = path =>
        {
            if (transport.Writes.Count != 2 || path != LoraxPaths.OperatingState || states++ == 0) { return; }
            if (outcome == "powered-off") { transport.Values[path] = [3]; }
            if (outcome == "disconnected") { throw new IOException("link unavailable"); }
        };
        await using LoraxDeviceClient client = new(transport, log);
        await client.ConnectAsync(new("test", "PUFFCO PEAK", -40), CancellationToken.None);
        await client.RunThcMcProbeAsync(TimeSpan.Zero, 6, CancellationToken.None);
        Assert.HasCount(2, transport.Writes);
        AssertBudget(transport, log);
        StringAssert.Contains(log.Text, outcome == "timeout" ? "no cycle observed" : outcome == "powered-off" ? "outcome=powered-off" : "outcome=disconnected-or-unavailable");
        StringAssert.Contains(log.Text, "clearing=unobserved survivedMcStart=unobserved");
        if (outcome == "disconnected") { Assert.AreEqual(3, log.Lines.Count(l => l.Contains("observation=gap", StringComparison.Ordinal))); }
    }

    [TestMethod]
    public async Task Mc_FirstSevenCannotBeReplacedAfterReadGap_SentinelsAndFailureResetPreserved()
    {
        RecordingLog log = new();
        ProbeTransport transport = McTransport(log);
        int tick = -2;
        transport.BeforeRead = path =>
        {
            if (transport.Writes.Count != 2) { return; }
            if (path == LoraxPaths.OperatingState)
            {
                tick++;
                if (tick < 0) { return; } // Start's refresh.
                if (tick is 2 or 3) { throw new IOException("transient state read"); }
                transport.Values[path] = [tick >= 5 ? (byte)5 : (byte)7];
                transport.Values[LoraxPaths.StateElapsedTime] = [0, 0, 128, 127];
                transport.Values[LoraxPaths.HeaterTemperature] = [0, 0, 192, 127];
            }
            if (tick == 0 && path == LoraxPaths.ActiveProfileTemperature) { throw new IOException("missing first seven THC"); }
        };
        await using LoraxDeviceClient client = new(transport, log);
        await client.ConnectAsync(new("test", "PUFFCO PEAK", -40), CancellationToken.None);
        await client.RunThcMcProbeAsync(TimeSpan.Zero, 9, CancellationToken.None);
        Assert.HasCount(3, transport.Writes);
        AssertBudget(transport, log);
        StringAssert.Contains(log.Text, "survivedMcStart=unobserved");
        Assert.IsFalse(log.Text.Contains("finding=first-seven", StringComparison.Ordinal));
        StringAssert.Contains(log.Text, "path=\"/p/app/stat/elap\" hex=0000807F");
        StringAssert.Contains(log.Text, "observation=PositiveInfinity finite=False");
        StringAssert.Contains(log.Text, "observation=NaN finite=False");
        Assert.IsFalse(log.Text.Contains("consecutiveFailures=3", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Mc_TraceCannotPassVerificationToConstructHeat()
    {
        RecordingLog log = new();
        ProbeTransport transport = McTransport(log, trace: true);
        await using LoraxDeviceClient client = new(transport, log, traceWrites: true);
        await client.ConnectAsync(new("test", "PUFFCO PEAK", -40), CancellationToken.None);
        await client.RunThcMcProbeAsync(TimeSpan.Zero, 4, CancellationToken.None);
        Assert.HasCount(0, transport.Writes);
        Assert.AreEqual(1, log.Lines.Count(l => l.StartsWith("LORAX WRITE FRAME", StringComparison.Ordinal)));
        StringAssert.Contains(log.Text, "TRACE-WRITE SUPPRESSED path=\"/p/app/thc/temp\"");
        AssertNoHeat(log);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Mc_FirstSevenFindingIsIndependentOfLaterDriveAndClearing(bool firstPreserved)
    {
        RecordingLog log = new();
        ProbeTransport transport = McTransport(log);
        int tick = -2;
        transport.BeforeRead = path =>
        {
            if (transport.Writes.Count != 2 || path != LoraxPaths.OperatingState || ++tick < 0) { return; }
            transport.Values[path] = [tick == 0 ? (byte)7 : tick == 1 ? (byte)8 : (byte)5];
            bool targetHeld = tick == 0 ? firstPreserved : tick == 1 && !firstPreserved;
            transport.Values[LoraxPaths.ActiveProfileTemperature] = targetHeld ? transport.Writes[0].Value : Float(248.88889f);
            transport.Values[LoraxPaths.HeaterTargetTemperature] = transport.Values[LoraxPaths.ActiveProfileTemperature];
            transport.Values[LoraxPaths.StateElapsedTime] = Float(tick == 0 ? 0.21f : 89.8f);
        };
        await using LoraxDeviceClient client = new(transport, log);
        await client.ConnectAsync(new("test", "PUFFCO PEAK", -40), CancellationToken.None);
        await client.RunThcMcProbeAsync(TimeSpan.Zero, 6, CancellationToken.None);
        Assert.HasCount(3, transport.Writes);
        AssertBudget(transport, log);
        StringAssert.Contains(log.Text, $"SUMMARY drive=observed clearing=unobserved survivedMcStart={(firstPreserved ? "survived" : "reverted-to-slot")}");
        Assert.AreEqual(1, log.Lines.Count(l => l.Contains("finding=first-seven", StringComparison.Ordinal)));
        StringAssert.Contains(log.Text, "reason=thc-left-target-during-cycle");
    }

    [TestMethod]
    public async Task Mc_Phase7FailureNeverRestoresOrClaimsClearing()
    {
        RecordingLog log = new();
        ProbeTransport transport = McTransport(log);
        int tick = -2;
        transport.BeforeRead = path =>
        {
            if (transport.Writes.Count != 2) { return; }
            if (path == LoraxPaths.OperatingState && ++tick >= 0)
            { transport.Values[path] = [tick == 0 ? (byte)7 : (byte)5]; }
            if (tick >= 1 && path == LoraxPaths.ProfileTemperature(3)) { throw new IOException("phase 7 slot read lost"); }
        };
        await using LoraxDeviceClient client = new(transport, log);
        await client.ConnectAsync(new("test", "PUFFCO PEAK", -40), CancellationToken.None);
        await Assert.ThrowsExactlyAsync<IOException>(() => client.RunThcMcProbeAsync(TimeSpan.Zero, 5, CancellationToken.None));
        Assert.HasCount(2, transport.Writes);
        AssertBudget(transport, log);
        Assert.IsFalse(log.Text.Contains("phase=7 complete", StringComparison.Ordinal));
        Assert.IsFalse(log.Text.Contains("phase=8 begin", StringComparison.Ordinal), "FATAL: no hcs restore after incomplete phase-7 evidence.");
        StringAssert.Contains(log.Text, "SUMMARY drive=observed clearing=unobserved survivedMcStart=survived");
    }

    private static ProbeTransport McTransport(RecordingLog log, int rejectWrite = 0, bool trace = false)
    {
        ProbeTransport transport = new(log, trace) { AllowModeCommand = true, RejectWrite = rejectWrite };
        transport.Values[LoraxPaths.ActiveProfile] = [3];
        transport.Values[LoraxPaths.ActiveProfileTemperature] = Float(248.88889f);
        transport.Values[LoraxPaths.ProfileTemperature(3)] = Float(248.88889f);
        transport.AfterWrite = (path, value) =>
        {
            if (path != LoraxPaths.ModeCommand) { return; }
            transport.Values[LoraxPaths.HeaterTargetTemperature] = transport.Values[LoraxPaths.ActiveProfileTemperature];
            if (value[0] == 8) { transport.Values[LoraxPaths.OperatingState] = [5]; }
        };
        return transport;
    }

    private static void AssertBudget(ProbeTransport transport, RecordingLog log)
    {
        Assert.IsTrue(transport.Writes.Count <= 4, "Hard upper bound: THC, start, optional abort, restore; never repeated.");
        Assert.AreEqual(transport.Writes.Count, log.Lines.Count(l => l.StartsWith("LORAX WRITE FRAME", StringComparison.Ordinal)), "Count constructed frames, not merely accepted writes.");
        Assert.AreEqual(1, transport.Writes.Count(w => w.Path == LoraxPaths.ActiveProfileTemperature));
        Assert.IsTrue(transport.Writes.Count(w => w.Path == LoraxPaths.ActiveProfile) <= 1);
        foreach (byte command in new byte[] { 7, 8 })
        { Assert.IsTrue(transport.Writes.Count(w => w.Path == LoraxPaths.ModeCommand && w.Value.SequenceEqual(new[] { command })) <= 1, "mc must NEVER be retried."); }
        Assert.IsTrue(transport.Writes.All(w => w.Path is LoraxPaths.ActiveProfileTemperature or LoraxPaths.ModeCommand or LoraxPaths.ActiveProfile));
    }
}
