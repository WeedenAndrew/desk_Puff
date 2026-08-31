using System.Buffers.Binary;
using DeskPuff.Bluetooth.Windows.Protocol;

namespace DeskPuff.Bluetooth.Windows.Tests;

[TestClass]
public sealed class LoraxProtocolTests
{
    [TestMethod]
    public void UnlockKey_MatchesKnownVector()
    {
        byte[] seed = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();

        byte[] key = LoraxProtocol.DeriveUnlockKey(seed);

        Assert.AreEqual("CC4AF4246A24EB8BB512BC7DC5157B9C", Convert.ToHexString(key));
    }

    [TestMethod]
    public void UnlockKey_RejectsIncorrectSeedLength()
    {
        Assert.ThrowsExactly<ArgumentException>(() => LoraxProtocol.DeriveUnlockKey(new byte[15]));
        Assert.ThrowsExactly<ArgumentException>(() => LoraxProtocol.DeriveUnlockKey(new byte[17]));
    }

    [TestMethod]
    public void ProductionOpcodeSurface_ContainsNoFileOrFirmwareOperations()
    {
        CollectionAssert.AreEquivalent(
            new byte[] { 0x00, 0x01, 0x02, 0x10, 0x11 },
            Enum.GetValues<LoraxOpcode>().Select(value => (byte)value).ToArray());
    }

    [TestMethod]
    public void Frame_UsesLittleEndianSequenceAndOpcode()
    {
        byte[] frame = LoraxProtocol.BuildFrame(0x1234, LoraxOpcode.ReadShort, [0xAA, 0xBB]);

        CollectionAssert.AreEqual(new byte[] { 0x34, 0x12, 0x10, 0xAA, 0xBB }, frame);
    }

    [TestMethod]
    public void Reply_RejectsTruncatedHeader()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => LoraxProtocol.ParseReply(new byte[2]));
    }

    [TestMethod]
    public void Reply_ParsesSequenceStatusAndPayload()
    {
        LoraxReply reply = LoraxProtocol.ParseReply(new byte[] { 0x34, 0x12, 0x07, 0xAA });

        Assert.AreEqual((ushort)0x1234, reply.Sequence);
        Assert.AreEqual((byte)0x07, reply.Status);
        CollectionAssert.AreEqual(new byte[] { 0xAA }, reply.Payload.ToArray());
    }

    [TestMethod]
    public void ReadBody_EncodesBoundsAndPath()
    {
        byte[] body = LoraxProtocol.BuildReadBody(LoraxPaths.HeaterTemperature, 2, 4);

        Assert.AreEqual((ushort)2, BinaryPrimitives.ReadUInt16LittleEndian(body));
        Assert.AreEqual((ushort)4, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(2)));
        Assert.AreEqual(LoraxPaths.HeaterTemperature, System.Text.Encoding.UTF8.GetString(body.AsSpan(4)));
    }

    [TestMethod]
    public void WriteBody_AllowsOnlyEnumeratedControlPaths()
    {
        byte[] body = LoraxProtocol.BuildWriteBody(LoraxPaths.ModeCommand, 0, 0, [7]);

        Assert.IsGreaterThan(4, body.Length);
        Assert.ThrowsExactly<DeviceWriteBlockedException>(() =>
            LoraxProtocol.BuildWriteBody("/p/sys/fw/update", 0, 0, [1]));
        Assert.ThrowsExactly<DeviceWriteBlockedException>(() =>
            LoraxProtocol.BuildWriteBody("/p/sys/facr", 0, 0, [1]));
        Assert.ThrowsExactly<DeviceWriteBlockedException>(() =>
            LoraxProtocol.BuildWriteBody("/p/fs/file", 0, 0, [1]));
    }

    [TestMethod]
    public void ProfileWriteAllowlist_IsBoundedToFourKnownFields()
    {
        Assert.IsTrue(LoraxPaths.IsWriteAllowed("/u/app/hc/0/name"));
        Assert.IsTrue(LoraxPaths.IsWriteAllowed("/u/app/hc/3/colr"));
        Assert.IsFalse(LoraxPaths.IsWriteAllowed("/u/app/hc/4/name"));
        Assert.IsFalse(LoraxPaths.IsWriteAllowed("/u/app/hc/-1/temp"));
        Assert.IsFalse(LoraxPaths.IsWriteAllowed("/u/app/hc/0/unknown"));
        Assert.IsFalse(LoraxPaths.IsWriteAllowed("/u/app/hc/0/temp/extra"));
    }

    [TestMethod]
    public void WriteBody_RejectsEmptyAndOversizedPayloads()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            LoraxProtocol.BuildWriteBody(LoraxPaths.ModeCommand, 0, 0, []));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            LoraxProtocol.BuildWriteBody(LoraxPaths.ModeCommand, 0, 0, new byte[129]));
    }

    [TestMethod]
    public void PathValidation_RejectsMalformedPaths()
    {
        Assert.ThrowsExactly<ArgumentException>(() => LoraxProtocol.BuildReadBody("relative", 0, 4));
        Assert.ThrowsExactly<ArgumentException>(() => LoraxProtocol.BuildReadBody("/bad\0path", 0, 4));
        Assert.ThrowsExactly<ArgumentException>(() => LoraxProtocol.BuildReadBody("/" + new string('a', 63), 0, 4));
    }

    [TestMethod]
    public void ValueCodec_RejectsNonFiniteAndOutOfRangeValues()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => LoraxValueCodec.WriteSingle(double.NaN));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => LoraxValueCodec.WriteSingle(1001));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => LoraxValueCodec.WriteString("bad\0name"));
    }
}
