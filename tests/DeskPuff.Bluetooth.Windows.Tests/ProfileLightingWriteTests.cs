using System.Buffers.Binary;
using System.Text;
using DeskPuff.Bluetooth.Windows.Protocol;
using DeskPuff.Core.Devices;

namespace DeskPuff.Bluetooth.Windows.Tests;

public sealed partial class ThcProbeTests
{
    private static readonly string[] OriginalLightingColors = ["#102030", "#405060"];
    private static readonly string[] ChangedLightingColors = ["#AABBCC", "#DDEEFF"];

    [TestMethod]
    public async Task LightingWrite_PreservesRichDocumentAndUsesBoundedOffsetPatches()
    {
        byte[] original = RichLighting("solid");
        LightingWire wire = new(original);
        await using LoraxDeviceClient client = new(wire.Transport, wire.Log);
        await client.ConnectAsync(new("test", "PUFFCO PEAK", -40), CancellationToken.None);

        await client.UpdateProfileAsync(LightingProfile(ChangedLightingColors), CancellationToken.None);

        byte[] expected = RichLighting("solid", ChangedLightingColors);
        CollectionAssert.AreEqual(expected, wire.Document);
        Assert.AreEqual(original.Length, wire.Document.Length);
        Assert.IsTrue(wire.ColorWrites.Count > 1);
        Assert.IsTrue(wire.ColorWrites.All(write => write.Offset > 125 && write.Length <= 12));
        AssertNoHeat(wire.Log);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(4)]
    public async Task LightingWrite_SupportsExistingSolidWithoutMetadataAtPaletteBounds(int count)
    {
        string[] original = Enumerable.Repeat("#102030", count).ToArray();
        string[] changed = Enumerable.Repeat("#ABCDEF", count).ToArray();
        LightingWire wire = new(ProfileLightingCodec.EncodeSolid(original));
        await using LoraxDeviceClient client = new(wire.Transport, wire.Log);
        await client.ConnectAsync(new("test", "PUFFCO PEAK", -40), CancellationToken.None);
        await client.UpdateProfileAsync(LightingProfile(changed), CancellationToken.None);
        CollectionAssert.AreEqual(ProfileLightingCodec.EncodeSolid(changed), wire.Document);
    }

    [TestMethod]
    public async Task LightingWrite_RejectsColorCorrectReadbackThatDroppedAllMetadata()
    {
        LightingWire wire = new(RichLighting("solid")) { DropMetadataOnReadback = true };
        await using LoraxDeviceClient client = new(wire.Transport, wire.Log);
        await client.ConnectAsync(new("test", "PUFFCO PEAK", -40), CancellationToken.None);
        await Assert.ThrowsExactlyAsync<IOException>(() =>
            client.UpdateProfileAsync(LightingProfile(ChangedLightingColors), CancellationToken.None));
    }

    [TestMethod]
    public async Task LightingWrite_UnchangedPaletteComparisonIgnoresHexCase()
    {
        byte[] original = RichLighting("pikaled2");
        LightingWire wire = new(original);
        await using LoraxDeviceClient client = new(wire.Transport, wire.Log);
        await client.ConnectAsync(new("test", "PUFFCO PEAK", -40), CancellationToken.None);
        await client.UpdateProfileAsync(LightingProfile(OriginalLightingColors.Select(color => color.ToLowerInvariant()).ToArray()), CancellationToken.None);
        CollectionAssert.AreEqual(original, wire.Document);
        Assert.HasCount(0, wire.ColorWrites);
    }

    [TestMethod]
    [DataRow("F6")]
    [DataRow("A16A75736572436F6C6F72738163626164")]
    public async Task LightingWrite_InvalidMetadataRefusesBeforeAnyWrite(string metadata)
    {
        byte[] document = LightingMap(("lamp", LightingMap(("name", LightingText("solid")),
            ("param", LightingMap(("color", LightingBytes(Convert.FromHexString("102030405060"))))))),
            ("meta", Convert.FromHexString(metadata)));
        LightingWire wire = new(document);
        await using LoraxDeviceClient client = new(wire.Transport, wire.Log);
        await client.ConnectAsync(new("test", "PUFFCO PEAK", -40), CancellationToken.None);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            client.UpdateProfileAsync(LightingProfile(ChangedLightingColors), CancellationToken.None));
        Assert.HasCount(0, wire.Transport.Writes);
    }

    [TestMethod]
    [DataRow("pikaled2")]
    [DataRow("migrtn1")]
    public async Task LightingWrite_UnchangedAnimatedPalettePreservesEveryByteWithoutLightingWrite(string program)
    {
        byte[] original = RichLighting(program);
        LightingWire wire = new(original);
        await using LoraxDeviceClient client = new(wire.Transport, wire.Log);
        await client.ConnectAsync(new("test", "PUFFCO PEAK", -40), CancellationToken.None);

        await client.UpdateProfileAsync(LightingProfile(OriginalLightingColors), CancellationToken.None);

        CollectionAssert.AreEqual(original, wire.Document);
        Assert.HasCount(0, wire.ColorWrites);
    }

    [TestMethod]
    [DataRow("pikaled2")]
    [DataRow("migrtn1")]
    public async Task LightingWrite_UnsupportedAnimationEditRefusesBeforeAnyProfileWrite(string program)
    {
        LightingWire wire = new(RichLighting(program));
        await using LoraxDeviceClient client = new(wire.Transport, wire.Log);
        await client.ConnectAsync(new("test", "PUFFCO PEAK", -40), CancellationToken.None);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            client.UpdateProfileAsync(LightingProfile(ChangedLightingColors), CancellationToken.None));

        Assert.HasCount(0, wire.Transport.Writes);
    }

    [TestMethod]
    public async Task LightingWrite_ColorCountChangeRefusesBeforeAnyProfileWrite()
    {
        LightingWire wire = new(RichLighting("solid"));
        await using LoraxDeviceClient client = new(wire.Transport, wire.Log);
        await client.ConnectAsync(new("test", "PUFFCO PEAK", -40), CancellationToken.None);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            client.UpdateProfileAsync(LightingProfile(["#AABBCC"]), CancellationToken.None));
        Assert.HasCount(0, wire.Transport.Writes);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LightingWrite_FullReadbackRejectsMetadataCorruptionOrTrailingBytes(bool trailing)
    {
        LightingWire wire = new(RichLighting("solid")) { CorruptMetadata = !trailing, AddTrailingByte = trailing };
        await using LoraxDeviceClient client = new(wire.Transport, wire.Log);
        await client.ConnectAsync(new("test", "PUFFCO PEAK", -40), CancellationToken.None);
        await Assert.ThrowsExactlyAsync<IOException>(() =>
            client.UpdateProfileAsync(LightingProfile(ChangedLightingColors), CancellationToken.None));
    }

    [TestMethod]
    public async Task LightingWrite_RejectedChunkStopsWithoutRetry()
    {
        LightingWire wire = new(RichLighting("solid"), rejectWrite: 6);
        await using LoraxDeviceClient client = new(wire.Transport, wire.Log);
        await client.ConnectAsync(new("test", "PUFFCO PEAK", -40), CancellationToken.None);
        await Assert.ThrowsExactlyAsync<IOException>(() =>
            client.UpdateProfileAsync(LightingProfile(ChangedLightingColors), CancellationToken.None));
        Assert.HasCount(6, wire.Transport.Writes);
    }

    [TestMethod]
    [DataRow("A0")]
    [DataRow("BF")]
    [DataRow("A1646C616D70")]
    public async Task LightingWrite_MalformedOrMissingColorRefusesBeforeAnyWrite(string hex)
    {
        LightingWire wire = new(Convert.FromHexString(hex));
        await using LoraxDeviceClient client = new(wire.Transport, wire.Log);
        await client.ConnectAsync(new("test", "PUFFCO PEAK", -40), CancellationToken.None);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            client.UpdateProfileAsync(LightingProfile(ChangedLightingColors), CancellationToken.None));
        Assert.HasCount(0, wire.Transport.Writes);
    }

    private static HeatProfile LightingProfile(string[] colors) =>
        new(0, "TEST", 260, TimeSpan.FromSeconds(90), VaporLevel.Standard, 5, TimeSpan.FromSeconds(10), colors[0])
        { ColorPalette = colors };

    // Synthetic identity sentinels only. Noncanonical integer widths, a tag, float,
    // binary extension and nested animation data must survive byte-for-byte.
    private static byte[] RichLighting(string program, string[]? colors = null)
    {
        colors ??= OriginalLightingColors;
        byte[] rgb = colors.SelectMany(color => Convert.FromHexString(color[1..])).ToArray();
        return LightingMap(
            ("extension", LightingBytes(new byte[520])),
            ("lamp", LightingMap(("name", LightingText(program)),
                ("param", LightingMap((program == "migrtn1" ? "colors" : "color", LightingBytes(rgb)),
                    ("paths", Convert.FromHexString("82A1646E6F64651801C1F93C00")))))),
            ("meta", LightingMap(("heatProfileUlid", LightingText("SYNTHETIC-PROFILE")),
                ("moodUlid", LightingText("SYNTHETIC-MOOD")),
                ("userColors", [(byte)(0x80 + colors.Length), .. colors.SelectMany(LightingText)]),
                ("version", Convert.FromHexString("190001")))));
    }

    private static byte[] LightingText(string text) => LightingValue(3, Encoding.UTF8.GetBytes(text));
    private static byte[] LightingBytes(byte[] bytes) => LightingValue(2, bytes);
    private static byte[] LightingValue(int major, byte[] bytes) => bytes.Length < 24
        ? [(byte)((major << 5) | bytes.Length), .. bytes]
        : bytes.Length <= 255
            ? [(byte)((major << 5) | 24), (byte)bytes.Length, .. bytes]
            : [(byte)((major << 5) | 25), (byte)(bytes.Length >> 8), (byte)bytes.Length, .. bytes];
    private static byte[] LightingMap(params (string Key, byte[] Value)[] entries) =>
        [(byte)(0xA0 + entries.Length), .. entries.SelectMany(entry => LightingText(entry.Key).Concat(entry.Value))];

    private sealed class LightingWire
    {
        internal RecordingLog Log { get; } = new();
        internal ProbeTransport Transport { get; }
        internal List<(int Offset, int Length)> ColorWrites { get; } = [];
        internal byte[] Document => Transport.Values[LoraxPaths.ProfileColor(0)];
        internal bool CorruptMetadata { get; init; }
        internal bool AddTrailingByte { get; init; }
        internal bool DropMetadataOnReadback { get; init; }

        internal LightingWire(byte[] document, int rejectWrite = 0)
        {
            Transport = new(Log) { RejectWrite = rejectWrite };
            for (int slot = 0; slot < 4; slot++)
            {
                Transport.Values[LoraxPaths.ProfileColor(slot)] = document.ToArray();
                Transport.Values[LoraxPaths.ProfileName(slot)] = Encoding.UTF8.GetBytes("TEST");
                Transport.Values[LoraxPaths.ProfileBoostTemperature(slot)] = Float(5);
                Transport.Values[LoraxPaths.ProfileBoostTime(slot)] = Float(10);
            }

            byte[] prior = [];
            int offset = 0;
            bool corrupted = false;
            Transport.BeforeRead = path =>
            {
                if (DropMetadataOnReadback && ColorWrites.Count > 0 && path == LoraxPaths.ProfileColor(0))
                {
                    Transport.Values[path] = ProfileLightingCodec.EncodeSolid(ChangedLightingColors);
                }
            };
            Transport.BeforeCommandAsync = (opcode, body) =>
            {
                if (opcode == LoraxOpcode.WriteShort)
                {
                    offset = BinaryPrimitives.ReadUInt16LittleEndian(body.Span);
                    prior = Document.ToArray();
                }
                return Task.CompletedTask;
            };
            Transport.AfterWrite = (path, written) =>
            {
                if (path != LoraxPaths.ProfileColor(0)) { return; }
                ColorWrites.Add((offset, written.Length));
                // Offset writes do not truncate an existing file. This catches
                // the old short replacement leaving stale bytes at the tail.
                if (offset + written.Length > prior.Length) { Array.Resize(ref prior, offset + written.Length); }
                written.CopyTo(prior, offset);
                if (CorruptMetadata && !corrupted) { prior[^1] ^= 1; corrupted = true; }
                Transport.Values[path] = AddTrailingByte ? [.. prior, 0] : prior;
            };
        }
    }
}
