using System.Buffers.Binary;
using System.Text;
using DeskPuff.Bluetooth.Windows.Protocol;
using DeskPuff.Bluetooth.Windows.Transport;
using DeskPuff.Core.Devices;
using DeskPuff.Core.Diagnostics;
using DeskPuff.Core.Safety;

namespace DeskPuff.Bluetooth.Windows.Tests;

[TestClass]
public sealed partial class ThcProbeTests
{
    [TestMethod]
    [DataRow(LoraxPaths.ActiveProfileTime)]
    [DataRow(LoraxPaths.ActiveProfileTemperature)]
    public void NormalAdmission_StillRejectsThc(string path)
    {
        Assert.IsFalse(LoraxPaths.IsWriteAllowed(path));
        Assert.ThrowsExactly<DeviceWriteBlockedException>(() => LoraxProtocol.BuildWriteBody(path, 0, 0, new byte[4]));
        Assert.IsTrue(LoraxProtocol.BuildWriteBody(LoraxWriteContext.ThcParameters, path, 0, 0, new byte[4]).Length > 4);
    }

    [TestMethod]
    [DataRow("/p/app/thc/name")]
    [DataRow("/p/app/htr/tcmd")]
    [DataRow("/arbitrary")]
    [DataRow("/p/app/thc/time/")]
    public void ProbeAdmission_DoesNotAdmitOtherPaths(string path) =>
        Assert.ThrowsExactly<DeviceWriteBlockedException>(() =>
            LoraxProtocol.BuildWriteBody(LoraxWriteContext.ThcParameters, path, 0, 0, new byte[4]));

    [TestMethod]
    public async Task Sequence_IdentityThenDownwardWritesAndReselect_NoHeat()
    {
        RecordingLog log = new();
        ProbeTransport transport = new(log);
        await using LoraxDeviceClient client = new(transport, log);
        await client.ConnectAsync(new("test", "PUFFCO PEAK", -40), CancellationToken.None);
        await client.RunThcProbeAsync(CancellationToken.None);
        CollectionAssert.AreEqual(new[]
        {
            LoraxPaths.ActiveProfileTime, LoraxPaths.ActiveProfileTime,
            LoraxPaths.ActiveProfileTemperature, LoraxPaths.ActiveProfileTemperature, LoraxPaths.ActiveProfile, LoraxPaths.ActiveProfile,
        }, transport.Writes.Select(write => write.Path).ToArray());
        Assert.AreEqual(90f, Number(transport.Writes[0].Value));
        Assert.AreEqual(80f, Number(transport.Writes[1].Value));
        Assert.AreEqual(260f, Number(transport.Writes[2].Value));
        Assert.AreEqual((float)(260 - 2.777778), Number(transport.Writes[3].Value));
        foreach ((string path, byte[] value) in transport.Writes.Where(write => write.Path != LoraxPaths.ActiveProfile))
        {
            Assert.IsTrue(Number(value) <= (path == LoraxPaths.ActiveProfileTime ? 90 : 260));
        }
        Assert.AreEqual(1, transport.Writes[4].Value[0]);
        Assert.AreEqual(0, transport.Writes[5].Value[0]);
        Assert.AreEqual(6, log.Lines.Count(line => line.Contains("result=verified", StringComparison.Ordinal)));
        StringAssert.Contains(log.Text, "THC PROBE RESELECT phase=other slot=1 observedTime=60 expectedTime=60 observedTemperature=240 expectedTemperature=240 verified=true matchesSlotTime=true matchesSlotTemperature=true");
        StringAssert.Contains(log.Text, "THC PROBE RESELECT phase=original slot=0 observedTime=90 expectedTime=90 observedTemperature=260 expectedTemperature=260");
        Assert.IsTrue(log.Lines.FindIndex(line => line.Contains("phase=other", StringComparison.Ordinal)) <
            log.Lines.FindLastIndex(line => line.StartsWith("LORAX WRITE FRAME", StringComparison.Ordinal)),
            "Intermediate reads must be logged before the original slot is selected.");
        StringAssert.Contains(log.Text, "writesAccepted=true slotValuesUnchanged=true verifiedWrites=6 attemptedWrites=6 slotComparisons=2 unchangedSlotComparisons=2 reselectRestoredTime=true reselectRestoredTemperature=true");
        AssertNoHeat(log);
    }

    [TestMethod]
    public async Task Trace_SuppressesAdmittedFramesButDoesNotAdmitNormalThc()
    {
        RecordingLog log = new();
        ProbeTransport transport = new(log, trace: true);
        await using LoraxDeviceClient client = new(transport, log, traceWrites: true);
        await client.ConnectAsync(new("test", "PUFFCO PEAK", -40), CancellationToken.None);
        foreach (string path in new[] { LoraxPaths.ActiveProfileTime, LoraxPaths.ActiveProfileTemperature })
        {
            await Assert.ThrowsExactlyAsync<DeviceWriteBlockedException>(async () =>
                await transport.RunCommandAsync(LoraxOpcode.WriteShort,
                    LoraxProtocol.BuildWriteBody(path, 0, 0, new byte[4]), 0, CancellationToken.None));
        }
        await client.RunThcProbeAsync(CancellationToken.None);
        Assert.HasCount(0, transport.Writes);
        Assert.AreEqual(6, log.Lines.Count(line => line.StartsWith("TRACE-WRITE SUPPRESSED", StringComparison.Ordinal)));
        Assert.AreEqual(6, log.Lines.Count(line => line.Contains("result=skipped reason=trace-write-suppressed", StringComparison.Ordinal)));
        Assert.AreEqual(6, log.Lines.Count(line => line.StartsWith("LORAX WRITE FRAME", StringComparison.Ordinal) && line.Contains("frameHex=", StringComparison.Ordinal)));
        StringAssert.Contains(log.Text, "result=trace-only hardwareEvidence=none");
        Assert.IsFalse(log.Text.Contains("writesAccepted=true", StringComparison.Ordinal));
        StringAssert.Contains(log.Text, "writesAccepted=false slotValuesUnchanged=false verifiedWrites=0 attemptedWrites=6");
        StringAssert.Contains(log.Text, "verified=false matchesSlotTime=unknown matchesSlotTemperature=unknown");
        AssertNoHeat(log);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    public async Task NonIdleAtStartupOrBeforeAnyLaterWrite_Halts(int writesBeforeStateChange)
    {
        RecordingLog log = new();
        ProbeTransport transport = new(log) { NonIdleAfterWrites = writesBeforeStateChange };
        await using LoraxDeviceClient client = new(transport, log);
        await client.ConnectAsync(new("test", "PUFFCO PEAK", -40), CancellationToken.None);
        DeviceSafetyException exception = await Assert.ThrowsExactlyAsync<DeviceSafetyException>(() => client.RunThcProbeAsync(CancellationToken.None));
        StringAssert.Contains(exception.Message, "stat/id == 5");
        Assert.HasCount(writesBeforeStateChange, transport.Writes);
        AssertNoHeat(log);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    [DataRow(6)]
    public async Task NonZeroReply_HaltsWithoutRemainingWrites(int rejectedWrite)
    {
        RecordingLog log = new();
        ProbeTransport transport = new(log) { RejectWrite = rejectedWrite };
        await using LoraxDeviceClient client = new(transport, log);
        await client.ConnectAsync(new("test", "PUFFCO PEAK", -40), CancellationToken.None);
        IOException exception = await Assert.ThrowsExactlyAsync<IOException>(() => client.RunThcProbeAsync(CancellationToken.None));
        Assert.AreEqual("The device rejected the Lorax write with status 0x7A.", exception.Message);
        StringAssert.Contains(log.Text, "status=0x7A");
        StringAssert.Contains(log.Text, "result=failed reason=\"The device rejected the Lorax write with status 0x7A.\"");
        Assert.HasCount(rejectedWrite, transport.Writes);
        StringAssert.Contains(log.Text, $"writesAccepted=false slotValuesUnchanged={(rejectedWrite >= 5 ? "true" : "false")} verifiedWrites={rejectedWrite - 1} attemptedWrites={rejectedWrite}");
        AssertNoHeat(log);
    }

    [TestMethod]
    public async Task ReadBackMismatch_Halts()
    {
        RecordingLog log = new();
        ProbeTransport transport = new(log) { Mismatch = true };
        await using LoraxDeviceClient client = new(transport, log);
        await client.ConnectAsync(new("test", "PUFFCO PEAK", -40), CancellationToken.None);
        await Assert.ThrowsExactlyAsync<IOException>(() => client.RunThcProbeAsync(CancellationToken.None));
        Assert.HasCount(1, transport.Writes);
        StringAssert.Contains(log.Text, "result=failed");
        StringAssert.Contains(log.Text, "writesAccepted=false slotValuesUnchanged=false verifiedWrites=0 attemptedWrites=1");
        AssertNoHeat(log);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task BelowMinimumReduction_IsRefusedWithoutClamping(bool duration)
    {
        RecordingLog log = new();
        ProbeTransport transport = new(log);
        transport.Values[duration ? LoraxPaths.ActiveProfileTime : LoraxPaths.ActiveProfileTemperature] = Float(duration ? 35 : 205);
        await using LoraxDeviceClient client = new(transport, log);
        await client.ConnectAsync(new("test", "PUFFCO PEAK", -40), CancellationToken.None);
        DeviceSafetyException exception = await Assert.ThrowsExactlyAsync<DeviceSafetyException>(() => client.RunThcProbeAsync(CancellationToken.None));
        StringAssert.Contains(exception.Message, duration ? "MinimumDuration" : "MinimumTemperatureCelsius");
        Assert.HasCount(duration ? 1 : 3, transport.Writes);
        AssertNoHeat(log);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ChangedSlot_HypothesisFailsWithoutReselectOrOtherWrites(bool duration)
    {
        RecordingLog log = new();
        ProbeTransport transport = new(log) { AliasPath = duration ? LoraxPaths.ActiveProfileTime : LoraxPaths.ActiveProfileTemperature };
        await using LoraxDeviceClient client = new(transport, log);
        await client.ConnectAsync(new("test", "PUFFCO PEAK", -40), CancellationToken.None);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => client.RunThcProbeAsync(CancellationToken.None));
        Assert.HasCount(duration ? 2 : 4, transport.Writes);
        StringAssert.Contains(log.Text, "result=alias-detected");
        StringAssert.Contains(log.Text, "writesAccepted=false slotValuesUnchanged=false");
        StringAssert.Contains(log.Text, $"slotComparisons={(duration ? 1 : 2)} unchangedSlotComparisons={(duration ? 0 : 1)}");
        AssertNoHeat(log);
    }

    [TestMethod]
    public async Task UnverifiedFirmware_RemainsBlocked()
    {
        RecordingLog log = new();
        ProbeTransport transport = new(log);
        transport.Values[LoraxPaths.FirmwareVersion] = [40];
        await using LoraxDeviceClient client = new(transport, log);
        await client.ConnectAsync(new("test", "PUFFCO PEAK", -40), CancellationToken.None);
        await Assert.ThrowsExactlyAsync<DeviceSafetyException>(() => client.RunThcProbeAsync(CancellationToken.None));
        Assert.HasCount(0, transport.Writes);
        StringAssert.Contains(log.Text, "WRITE BLOCKED");
        AssertNoHeat(log);
    }

    [TestMethod]
    public async Task NoDifferentValidSlot_RefusesBeforeAnyWrite()
    {
        RecordingLog log = new();
        ProbeTransport transport = new(log);
        for (int slot = 1; slot < 4; slot++) { transport.Values[LoraxPaths.ProfileTime(slot)] = Float(0); }
        await using LoraxDeviceClient client = new(transport, log);
        await client.ConnectAsync(new("test", "PUFFCO PEAK", -40), CancellationToken.None);
        DeviceSafetyException exception = await Assert.ThrowsExactlyAsync<DeviceSafetyException>(() => client.RunThcProbeAsync(CancellationToken.None));
        Assert.AreEqual("THC probe requires a different valid slot; refusing identity reselect.", exception.Message);
        Assert.HasCount(0, transport.Writes);
        StringAssert.Contains(log.Text, "writesAccepted=false slotValuesUnchanged=false verifiedWrites=0 attemptedWrites=0");
        AssertNoHeat(log);
    }

    [TestMethod]
    public async Task InvalidFirstAlternate_IsSkippedForDifferentValidSlot()
    {
        RecordingLog log = new();
        ProbeTransport transport = new(log);
        transport.Values[LoraxPaths.ProfileTime(1)] = Float(0);
        await using LoraxDeviceClient client = new(transport, log);
        await client.ConnectAsync(new("test", "PUFFCO PEAK", -40), CancellationToken.None);
        await client.RunThcProbeAsync(CancellationToken.None);
        Assert.AreEqual(2, transport.Writes[4].Value[0]);
        Assert.AreEqual(0, transport.Writes[5].Value[0]);
        StringAssert.Contains(log.Text, "phase=other slot=2 observedTime=100 expectedTime=100");
        AssertNoHeat(log);
    }

    private static void AssertNoHeat(RecordingLog log)
    {
        Assert.IsFalse(log.Lines.Any(line => line.StartsWith("LORAX WRITE FRAME", StringComparison.Ordinal) &&
            line.Contains("path=\"/p/app/mc\"", StringComparison.Ordinal)), "No mc frame may even be constructed.");
    }

    private static float Number(byte[] bytes) => BinaryPrimitives.ReadSingleLittleEndian(bytes);
    private static byte[] Float(float value) => LoraxValueCodec.WriteSingle(value);

    private sealed class RecordingLog : IDiagnosticLog
    {
        internal List<string> Lines { get; } = [];
        internal string Text => string.Join(Environment.NewLine, Lines);
        public void Write(string message) => Lines.Add(message);
        public void WriteException(string context, Exception exception) => Lines.Add($"{context}: {exception.Message}");
    }

    // Real managed framing, status parsing and trace suppression; only the wire is fake.
    private sealed class ProbeTransport : ILoraxTransport
    {
        private readonly SidecarLoraxTransport inner;
        internal List<(string Path, byte[] Value)> Writes { get; } = [];
        internal Dictionary<string, byte[]> Values { get; } = new(StringComparer.Ordinal)
        {
            [LoraxPaths.ModelCode] = [13, 0, 0, 0],
            [LoraxPaths.FirmwareVersion] = [39],
            [LoraxPaths.DeviceName] = Encoding.UTF8.GetBytes("TEST"),
            [LoraxPaths.ActiveProfile] = [0],
            [LoraxPaths.ActiveProfileName] = Encoding.UTF8.GetBytes("TEST"),
            [LoraxPaths.ActiveProfileTime] = Float(90),
            [LoraxPaths.ActiveProfileTemperature] = Float(260),
            [LoraxPaths.ProfileTime(0)] = Float(90),
            [LoraxPaths.ProfileTemperature(0)] = Float(260),
            [LoraxPaths.ProfileTime(1)] = Float(60),
            [LoraxPaths.ProfileTemperature(1)] = Float(240),
            [LoraxPaths.ProfileTime(2)] = Float(100),
            [LoraxPaths.ProfileTemperature(2)] = Float(250),
            [LoraxPaths.ProfileTime(3)] = Float(120),
            [LoraxPaths.ProfileTemperature(3)] = Float(230),
            [LoraxPaths.BatteryStateOfCharge] = Float(80),
            [LoraxPaths.BatteryChargeState] = [4],
            [LoraxPaths.ChamberType] = [2],
            [LoraxPaths.OperatingState] = [5],
            [LoraxPaths.HeaterTemperature] = Float(25),
            [LoraxPaths.StateTotalTime] = Float(90),
            [LoraxPaths.StateElapsedTime] = Float(0),
            [LoraxPaths.TemperatureOverride] = Float(0),
            [LoraxPaths.TimeOverride] = Float(0),
            [LoraxPaths.HeaterTargetTemperature] = Float(260),
        };
        internal int NonIdleAfterWrites { get; init; } = int.MaxValue;
        internal int RejectWrite { get; init; }
        internal bool Mismatch { get; init; }
        internal string? AliasPath { get; init; }
        internal Action<string>? BeforeRead { get; set; }
        internal bool AllowModeCommand { get; init; }
        internal Action<string, byte[]>? AfterWrite { get; set; }
        internal Func<LoraxOpcode, ReadOnlyMemory<byte>, Task>? BeforeCommandAsync { get; set; }

        internal ProbeTransport(RecordingLog log, bool trace = false) =>
            inner = new(log, trace, SendFrameAsync);

        public bool IsConnected => inner.IsConnected;
        public string AdvertisedName => "PUFFCO PEAK";
        public Task<IReadOnlyList<DeviceCandidate>> ScanAsync(TimeSpan duration, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DeviceCandidate>>([]);
        public Task ConnectAsync(DeviceCandidate candidate, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken cancellationToken) => inner.DisconnectAsync(cancellationToken);
        public Task TriggerBondingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public async Task<ReadOnlyMemory<byte>> RunCommandAsync(LoraxOpcode opcode, ReadOnlyMemory<byte> body, int maximumReplyLength, CancellationToken cancellationToken)
        {
            if (BeforeCommandAsync is not null) { await BeforeCommandAsync(opcode, body); }
            return await inner.RunCommandAsync(opcode, body, maximumReplyLength, cancellationToken);
        }
        public ValueTask DisposeAsync() => inner.DisposeAsync();

        private Task<ReadOnlyMemory<byte>> SendFrameAsync(ReadOnlyMemory<byte> frame, ushort sequence, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadOnlySpan<byte> body = frame.Span[3..];
            byte[] payload = [];
            byte status = 0;
            switch ((LoraxOpcode)frame.Span[2])
            {
                case LoraxOpcode.GetAccessSeed:
                    payload = new byte[16];
                    break;
                case LoraxOpcode.UnlockAccess:
                    break;
                case LoraxOpcode.ReadShort:
                    string readPath = Encoding.UTF8.GetString(body[4..]);
                    BeforeRead?.Invoke(readPath);
                    byte[] value = readPath == LoraxPaths.OperatingState && Writes.Count >= NonIdleAfterWrites
                        ? [8] : Values[readPath];
                    int offset = BinaryPrimitives.ReadUInt16LittleEndian(body);
                    int size = BinaryPrimitives.ReadUInt16LittleEndian(body[2..]);
                    payload = value.Skip(offset).Take(size).ToArray();
                    break;
                case LoraxOpcode.WriteShort:
                    int end = body[3..].IndexOf((byte)0);
                    string path = Encoding.UTF8.GetString(body.Slice(3, end));
                    byte[] written = body[(4 + end)..].ToArray();
                    if (!AllowModeCommand) { Assert.AreNotEqual(LoraxPaths.ModeCommand, path); }
                    Writes.Add((path, written));
                    if (Writes.Count == RejectWrite) { status = 0x7A; break; }
                    Values[path] = Mismatch ? Float(-1) : written;
                    if (path == AliasPath)
                    {
                        Values[path == LoraxPaths.ActiveProfileTime ? LoraxPaths.ProfileTime(0) : LoraxPaths.ProfileTemperature(0)] = written;
                    }
                    if (path == LoraxPaths.ActiveProfile)
                    {
                        Values[LoraxPaths.ActiveProfileTime] = Values[LoraxPaths.ProfileTime(written[0])];
                        Values[LoraxPaths.ActiveProfileTemperature] = Values[LoraxPaths.ProfileTemperature(written[0])];
                    }
                    AfterWrite?.Invoke(path, written);
                    break;
                default:
                    throw new InvalidOperationException("Unexpected probe opcode.");
            }
            byte[] reply = new byte[3 + payload.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(reply, sequence);
            reply[2] = status;
            payload.CopyTo(reply, 3);
            return Task.FromResult<ReadOnlyMemory<byte>>(reply);
        }
    }
}
