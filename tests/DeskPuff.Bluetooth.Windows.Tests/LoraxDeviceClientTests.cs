using System.Buffers.Binary;
using System.Text;
using DeskPuff.Bluetooth.Windows.Protocol;
using DeskPuff.Bluetooth.Windows.Transport;
using DeskPuff.Core.Devices;
using DeskPuff.Core.Diagnostics;
using DeskPuff.Core.Safety;

namespace DeskPuff.Bluetooth.Windows.Tests;

[TestClass]
public sealed class LoraxDeviceClientTests
{
    private const string FirstDeviceColorBytes =
        "7B07FF6F4FEC5F8AD74CC1C72CEDB507FFAB07F9B607E9CE07D6E607C6F807BFFF72B2F2BC92D3E667ADFA358CFF077DFF0D8BFF14A9FF16CCFF0FEAFF07F7F307F6D707F5B207F68E07FB";
    private static readonly string[] FirstDevicePalette =
        ["#7B07FF", "#07FFAB", "#07BFFF", "#FF077D", "#FF07F7"];
    private static readonly byte[] FirstDeviceLighting = BuildCompletePikaledFixtureWithUserColors(
        FirstDeviceColorBytes,
        ["#7b07ff", "#07ffab", "#07bfff", "#ff077d", "#ff07f7"]);
    private static readonly string[] FourthProfileFallback = ["#FFFFFF"];
    private static readonly string[] ProfilePrimaryColors = ["#102030", "#202030", "#302030", "#402030"];
    private static readonly byte[] MigrationPathLighting = Convert.FromHexString(
        "A1646C616D70A2646E616D65676D696772746E3165706172616DA165706174687380");

    [TestMethod]
    public async Task Connect_AuthenticatesAndKeepsUnknownFirmwareReadOnly()
    {
        FakeLoraxTransport transport = new();
        await using LoraxDeviceClient client = new(transport);

        await client.ConnectAsync(
            new DeviceCandidate("test-peak", "PUFFCO PEAK", -40),
            CancellationToken.None);

        Assert.IsTrue(transport.UnlockKeyMatched);
        Assert.AreEqual(DeviceFamily.PeakPro, client.Snapshot.Identity!.Family);
        Assert.AreEqual(DeviceConnectionState.ConnectedReadOnly, client.Snapshot.ConnectionState);
        Assert.IsTrue(client.Snapshot.IsAuthenticated);
        Assert.IsFalse(client.Snapshot.IsFirmwareVerified);
        Assert.AreEqual(0, transport.WriteCount);
        Assert.IsFalse(transport.ReadPaths.Contains("/p/sys/hw/ser", StringComparer.Ordinal));
    }

    [TestMethod]
    public async Task CharacterisedSnapshot_HasSaneLimitsAndReachesTheWriteActionSwitch()
    {
        FakeLoraxTransport transport = new()
        {
            ModelCode = 13,
            FirmwareRevision = 39,
            DeviceName = "PEAKSHI V2",
        };
        await using LoraxDeviceClient client = new(transport);

        await client.ConnectAsync(
            new DeviceCandidate("test-verified-peak", "PUFFCO PEAK", -40),
            CancellationToken.None);

        Assert.IsTrue(client.Snapshot.IsFirmwareVerified);
        Assert.AreEqual(DeviceConnectionState.ConnectedControlEnabled, client.Snapshot.ConnectionState);
        DeviceLimits? limits = client.Snapshot.Limits;
        Assert.IsNotNull(limits);
        Assert.IsTrue(limits.IsSane);

        await client.SelectProfileAsync(0, CancellationToken.None);

        Assert.AreEqual(1, transport.WriteCount, "The characterised snapshot must reach the allowed write action.");
    }

    [TestMethod]
    public async Task LanternWrites_SendOneThenZeroWithoutReadBackAndLogSkippedVerification()
    {
        FakeLoraxTransport transport = new()
        {
            ModelCode = 13,
            FirmwareRevision = 39,
            DeviceName = "PEAKSHI V2",
        };
        RecordingDiagnosticLog diagnosticLog = new();
        await using LoraxDeviceClient client = new(transport, diagnosticLog);
        await client.ConnectAsync(
            new DeviceCandidate("test-verified-peak", "PUFFCO PEAK", -40),
            CancellationToken.None);
        transport.ReadPaths.Clear();

        await client.SetLanternModeAsync(enabled: true, CancellationToken.None);
        await client.SetLanternModeAsync(enabled: false, CancellationToken.None);

        Assert.AreEqual(2, transport.WriteCount);
        Assert.HasCount(2, transport.Writes);
        Assert.AreEqual(LoraxPaths.LanternMode, transport.Writes[0].Path);
        Assert.AreEqual(LoraxPaths.LanternMode, transport.Writes[1].Path);
        CollectionAssert.AreEqual(new byte[] { 1 }, transport.Writes[0].Value);
        CollectionAssert.AreEqual(new byte[] { 0 }, transport.Writes[1].Value);
        Assert.IsFalse(transport.ReadPaths.Contains(LoraxPaths.LanternMode, StringComparer.Ordinal));
        StringAssert.Contains(
            diagnosticLog.Text,
            $"WRITE VERIFICATION path=\"{LoraxPaths.LanternMode}\" " +
            "result=skipped reason=read-back-unsupported");
    }

    [TestMethod]
    public async Task StealthWrite_NormalPathStillReadsBackAndLogsVerifiedSuccess()
    {
        FakeLoraxTransport transport = new()
        {
            ModelCode = 13,
            FirmwareRevision = 39,
            DeviceName = "PEAKSHI V2",
        };
        RecordingDiagnosticLog diagnosticLog = new();
        await using LoraxDeviceClient client = new(transport, diagnosticLog);
        await client.ConnectAsync(
            new DeviceCandidate("test-verified-peak", "PUFFCO PEAK", -40),
            CancellationToken.None);
        transport.ReadPaths.Clear();

        await client.SetStealthModeAsync(enabled: true, CancellationToken.None);

        Assert.IsTrue(transport.ReadPaths.Contains(LoraxPaths.StealthMode, StringComparer.Ordinal));
        StringAssert.Contains(
            diagnosticLog.Text,
            $"WRITE VERIFICATION path=\"{LoraxPaths.StealthMode}\" result=verified length=1");
    }

    [TestMethod]
    public async Task StealthWrite_NormalPathStillFailsWhenReadBackDisagrees()
    {
        FakeLoraxTransport transport = new()
        {
            ModelCode = 13,
            FirmwareRevision = 39,
            DeviceName = "PEAKSHI V2",
            MismatchedReadBackPath = LoraxPaths.StealthMode,
        };
        RecordingDiagnosticLog diagnosticLog = new();
        await using LoraxDeviceClient client = new(transport, diagnosticLog);
        await client.ConnectAsync(
            new DeviceCandidate("test-verified-peak", "PUFFCO PEAK", -40),
            CancellationToken.None);

        await Assert.ThrowsExactlyAsync<IOException>(() =>
            client.SetStealthModeAsync(enabled: true, CancellationToken.None));

        Assert.IsTrue(transport.ReadPaths.Contains(LoraxPaths.StealthMode, StringComparer.Ordinal));
        StringAssert.Contains(
            diagnosticLog.Text,
            $"WRITE VERIFICATION path=\"{LoraxPaths.StealthMode}\" result=failed");
    }

    [TestMethod]
    public async Task Connect_ReportsBatteryPercentageFromStateOfChargeInsteadOfCapacity()
    {
        FakeLoraxTransport transport = new();
        await using LoraxDeviceClient client = new(transport);

        await client.ConnectAsync(
            new DeviceCandidate("test-peak", "PUFFCO PEAK", -40),
            CancellationToken.None);

        Assert.AreEqual(
            64.7d,
            client.Snapshot.BatteryPercent,
            0.1d,
            "BatteryPercent must come from state of charge.");
        Assert.AreNotEqual(
            100d,
            client.Snapshot.BatteryPercent,
            "Capacity must not be treated as a percentage.");
    }

    [TestMethod]
    public async Task Refresh_ReadsOnlyChangingTelemetryWithinBatteryInterval()
    {
        FakeLoraxTransport transport = new();
        await using LoraxDeviceClient client = new(transport);
        await client.ConnectAsync(
            new DeviceCandidate("test-peak", "PUFFCO PEAK", -40),
            CancellationToken.None);
        transport.ReadPaths.Clear();

        await client.RefreshAsync(CancellationToken.None);

        CollectionAssert.AreEquivalent(
            new[]
            {
                LoraxPaths.ChamberType,
                LoraxPaths.OperatingState,
                LoraxPaths.ActiveProfile,
                LoraxPaths.HeaterTemperature,
                LoraxPaths.StateTotalTime,
                LoraxPaths.StateElapsedTime,
            },
            transport.ReadPaths);
    }

    [TestMethod]
    public async Task Start_UnverifiedFirmwareNeverReachesTransportWrite()
    {
        FakeLoraxTransport transport = new();
        RecordingDiagnosticLog diagnosticLog = new();
        await using LoraxDeviceClient client = new(
            transport,
            diagnosticLog,
            traceWrites: true);
        await client.ConnectAsync(
            new DeviceCandidate("test-peak", "PUFFCO PEAK", -40),
            CancellationToken.None);

        await Assert.ThrowsExactlyAsync<DeviceSafetyException>(() =>
            client.StartSessionAsync(CancellationToken.None));

        Assert.AreEqual(0, transport.WriteCount);
        StringAssert.Contains(
            diagnosticLog.Text,
            "WRITE BLOCKED action=StartSession",
            "Trace mode must not bypass or hide an existing policy denial.");
    }

    [TestMethod]
    public async Task AppEnabledProxyName_IsRecognizedButStillReadOnly()
    {
        FakeLoraxTransport transport = new()
        {
            AdvertisedName = "PUFFCO PROXY",
            ModelCode = 999,
            DeviceName = "PROXY",
        };
        await using LoraxDeviceClient client = new(transport);

        await client.ConnectAsync(
            new DeviceCandidate("test-proxy", "PUFFCO PROXY", -40),
            CancellationToken.None);

        Assert.AreEqual(DeviceFamily.NewProxy, client.Snapshot.Identity!.Family);
        Assert.AreEqual(DeviceConnectionState.ConnectedReadOnly, client.Snapshot.ConnectionState);
        Assert.AreEqual(0, transport.WriteCount);
    }

    [TestMethod]
    public async Task Profiles_UseAuthoredColorsAndLogPaletteSource()
    {
        FakeLoraxTransport transport = new();
        RecordingDiagnosticLog diagnosticLog = new();
        await using LoraxDeviceClient client = new(transport, diagnosticLog);
        await client.ConnectAsync(
            new DeviceCandidate("test-peak", "PUFFCO PEAK", -40),
            CancellationToken.None);

        IReadOnlyList<HeatProfile> profiles = await client.GetProfilesAsync(CancellationToken.None);

        Assert.HasCount(4, profiles);
        Assert.IsTrue(profiles.All(profile => profile.HasDeviceColor));
        Assert.HasCount(5, profiles[0].ColorPalette);
        CollectionAssert.AreEqual(
            FirstDevicePalette,
            profiles[0].ColorPalette.ToArray());
        Assert.AreEqual("DISCO", profiles[0].ColorwayName);
        Assert.AreEqual("pikaled2", profiles[0].LampName);
        StringAssert.Contains(
            diagnosticLog.Text,
            "PROFILE PALETTE index=0 source=meta.userColors colorCount=5 " +
            "moodName=\"DISCO\" lampName=\"pikaled2\"");
        Assert.AreEqual(
            4,
            diagnosticLog.Text.Split("PROFILE PALETTE index=", StringSplitOptions.None).Length - 1,
            "Each profile read must produce exactly one palette-source diagnostic line.");
        Assert.IsTrue(transport.ReadPaths.Contains(LoraxPaths.ProfileColor(0), StringComparer.Ordinal));
    }

    [TestMethod]
    public async Task Profiles_PathAnimationWithoutColorUsesNormalFallback()
    {
        FakeLoraxTransport transport = new() { FourthProfileLighting = MigrationPathLighting };
        await using LoraxDeviceClient client = new(transport);
        await client.ConnectAsync(
            new DeviceCandidate("test-peak", "PUFFCO PEAK", -40),
            CancellationToken.None);

        IReadOnlyList<HeatProfile> profiles = await client.GetProfilesAsync(CancellationToken.None);

        Assert.IsFalse(profiles[3].HasDeviceColor);
        CollectionAssert.AreEqual(FourthProfileFallback, profiles[3].ColorPalette.ToArray());
        Assert.AreEqual(0, transport.WriteCount);
    }

    private sealed class FakeLoraxTransport : ILoraxTransport
    {
        private static readonly byte[] Seed = Enumerable.Range(0, 16)
            .Select(value => (byte)value)
            .ToArray();
        private readonly Dictionary<string, byte[]> writtenValues = new(StringComparer.Ordinal);

        public bool IsConnected { get; private set; }

        public string AdvertisedName { get; init; } = "PUFFCO PEAK";

        public uint ModelCode { get; init; }

        public byte FirmwareRevision { get; init; }

        public string DeviceName { get; init; } = "TEST PEAK";

        public byte[]? FourthProfileLighting { get; init; }

        public string? MismatchedReadBackPath { get; init; }

        public bool UnlockKeyMatched { get; private set; }

        public int WriteCount { get; private set; }

        public List<string> ReadPaths { get; } = [];

        public List<(string Path, byte[] Value)> Writes { get; } = [];

        public Task<IReadOnlyList<DeviceCandidate>> ScanAsync(
            TimeSpan duration,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DeviceCandidate>>([]);

        public Task ConnectAsync(DeviceCandidate candidate, CancellationToken cancellationToken)
        {
            IsConnected = true;
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken)
        {
            IsConnected = false;
            return Task.CompletedTask;
        }

        public Task TriggerBondingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<ReadOnlyMemory<byte>> RunCommandAsync(
            LoraxOpcode opcode,
            ReadOnlyMemory<byte> body,
            int maximumReplyLength,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return opcode switch
            {
                LoraxOpcode.GetAccessSeed => Result(Seed),
                LoraxOpcode.UnlockAccess => Unlock(body),
                LoraxOpcode.ReadShort => Read(body),
                LoraxOpcode.WriteShort => CountWrite(body),
                _ => throw new InvalidOperationException($"Unexpected fake opcode {opcode}."),
            };
        }

        public ValueTask DisposeAsync()
        {
            IsConnected = false;
            return ValueTask.CompletedTask;
        }

        private Task<ReadOnlyMemory<byte>> Unlock(ReadOnlyMemory<byte> body)
        {
            UnlockKeyMatched = body.Span.SequenceEqual(LoraxProtocol.DeriveUnlockKey(Seed));
            return Result([]);
        }

        private Task<ReadOnlyMemory<byte>> Read(ReadOnlyMemory<byte> body)
        {
            ushort offset = BinaryPrimitives.ReadUInt16LittleEndian(body.Span);
            ushort size = BinaryPrimitives.ReadUInt16LittleEndian(body.Span[2..]);
            string path = Encoding.UTF8.GetString(body.Span[4..]);
            ReadPaths.Add(path);
            byte[] value = ReadValue(path);
            if (offset >= value.Length)
            {
                return Result([]);
            }

            int length = Math.Min(size, value.Length - offset);
            return Result(value.AsSpan(offset, length).ToArray());
        }

        private byte[] ReadValue(string path)
        {
            if (writtenValues.TryGetValue(path, out byte[]? writtenValue))
            {
                return string.Equals(path, MismatchedReadBackPath, StringComparison.Ordinal)
                    ? writtenValue.Select(value => (byte)(value ^ 0xFF)).ToArray()
                    : writtenValue;
            }

            byte[]? profileValue = ReadProfileValue(path);
            return profileValue ?? path switch
            {
                LoraxPaths.ModelCode => UInt32(ModelCode),
                LoraxPaths.DeviceName => Encoding.UTF8.GetBytes(DeviceName),
                LoraxPaths.FirmwareVersion => [FirmwareRevision],
                LoraxPaths.BatteryStateOfCharge => Single(64.679f),
                LoraxPaths.BatteryCapacity => Single(6018.443f),
                LoraxPaths.BatteryChargeState => [4],
                LoraxPaths.ChamberType => [2],
                LoraxPaths.OperatingState => [(byte)DeviceOperatingState.Idle],
                LoraxPaths.ActiveProfile => [0],
                LoraxPaths.ActiveProfileName => Encoding.UTF8.GetBytes("BLUE"),
                LoraxPaths.ActiveProfileTemperature => Single(260),
                LoraxPaths.ActiveProfileTime => Single(40),
                LoraxPaths.HeaterTemperature => Single(25),
                LoraxPaths.StateTotalTime => Single(40),
                LoraxPaths.StateElapsedTime => Single(0),
                _ => throw new InvalidOperationException($"Unexpected fake read path {path}."),
            };
        }

        private byte[]? ReadProfileValue(string path)
        {
            string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments is not ["u", "app", "hc", _, _] ||
                !int.TryParse(segments[3], out int index) ||
                index is < 0 or > 3)
            {
                return null;
            }

            return segments[4] switch
            {
                "name" => Encoding.UTF8.GetBytes($"PROFILE {index + 1}"),
                "temp" => Single(260 + index),
                "time" => Single(40 + index),
                "btmp" => Single(5),
                "btim" => Single(10),
                "colr" when index == 0 => FirstDeviceLighting,
                "colr" when index == 3 && FourthProfileLighting is not null => FourthProfileLighting,
                "colr" => ProfileLightingCodec.EncodeSolid(
                    [ProfilePrimaryColors[index], "#FFFFFF"]),
                _ => null,
            };
        }

        private Task<ReadOnlyMemory<byte>> CountWrite(ReadOnlyMemory<byte> body)
        {
            int terminator = body.Span[3..].IndexOf((byte)0);
            if (terminator < 0)
            {
                throw new InvalidOperationException("Fake write path was not terminated.");
            }

            string path = Encoding.UTF8.GetString(body.Span.Slice(3, terminator));
            int valueOffset = 4 + terminator;
            byte[] value = body[valueOffset..].ToArray();
            writtenValues[path] = value;
            Writes.Add((path, value));
            WriteCount++;
            return Result([]);
        }

        private static Task<ReadOnlyMemory<byte>> Result(byte[] value) =>
            Task.FromResult<ReadOnlyMemory<byte>>(value);

        private static byte[] UInt32(uint value)
        {
            byte[] bytes = new byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            return bytes;
        }

        private static byte[] Single(float value)
        {
            byte[] bytes = new byte[sizeof(float)];
            BinaryPrimitives.WriteSingleLittleEndian(bytes, value);
            return bytes;
        }
    }

    private sealed class RecordingDiagnosticLog : IDiagnosticLog
    {
        private readonly StringBuilder text = new();

        internal string Text => text.ToString();

        public void Write(string message) => text.AppendLine(message);

        public void WriteException(string context, Exception exception) =>
            text.Append(context)
                .Append(": ")
                .Append(exception.GetType().Name)
                .Append(": ")
                .AppendLine(exception.Message);
    }

    private static byte[] BuildCompletePikaledFixtureWithUserColors(
        string colorBytesHex,
        string[] userColors)
    {
        byte[] header = Convert.FromHexString(
            "A2646C616D70A2646E616D656870696B616C65643265706172616DA165636F6C6F7258");
        byte[] colorBytes = Convert.FromHexString(colorBytesHex);
        byte[] metadataHeader = Convert.FromHexString(
            "646D657461A2686D6F6F644E616D6565444953434F6A75736572436F6C6F7273");
        List<byte> fixture = new(header.Length + colorBytes.Length + metadataHeader.Length + 64);
        fixture.AddRange(header);
        fixture.Add(checked((byte)colorBytes.Length));
        fixture.AddRange(colorBytes);
        fixture.AddRange(metadataHeader);
        fixture.Add((byte)(0x80 | userColors.Length));
        foreach (string color in userColors)
        {
            byte[] encoded = Encoding.UTF8.GetBytes(color);
            fixture.Add((byte)(0x60 | encoded.Length));
            fixture.AddRange(encoded);
        }

        return fixture.ToArray();
    }
}
