using System.Text;
using DeskPuff.Bluetooth.Windows.Protocol;
using DeskPuff.Core.Devices;
using DeskPuff.Core.Safety;
using DeskPuff.Core.Sessions;

namespace DeskPuff.Bluetooth.Windows.Tests;

public sealed partial class ThcProbeTests
{
    [TestMethod]
    public async Task Override_WritesTimeThenTemperature_RecordsOnlyAfterBothVerify()
    {
        RecordingLog log = new();
        ProbeTransport transport = new(log);
        LoraxDeviceClient client = new(transport, log);
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await controller.ConnectAsync(new("test", "PUFFCO PEAK", -40), CancellationToken.None);
        transport.AfterWrite = (_, _) => Assert.IsNull(client.AppliedSessionOverride,
            "Neither a sent write nor one verified write is proof of a complete override.");
        SessionOverride request = OverrideRequest();
        await controller.ApplySessionOverrideAsync(request, CancellationToken.None);
        CollectionAssert.AreEqual(new[] { LoraxPaths.ActiveProfileTime, LoraxPaths.ActiveProfileTemperature }, transport.Writes.Select(w => w.Path).ToArray());
        Assert.AreEqual(60f, Number(transport.Writes[0].Value));
        Assert.AreEqual(220f, Number(transport.Writes[1].Value));
        Assert.IsNotNull(client.AppliedSessionOverride);
        Assert.AreEqual(request.TargetTemperatureCelsius, client.AppliedSessionOverride.TargetTemperatureCelsius);
        Assert.AreEqual(request.Duration, client.AppliedSessionOverride.Duration);
        Assert.AreEqual(2, log.Lines.Count(l => l.Contains("result=verified", StringComparison.Ordinal)));
        StringAssert.Contains(log.Text, "not-transmitted vapor=Standard (validation only, not written); colours=display-only (never sent); name=local-only");
        AssertNoHeat(log);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public async Task Override_LostWriteReplyRollsBackOnce_AndRethrowsOriginal(int failingWrite)
    {
        RecordingLog log = new();
        ProbeTransport transport = new(log);
        IOException original = new("reply lost; write may have acted");
        transport.AfterWrite = (_, _) => { if (transport.Writes.Count == failingWrite) { throw original; } };
        LoraxDeviceClient client = new(transport, log);
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await controller.ConnectAsync(new("test", "PUFFCO PEAK", -40), CancellationToken.None);
        IOException received = await Assert.ThrowsExactlyAsync<IOException>(() => controller.ApplySessionOverrideAsync(OverrideRequest(), CancellationToken.None));
        Assert.AreSame(original, received);
        Assert.HasCount(failingWrite + 1, transport.Writes);
        Assert.AreEqual(LoraxPaths.ActiveProfile, transport.Writes[^1].Path);
        Assert.AreEqual(0, transport.Writes[^1].Value[0]);
        Assert.AreEqual(260f, Number(transport.Values[LoraxPaths.ActiveProfileTemperature]));
        Assert.AreEqual(90f, Number(transport.Values[LoraxPaths.ActiveProfileTime]));
        Assert.IsNull(client.AppliedSessionOverride);
        StringAssert.Contains(log.Text, "partial-application rolled-back=true; hcs reload verified");
        AssertNoHeat(log);
    }

    [TestMethod]
    public async Task Override_SecondWriteRejected_IsRolledBackWithoutRetry()
    {
        RecordingLog log = new();
        ProbeTransport transport = new(log) { RejectWrite = 2 };
        LoraxDeviceClient client = new(transport, log);
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await controller.ConnectAsync(new("test", "PUFFCO PEAK", -40), CancellationToken.None);
        IOException exception = await Assert.ThrowsExactlyAsync<IOException>(() => controller.ApplySessionOverrideAsync(OverrideRequest(), CancellationToken.None));
        Assert.AreEqual("The device rejected the Lorax write with status 0x7A.", exception.Message);
        CollectionAssert.AreEqual(new[] { LoraxPaths.ActiveProfileTime, LoraxPaths.ActiveProfileTemperature, LoraxPaths.ActiveProfile }, transport.Writes.Select(w => w.Path).ToArray());
        Assert.IsNull(client.AppliedSessionOverride);
    }

    [TestMethod]
    public async Task Override_RollbackFailurePreservesOriginalException_AndNamesPartialState()
    {
        RecordingLog log = new();
        ProbeTransport transport = new(log);
        IOException original = new("temperature reply lost");
        transport.AfterWrite = (path, _) =>
        {
            if (path == LoraxPaths.ActiveProfileTemperature) { throw original; }
            if (path == LoraxPaths.ActiveProfile) { throw new IOException("rollback reply lost"); }
        };
        LoraxDeviceClient client = new(transport, log);
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await controller.ConnectAsync(new("test", "PUFFCO PEAK", -40), CancellationToken.None);
        Assert.AreSame(original, await Assert.ThrowsExactlyAsync<IOException>(() => controller.ApplySessionOverrideAsync(OverrideRequest(), CancellationToken.None)));
        Assert.HasCount(3, transport.Writes);
        Assert.IsNull(client.AppliedSessionOverride);
        StringAssert.Contains(log.Text, "device may hold a partial override: thc/time may be 60s and thc/temp may be 220C or their previous values");
        Assert.IsFalse(log.Text.Contains("rolled-back=true", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Override_CancellationAfterFirstWriteStillRollsBackUnderFreshCleanupToken()
    {
        RecordingLog log = new();
        ProbeTransport transport = new(log);
        using CancellationTokenSource cancellation = new();
        transport.AfterWrite = (path, _) => { if (path == LoraxPaths.ActiveProfileTime) { cancellation.Cancel(); } };
        LoraxDeviceClient client = new(transport, log);
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await controller.ConnectAsync(new("test", "PUFFCO PEAK", -40), CancellationToken.None);
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => controller.ApplySessionOverrideAsync(OverrideRequest(), cancellation.Token));
        CollectionAssert.AreEqual(new[] { LoraxPaths.ActiveProfileTime, LoraxPaths.ActiveProfile }, transport.Writes.Select(w => w.Path).ToArray());
        Assert.IsNull(client.AppliedSessionOverride);
        StringAssert.Contains(log.Text, "rolled-back=true");
    }

    [TestMethod]
    [DataRow(LoraxPaths.ActiveProfileTemperature)]
    [DataRow(LoraxPaths.ActiveProfileTime)]
    public async Task Clear_HcsReadbackIsNotEnough_ReloadMismatchFails(string mismatchedPath)
    {
        RecordingLog log = new();
        ProbeTransport transport = new(log);
        transport.AfterWrite = (path, _) => { if (path == LoraxPaths.ActiveProfile) { transport.Values[mismatchedPath] = Float(1); } };
        LoraxDeviceClient client = new(transport, log);
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await controller.ConnectAsync(new("test", "PUFFCO PEAK", -40), CancellationToken.None);
        IOException exception = await Assert.ThrowsExactlyAsync<IOException>(() => controller.ClearSessionOverrideAsync(CancellationToken.None));
        StringAssert.Contains(exception.Message, "does not match the active slot");
        Assert.HasCount(1, transport.Writes);
        Assert.IsFalse(log.Text.Contains("clear=verified", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Clear_WithNoPriorApply_ReloadsBothValuesAndVerifiesEffect()
    {
        RecordingLog log = new();
        ProbeTransport transport = new(log);
        transport.Values[LoraxPaths.ActiveProfileTemperature] = Float(220);
        transport.Values[LoraxPaths.ActiveProfileTime] = Float(60);
        LoraxDeviceClient client = new(transport, log);
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await controller.ConnectAsync(new("test", "PUFFCO PEAK", -40), CancellationToken.None);
        Assert.IsNull(client.AppliedSessionOverride);
        await controller.ClearSessionOverrideAsync(CancellationToken.None);
        Assert.HasCount(1, transport.Writes);
        Assert.AreEqual(LoraxPaths.ActiveProfile, transport.Writes[0].Path);
        Assert.AreEqual(260f, Number(transport.Values[LoraxPaths.ActiveProfileTemperature]));
        Assert.AreEqual(90f, Number(transport.Values[LoraxPaths.ActiveProfileTime]));
        StringAssert.Contains(log.Text, "clear-reload slot=0 temperatureMatches=True timeMatches=True");
        StringAssert.Contains(log.Text, "clear=verified; both THC parameters match the slot");
        AssertNoHeat(log);
    }

    [TestMethod]
    public async Task Override_TraceFalseResultsNeverRecordApplyOrClearing()
    {
        RecordingLog log = new();
        ProbeTransport transport = new(log, trace: true);
        LoraxDeviceClient client = new(transport, log, traceWrites: true);
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await controller.ConnectAsync(new("test", "PUFFCO PEAK", -40), CancellationToken.None);
        await controller.ApplySessionOverrideAsync(OverrideRequest(), CancellationToken.None);
        Assert.IsNull(client.AppliedSessionOverride);
        await controller.ClearSessionOverrideAsync(CancellationToken.None);
        Assert.HasCount(0, transport.Writes);
        Assert.AreEqual(3, log.Lines.Count(l => l.StartsWith("TRACE-WRITE SUPPRESSED", StringComparison.Ordinal)));
        StringAssert.Contains(log.Text, "result=not-applied reason=trace-write-suppressed");
        StringAssert.Contains(log.Text, "clear=unverified reason=trace-write-suppressed");
        Assert.IsFalse(log.Text.Contains("SESSION OVERRIDE result=applied", StringComparison.Ordinal));
        Assert.IsFalse(log.Text.Contains("SESSION OVERRIDE clear=verified", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Override_IndependentValidationAndFinalRawIdleCheckRefuseBeforeWrites(bool rawIdleFailure)
    {
        RecordingLog log = new();
        ProbeTransport transport = new(log);
        LoraxDeviceClient client = new(transport, log);
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await controller.ConnectAsync(new("test", "PUFFCO PEAK", -40), CancellationToken.None);
        int stateReads = 0;
        transport.BeforeRead = path =>
        {
            if (rawIdleFailure && path == LoraxPaths.OperatingState && ++stateReads == 2) { transport.Values[path] = [7]; }
        };
        SessionOverride request = rawIdleFailure ? OverrideRequest() : OverrideRequest() with { Vapor = VaporLevel.XL };
        await Assert.ThrowsExactlyAsync<DeviceSafetyException>(() => controller.ApplySessionOverrideAsync(request, CancellationToken.None));
        Assert.HasCount(0, transport.Writes);
        StringAssert.Contains(log.Text, "WRITE BLOCKED action=ApplySessionOverride");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Controller_ApplyAndRollbackHoldOneGate_QueuedClearCannotInterleave(bool rollback)
    {
        RecordingLog log = new();
        ProbeTransport transport = new(log);
        LoraxDeviceClient client = new(transport, log);
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await controller.ConnectAsync(new("test", "PUFFCO PEAK", -40), CancellationToken.None);
        TaskCompletionSource atTemperature = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseTemperature = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource atRollback = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseRollback = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IOException original = new("temperature attempt failed");
        bool blockedRollback = false;
        transport.BeforeCommandAsync = async (opcode, body) =>
        {
            if (opcode != LoraxOpcode.WriteShort) { return; }
            string path = WrittenPath(body);
            if (path == LoraxPaths.ActiveProfileTemperature)
            {
                atTemperature.SetResult();
                await releaseTemperature.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (rollback) { throw original; }
            }
            if (rollback && path == LoraxPaths.ActiveProfile && !blockedRollback)
            {
                blockedRollback = true;
                atRollback.SetResult();
                await releaseRollback.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
        };
        Task applying = controller.ApplySessionOverrideAsync(OverrideRequest(), CancellationToken.None);
        await atTemperature.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task clearing = controller.ClearSessionOverrideAsync(CancellationToken.None);
        Assert.IsFalse(clearing.IsCompleted, "A concurrent clear must wait behind the entire apply.");
        Assert.HasCount(1, transport.Writes);
        releaseTemperature.SetResult();
        if (rollback)
        {
            await atRollback.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(clearing.IsCompleted, "Rollback MUST remain under the original commandGate hold.");
            Assert.IsFalse(applying.IsCompleted, "The original exception must wait until rollback completes.");
            Assert.HasCount(1, transport.Writes);
            releaseRollback.SetResult();
            Assert.AreSame(original, await Assert.ThrowsExactlyAsync<IOException>(() => applying));
        }
        else { await applying; }
        await clearing.WaitAsync(TimeSpan.FromSeconds(5));
        CollectionAssert.AreEqual(rollback
            ? new[] { LoraxPaths.ActiveProfileTime, LoraxPaths.ActiveProfile, LoraxPaths.ActiveProfile }
            : new[] { LoraxPaths.ActiveProfileTime, LoraxPaths.ActiveProfileTemperature, LoraxPaths.ActiveProfile }, transport.Writes.Select(w => w.Path).ToArray());
        Assert.IsNull(client.AppliedSessionOverride);
        if (rollback)
        {
            int rolledBack = log.Lines.FindIndex(l => l.Contains("rolled-back=true", StringComparison.Ordinal));
            int finalHcs = log.Lines.FindLastIndex(l => l.StartsWith("LORAX WRITE FRAME", StringComparison.Ordinal));
            Assert.IsTrue(rolledBack >= 0 && finalHcs > rolledBack, "Queued clear must begin after rollback, never in its middle.");
        }
    }

    [TestMethod]
    public async Task Start_AppliedOverrideMismatchRefusesWithLiveAndExpectedValues()
    {
        RecordingLog log = new();
        ProbeTransport transport = new(log) { AllowModeCommand = true };
        LoraxDeviceClient client = new(transport, log);
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await controller.ConnectAsync(new("test", "PUFFCO PEAK", -40), CancellationToken.None);
        await controller.ApplySessionOverrideAsync(OverrideRequest(), CancellationToken.None);
        transport.Values[LoraxPaths.ActiveProfileTemperature] = Float(219);
        transport.Values[LoraxPaths.ActiveProfileTime] = Float(59);

        DeviceSafetyException exception = await Assert.ThrowsExactlyAsync<DeviceSafetyException>(
            () => controller.StartAsync(CancellationToken.None));

        Assert.AreEqual(
            "Session start refused: live thc/temp=219C thc/time=59s; expected applied override thc/temp=220C thc/time=60s.",
            exception.Message);
        CollectionAssert.AreEqual(
            new[] { LoraxPaths.ActiveProfileTime, LoraxPaths.ActiveProfileTemperature },
            transport.Writes.Select(write => write.Path).ToArray(),
            "A mismatch must be refused without hcs substitution or mc.");
        StringAssert.Contains(log.Text, $"WRITE BLOCKED action={DeviceAction.StartSession} reason=\"{exception.Message}\"");
        Assert.IsTrue(client.HasUntrackedSessionParameters);
    }

    [TestMethod]
    public async Task Start_AppliedOverrideMatchProceedsWithoutSlotReload()
    {
        RecordingLog log = new();
        ProbeTransport transport = new(log) { AllowModeCommand = true };
        LoraxDeviceClient client = new(transport, log);
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await controller.ConnectAsync(new("test", "PUFFCO PEAK", -40), CancellationToken.None);
        await controller.ApplySessionOverrideAsync(OverrideRequest(), CancellationToken.None);

        await controller.StartAsync(CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { LoraxPaths.ActiveProfileTime, LoraxPaths.ActiveProfileTemperature, LoraxPaths.ModeCommand },
            transport.Writes.Select(write => write.Path).ToArray());
        Assert.AreEqual((byte)DeviceModeCommand.StartHeatCycle, transport.Writes[^1].Value[0]);
        Assert.IsFalse(client.HasUntrackedSessionParameters);
    }

    [TestMethod]
    public async Task Start_NoAppliedOverrideAndThcMatchesSlot_ProceedsWithoutReload()
    {
        RecordingLog log = new();
        ProbeTransport transport = new(log) { AllowModeCommand = true };
        LoraxDeviceClient client = new(transport, log);
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await controller.ConnectAsync(new("test", "PUFFCO PEAK", -40), CancellationToken.None);

        await controller.StartAsync(CancellationToken.None);

        Assert.HasCount(1, transport.Writes);
        Assert.AreEqual(LoraxPaths.ModeCommand, transport.Writes[0].Path);
        Assert.AreEqual((byte)DeviceModeCommand.StartHeatCycle, transport.Writes[0].Value[0]);
        StringAssert.Contains(log.Text, "start-check result=slot-match");
    }

    [TestMethod]
    public async Task Start_UntrackedThcReloadsSlotBeforeMc()
    {
        RecordingLog log = new();
        ProbeTransport transport = new(log) { AllowModeCommand = true };
        transport.Values[LoraxPaths.ActiveProfileTemperature] = Float(220);
        transport.Values[LoraxPaths.ActiveProfileTime] = Float(60);
        LoraxDeviceClient client = new(transport, log);
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await controller.ConnectAsync(new("test", "PUFFCO PEAK", -40), CancellationToken.None);
        Assert.IsTrue(client.HasUntrackedSessionParameters);

        await controller.StartAsync(CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { LoraxPaths.ActiveProfile, LoraxPaths.ModeCommand },
            transport.Writes.Select(write => write.Path).ToArray(),
            "The identity hcs reload MUST precede mc.");
        Assert.AreEqual(260f, Number(transport.Values[LoraxPaths.ActiveProfileTemperature]));
        Assert.AreEqual(90f, Number(transport.Values[LoraxPaths.ActiveProfileTime]));
        StringAssert.Contains(log.Text, "start-check result=reloaded-slot-before-start");
        Assert.IsFalse(client.HasUntrackedSessionParameters);
    }

    [TestMethod]
    public async Task Start_FailedSlotReloadRefusesWithoutMc()
    {
        RecordingLog log = new();
        ProbeTransport transport = new(log) { AllowModeCommand = true };
        transport.Values[LoraxPaths.ActiveProfileTemperature] = Float(220);
        transport.Values[LoraxPaths.ActiveProfileTime] = Float(60);
        transport.AfterWrite = (path, _) =>
        {
            if (path == LoraxPaths.ActiveProfile)
            {
                transport.Values[LoraxPaths.ActiveProfileTemperature] = Float(221);
            }
        };
        LoraxDeviceClient client = new(transport, log);
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await controller.ConnectAsync(new("test", "PUFFCO PEAK", -40), CancellationToken.None);

        DeviceSafetyException exception = await Assert.ThrowsExactlyAsync<DeviceSafetyException>(
            () => controller.StartAsync(CancellationToken.None));

        Assert.HasCount(1, transport.Writes);
        Assert.AreEqual(LoraxPaths.ActiveProfile, transport.Writes[0].Path);
        StringAssert.Contains(exception.Message, "live thc/temp=221C thc/time=90s");
        StringAssert.Contains(exception.Message, "expected slot=0 temp=260C time=90s");
        Assert.IsFalse(transport.Writes.Any(write => write.Path == LoraxPaths.ModeCommand),
            "mc must not be sent when hcs failed to restore known slot parameters.");
        Assert.IsTrue(client.HasUntrackedSessionParameters);
    }

    [TestMethod]
    public async Task SelectProfile_SuccessNullsAppliedOverrideProof()
    {
        RecordingLog log = new();
        ProbeTransport transport = new(log);
        LoraxDeviceClient client = new(transport, log);
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await controller.ConnectAsync(new("test", "PUFFCO PEAK", -40), CancellationToken.None);
        await controller.ApplySessionOverrideAsync(OverrideRequest(), CancellationToken.None);
        Assert.IsNotNull(client.AppliedSessionOverride);

        await controller.SelectProfileAsync(1, CancellationToken.None);

        Assert.IsNull(client.AppliedSessionOverride);
        Assert.IsFalse(client.HasUntrackedSessionParameters);
        Assert.AreEqual(LoraxPaths.ActiveProfile, transport.Writes[^1].Path);
        Assert.AreEqual(1, transport.Writes[^1].Value[0]);
    }

    [TestMethod]
    public async Task Connect_UntrackedThcIsLoggedAndRecordedWithoutWriting()
    {
        RecordingLog log = new();
        ProbeTransport transport = new(log);
        transport.Values[LoraxPaths.ActiveProfileTemperature] = Float(220);
        transport.Values[LoraxPaths.ActiveProfileTime] = Float(60);
        LoraxDeviceClient client = new(transport, log);
        await using SessionController controller = new(client, new DeviceSafetyPolicy());

        await controller.ConnectAsync(new("test", "PUFFCO PEAK", -40), CancellationToken.None);

        Assert.IsTrue(client.HasUntrackedSessionParameters);
        Assert.HasCount(0, transport.Writes, "Connect-time detection must remain read-only.");
        StringAssert.Contains(
            log.Text,
            "connect-check result=mismatch live thc/temp=220C thc/time=60s; expected slot=0 temp=260C time=90s; writePerformed=false");
    }

    private static SessionOverride OverrideRequest() => new("Local only", 220, TimeSpan.FromSeconds(60), VaporLevel.Standard, ["#ABCDEF", "#123456"]);

    private static string WrittenPath(ReadOnlyMemory<byte> body)
    {
        int end = body.Span[3..].IndexOf((byte)0);
        return Encoding.UTF8.GetString(body.Span.Slice(3, end));
    }
}
