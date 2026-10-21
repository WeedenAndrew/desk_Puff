using DeskPuff.Bluetooth.Windows.Protocol;
using DeskPuff.Core.Devices;

namespace DeskPuff.Bluetooth.Windows.Tests;

[TestClass]
public sealed class RuntimeSessionTransactionTests
{
    [TestMethod]
    public async Task ApplyUsesRuntimeOnlyAndRestoresExactOriginalBytes()
    {
        FakeWire wire = new();
        RuntimeSessionTransaction transaction = wire.Create();
        await transaction.ApplyAsync("same-device", Request(SessionVaporMode.Intense), CancellationToken.None);
        Assert.AreEqual(255, wire.Values["/p/app/hcs"][0]);
        CollectionAssert.AreEqual(RuntimeVaporCodec.Encode(SessionVaporMode.Intense), wire.Values["/p/app/thc/intn"]);
        await transaction.ClearAsync("same-device", CancellationToken.None);
        Assert.IsFalse(transaction.HasRecovery);
        Assert.AreEqual(0, wire.Values["/p/app/hcs"][0]);
        CollectionAssert.AreEqual(RuntimeVaporCodec.Encode(SessionVaporMode.Extreme), wire.Values["/p/app/thc/intn"]);
        Assert.IsFalse(wire.Writes.Any(path => path.StartsWith("/u/", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task DifferentDeviceCannotConsumeRecovery()
    {
        FakeWire wire = new(); RuntimeSessionTransaction transaction = wire.Create();
        await transaction.ApplyAsync("owner", Request(SessionVaporMode.Bold), CancellationToken.None);
        int count = wire.Writes.Count;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => transaction.ClearAsync("other-device", CancellationToken.None));
        Assert.AreEqual(count, wire.Writes.Count);
        Assert.IsTrue(transaction.HasRecovery);
    }

    [TestMethod]
    [DataRow("time", -1f)]
    [DataRow("time", 1000f)]
    [DataRow("temp", 1f)]
    [DataRow("temp", 500f)]
    [DataRow("intn", 0.75f)]
    public async Task FiniteInvalidOriginalsRefuseBeforeMutation(string field, float invalid)
    {
        FakeWire wire = new(); wire.Values[$"/p/app/thc/{field}"] = LoraxValueCodec.WriteSingle(invalid);
        RuntimeSessionTransaction transaction = wire.Create();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => transaction.ApplyAsync("owner", Request(SessionVaporMode.Bold), CancellationToken.None));
        Assert.HasCount(0, wire.Writes);
        Assert.IsFalse(transaction.HasRecovery);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(255)]
    public async Task ExternalContextOrRuntimeChangePreventsAnyCleanupWrite(int selection)
    {
        FakeWire wire = new(); RuntimeSessionTransaction transaction = wire.Create();
        await transaction.ApplyAsync("owner", Request(SessionVaporMode.Bold), CancellationToken.None);
        wire.Values["/p/app/hcs"] = [(byte)selection];
        if (selection == 255) { wire.Values["/p/app/thc/intn"] = RuntimeVaporCodec.Encode(SessionVaporMode.Intense); }
        int count = wire.Writes.Count;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => transaction.ClearAsync("owner", CancellationToken.None));
        Assert.AreEqual(count, wire.Writes.Count);
        Assert.IsTrue(transaction.HasRecovery);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => transaction.VerifyAppliedAsync("owner", CancellationToken.None));
    }

    [TestMethod]
    public async Task DisconnectInvalidatesProofButKeepsOriginalsForSameOwnerRecovery()
    {
        FakeWire wire = new(); RuntimeSessionTransaction transaction = wire.Create();
        await transaction.ApplyAsync("owner", Request(SessionVaporMode.Bold), CancellationToken.None);
        transaction.InvalidateApplication();
        Assert.IsTrue(transaction.HasRecovery);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => transaction.VerifyAppliedAsync("owner", CancellationToken.None));
        await transaction.ClearAsync("owner", CancellationToken.None);
        Assert.IsFalse(transaction.HasRecovery);
        Assert.AreEqual(1.5f, LoraxValueCodec.ReadSingle(wire.Values["/p/app/thc/intn"]));
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task OriginalTemperatureUsesExactEncodedBounds(bool upper, bool outside)
    {
        DeviceLimits limits = new(204.44444444444446, 315.55555555555554,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(120), 8.333333333, TimeSpan.FromSeconds(30));
        float boundary = (float)(upper ? limits.MaximumTemperatureCelsius : limits.MinimumTemperatureCelsius);
        float actual = outside ? upper ? MathF.BitIncrement(boundary) : MathF.BitDecrement(boundary) : boundary;
        FakeWire wire = new(); wire.Values["/p/app/thc/temp"] = LoraxValueCodec.WriteSingle(actual);
        RuntimeSessionTransaction transaction = wire.Create(limits);
        if (outside)
        {
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => transaction.ApplyAsync("owner", Request(SessionVaporMode.Bold), CancellationToken.None));
            Assert.HasCount(0, wire.Writes);
        }
        else
        {
            await transaction.ApplyAsync("owner", Request(SessionVaporMode.Bold), CancellationToken.None);
            await transaction.ClearAsync("owner", CancellationToken.None);
            CollectionAssert.AreEqual(LoraxValueCodec.WriteSingle(actual), wire.Values["/p/app/thc/temp"]);
        }
    }

    [TestMethod]
    public async Task CorruptIntensityReadbackCannotEstablishApplicationAndOriginalsRemainRestorable()
    {
        FakeWire wire = new() { MismatchIntensityReplyOnce = true };
        RuntimeSessionTransaction transaction = wire.Create();
        await Assert.ThrowsExactlyAsync<IOException>(() => transaction.ApplyAsync("owner", Request(SessionVaporMode.Bold), CancellationToken.None));
        Assert.IsTrue(transaction.HasRecovery);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => transaction.VerifyAppliedAsync("owner", CancellationToken.None));
        await transaction.ClearAsync("owner", CancellationToken.None);
        Assert.IsFalse(transaction.HasRecovery);
        CollectionAssert.AreEqual(RuntimeVaporCodec.Encode(SessionVaporMode.Extreme), wire.Values["/p/app/thc/intn"]);
    }

    private static SessionOverride Request(SessionVaporMode mode) => new("test", 220, TimeSpan.FromSeconds(60), VaporLevel.Standard, []) { VaporMode = mode };

    private sealed class FakeWire
    {
        internal Dictionary<string, byte[]> Values { get; } = new(StringComparer.Ordinal);
        internal List<string> Writes { get; } = [];
        internal bool MismatchIntensityReplyOnce { get; set; }
        internal FakeWire()
        {
            Values["/p/app/hcs"] = [0];
            Values["/p/app/thc/time"] = LoraxValueCodec.WriteSingle(90);
            Values["/p/app/thc/temp"] = LoraxValueCodec.WriteSingle(260);
            Values["/p/app/thc/intn"] = RuntimeVaporCodec.Encode(SessionVaporMode.Extreme);
            Values["/p/app/thc/name"] = "original"u8.ToArray();
            Values["/p/app/thc/colr"] = [0xA0];
            Values["/p/app/tmpo"] = LoraxValueCodec.WriteSingle(0);
            Values["/p/app/timo"] = LoraxValueCodec.WriteSingle(0);
            for (int slot = 0; slot < 4; slot++)
            {
                foreach (string field in new[] { "time", "temp", "intn", "name", "colr" })
                    Values[$"/u/app/hc/{slot}/{field}"] = Values[$"/p/app/thc/{field}"].ToArray();
            }
        }
        internal RuntimeSessionTransaction Create(DeviceLimits? limits = null) => new(
            (path, _, _) =>
            {
                if (MismatchIntensityReplyOnce && path == "/p/app/thc/intn" && Writes.Contains(path))
                {
                    MismatchIntensityReplyOnce = false;
                    return Task.FromResult<ReadOnlyMemory<byte>>(LoraxValueCodec.WriteSingle(0.75));
                }
                return Task.FromResult<ReadOnlyMemory<byte>>(Values[path].ToArray());
            },
            (path, data, _) => { Writes.Add(path); Values[path] = data.ToArray(); return Task.FromResult(true); },
            _ => Task.CompletedTask, limits);
    }
}
