using System.Buffers.Binary;
using System.Text;
using DeskPuff.Bluetooth.Windows.Protocol;
using DeskPuff.Bluetooth.Windows.Transport;
using DeskPuff.Core.Devices;
using DeskPuff.Core.Safety;

namespace DeskPuff.Bluetooth.Windows.Tests;

[TestClass]
public sealed class LoraxDeviceClientTests
{
    private const string FirstDeviceColorBytes =
        "7B07FF6F4FEC5F8AD74CC1C72CEDB507FFAB07F9B607E9CE07D6E607C6F807BFFF72B2F2BC92D3E667ADFA358CFF077DFF0D8BFF14A9FF16CCFF0FEAFF07F7F307F6D707F5B207F68E07FB";
    private static readonly string[] FirstDevicePalette = DecodeExpectedColors(FirstDeviceColorBytes);
    private static readonly byte[] FirstDeviceLighting = BuildCompletePikaledFixture(FirstDeviceColorBytes);
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
        await using LoraxDeviceClient client = new(transport);
        await client.ConnectAsync(
            new DeviceCandidate("test-peak", "PUFFCO PEAK", -40),
            CancellationToken.None);

        await Assert.ThrowsExactlyAsync<DeviceSafetyException>(() =>
            client.StartSessionAsync(CancellationToken.None));

        Assert.AreEqual(0, transport.WriteCount);
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
    public async Task Profiles_UseColorsReadFromDeviceLightingCbor()
    {
        FakeLoraxTransport transport = new();
        await using LoraxDeviceClient client = new(transport);
        await client.ConnectAsync(
            new DeviceCandidate("test-peak", "PUFFCO PEAK", -40),
            CancellationToken.None);

        IReadOnlyList<HeatProfile> profiles = await client.GetProfilesAsync(CancellationToken.None);

        Assert.HasCount(4, profiles);
        Assert.IsTrue(profiles.All(profile => profile.HasDeviceColor));
        Assert.HasCount(25, profiles[0].ColorPalette);
        CollectionAssert.AreEqual(
            FirstDevicePalette,
            profiles[0].ColorPalette.ToArray());
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

        public bool IsConnected { get; private set; }

        public string AdvertisedName { get; init; } = "PUFFCO PEAK";

        public uint ModelCode { get; init; }

        public string DeviceName { get; init; } = "TEST PEAK";

        public byte[]? FourthProfileLighting { get; init; }

        public bool UnlockKeyMatched { get; private set; }

        public int WriteCount { get; private set; }

        public List<string> ReadPaths { get; } = [];

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
                LoraxOpcode.WriteShort => CountWrite(),
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
            byte[]? profileValue = ReadProfileValue(path);
            return profileValue ?? path switch
            {
                LoraxPaths.ModelCode => UInt32(ModelCode),
                LoraxPaths.DeviceName => Encoding.UTF8.GetBytes(DeviceName),
                LoraxPaths.FirmwareVersion => [0],
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

        private Task<ReadOnlyMemory<byte>> CountWrite()
        {
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

    private static byte[] BuildCompletePikaledFixture(string colorBytesHex)
    {
        byte[] header = Convert.FromHexString(
            "A1646C616D70A2646E616D656870696B616C65643265706172616DA165636F6C6F7258");
        byte[] colorBytes = Convert.FromHexString(colorBytesHex);
        return [.. header, checked((byte)colorBytes.Length), .. colorBytes];
    }

    private static string[] DecodeExpectedColors(string colorBytesHex)
    {
        byte[] bytes = Convert.FromHexString(colorBytesHex);
        return Enumerable.Range(0, bytes.Length / 3)
            .Select(index => $"#{bytes[index * 3]:X2}{bytes[(index * 3) + 1]:X2}{bytes[(index * 3) + 2]:X2}")
            .ToArray();
    }
}
