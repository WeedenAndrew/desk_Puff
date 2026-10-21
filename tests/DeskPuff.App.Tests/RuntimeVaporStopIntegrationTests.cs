using System.Buffers.Binary;
using System.Text;
using DeskPuff.App.ViewModels;
using DeskPuff.Bluetooth.Windows;
using DeskPuff.Bluetooth.Windows.Protocol;
using DeskPuff.Bluetooth.Windows.Transport;
using DeskPuff.Core.Devices;
using DeskPuff.Core.Diagnostics;
using DeskPuff.Core.Safety;
using DeskPuff.Core.Sessions;

namespace DeskPuff.App.Tests;

[TestClass]
public sealed class RuntimeVaporStopIntegrationTests
{
    [TestMethod]
    public async Task ViewModelStopRestoresDifferentOriginalRuntimeExactlyOnce()
    {
        string root = Path.Combine(Path.GetTempPath(), "desk-puff-real-stop", Guid.NewGuid().ToString("N"));
        try
        {
            Wire wire = new();
            LoraxDeviceClient client = new(wire);
            await using SessionController controller = new(client, new DeviceSafetyPolicy());
            await using MainViewModel viewModel = new(controller, true, root);
            await viewModel.InitializeAsync();
            Assert.IsFalse(viewModel.StatusIsError, viewModel.StatusText);
            viewModel.SavedHeatingProfiles.Add(new("named", "named", 220, 60, VaporLevel.Standard, 5, 10, "blue", ["#0000FF"]) { VaporMode = SessionVaporMode.Bold });
            await viewModel.SelectProfileAtAsync(4, CancellationToken.None);
            await ExecuteAsync(viewModel);
            Assert.IsTrue(viewModel.IsHeating);
            await ExecuteAsync(viewModel);
            Assert.IsFalse(viewModel.IsHeating);
            Assert.AreEqual(255f, LoraxValueCodec.ReadSingle(wire.Values[LoraxPaths.ActiveProfileTemperature]));
            Assert.AreEqual(85f, LoraxValueCodec.ReadSingle(wire.Values[LoraxPaths.ActiveProfileTime]));
            Assert.AreEqual(1f, LoraxValueCodec.ReadSingle(wire.Values["/p/app/thc/intn"]));
            Assert.AreEqual(2, wire.Writes.Count(write => write.Path == LoraxPaths.ActiveProfile), "Apply FF and restore0 exactly once; no second legacy reload.");
            Assert.AreEqual(1, wire.Writes.Count(write => write.Path == LoraxPaths.ModeCommand && write.Value[0] == 8));
            Assert.IsTrue(wire.Writes.All(write => write.Flags == 4), "Named apply, start, abort and restore frames must carry captured flags04.");
            Assert.IsFalse(wire.Writes.Any(write => write.Path.StartsWith("/u/", StringComparison.Ordinal)));
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, true); } }
    }

    private static async Task ExecuteAsync(MainViewModel viewModel)
    {
        Assert.IsTrue(viewModel.StartStopCommand.CanExecute(null));
        string before = viewModel.StatusText;
        viewModel.StartStopCommand.Execute(null);
        for (int attempt = 0; attempt < 300 && viewModel.StatusText == before; attempt++) { await Task.Delay(10); }
        Assert.AreNotEqual(before, viewModel.StatusText);
        Assert.IsFalse(viewModel.StatusIsError, viewModel.StatusText);
    }

    private sealed class Wire : ILoraxTransport
    {
        private readonly SidecarLoraxTransport transport;
        internal Dictionary<string, byte[]> Values { get; } = new(StringComparer.Ordinal);
        internal List<(string Path, byte[] Value, byte Flags)> Writes { get; } = [];
        internal Wire()
        {
            Values[LoraxPaths.ModelCode] = [13, 0, 0, 0]; Values[LoraxPaths.FirmwareVersion] = [0x27, 0, 0, 0];
            Values["/p/sys/fw/api"] = [39, 0, 0, 0]; Values[LoraxPaths.DeviceName] = "TEST"u8.ToArray();
            Values[LoraxPaths.ActiveProfile] = [0]; Values[LoraxPaths.OperatingState] = [5]; Values[LoraxPaths.ChamberType] = [2];
            Values[LoraxPaths.ActiveProfileName] = "TEST"u8.ToArray(); Values["/p/app/thc/colr"] = [0xA0];
            Values[LoraxPaths.ActiveProfileTemperature] = LoraxValueCodec.WriteSingle(255);
            Values[LoraxPaths.ActiveProfileTime] = LoraxValueCodec.WriteSingle(85); Values["/p/app/thc/intn"] = LoraxValueCodec.WriteSingle(1);
            Values[LoraxPaths.BatteryStateOfCharge] = LoraxValueCodec.WriteSingle(80); Values[LoraxPaths.BatteryChargeState] = [4];
            Values[LoraxPaths.HeaterTemperature] = LoraxValueCodec.WriteSingle(25); Values[LoraxPaths.HeaterTargetTemperature] = LoraxValueCodec.WriteSingle(255);
            Values[LoraxPaths.StateTotalTime] = LoraxValueCodec.WriteSingle(85); Values[LoraxPaths.StateElapsedTime] = LoraxValueCodec.WriteSingle(0);
            Values[LoraxPaths.TemperatureOverride] = LoraxValueCodec.WriteSingle(0); Values[LoraxPaths.TimeOverride] = LoraxValueCodec.WriteSingle(0);
            for (int slot = 0; slot < 4; slot++)
            {
                Values[LoraxPaths.ProfileName(slot)] = "TEST"u8.ToArray(); Values[LoraxPaths.ProfileColor(slot)] = [0xA0];
                Values[LoraxPaths.ProfileTemperature(slot)] = LoraxValueCodec.WriteSingle(260); Values[LoraxPaths.ProfileTime(slot)] = LoraxValueCodec.WriteSingle(90);
                Values[$"/u/app/hc/{slot}/intn"] = LoraxValueCodec.WriteSingle(1.5);
                Values[LoraxPaths.ProfileBoostTemperature(slot)] = LoraxValueCodec.WriteSingle(5); Values[LoraxPaths.ProfileBoostTime(slot)] = LoraxValueCodec.WriteSingle(10);
            }
            transport = new(NullDiagnosticLog.Instance, false, SendAsync);
        }
        public bool IsConnected => true;
        public string AdvertisedName => "PUFFCO PEAK";
        public Task<IReadOnlyList<DeviceCandidate>> ScanAsync(TimeSpan duration, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DeviceCandidate>>([new("owner", "PUFFCO PEAK", -40)]);
        public Task ConnectAsync(DeviceCandidate candidate, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task TriggerBondingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => transport.DisposeAsync();
        public Task<ReadOnlyMemory<byte>> RunCommandAsync(LoraxOpcode opcode, ReadOnlyMemory<byte> body, int maximumReplyLength, CancellationToken cancellationToken) => transport.RunCommandAsync(opcode, body, maximumReplyLength, cancellationToken);
        private Task<ReadOnlyMemory<byte>> SendAsync(ReadOnlyMemory<byte> frame, ushort sequence, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); ReadOnlySpan<byte> body = frame.Span[3..]; byte[] payload = [];
            switch ((LoraxOpcode)frame.Span[2])
            {
                case LoraxOpcode.GetAccessSeed: payload = new byte[16]; break;
                case LoraxOpcode.UnlockAccess: break;
                case LoraxOpcode.ReadShort:
                    string path = Encoding.UTF8.GetString(body[4..]); int offset = BinaryPrimitives.ReadUInt16LittleEndian(body); int count = BinaryPrimitives.ReadUInt16LittleEndian(body[2..]);
                    payload = Values[path].Skip(offset).Take(count).ToArray(); break;
                case LoraxOpcode.WriteShort:
                    int end = body[3..].IndexOf((byte)0); string target = Encoding.UTF8.GetString(body.Slice(3, end)); byte[] value = body[(4 + end)..].ToArray();
                    Writes.Add((target, value, body[2])); Values[target] = value;
                    if (target == LoraxPaths.ActiveProfile && value[0] <= 3)
                    {
                        foreach (string field in new[] { "temp", "time", "intn", "name", "colr" }) { Values[$"/p/app/thc/{field}"] = Values[$"/u/app/hc/{value[0]}/{field}"].ToArray(); }
                    }
                    if (target == LoraxPaths.ModeCommand) { Values[LoraxPaths.OperatingState] = value[0] == 7 ? [7] : [5]; }
                    break;
                default: throw new InvalidOperationException("Unexpected command in managed fake wire.");
            }
            byte[] reply = new byte[3 + payload.Length]; BinaryPrimitives.WriteUInt16LittleEndian(reply, sequence); payload.CopyTo(reply, 3);
            return Task.FromResult<ReadOnlyMemory<byte>>(reply);
        }
    }
}
