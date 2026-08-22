using DeskPuff.Bluetooth.Windows.Protocol;

namespace DeskPuff.Bluetooth.Windows.Tests;

[TestClass]
public sealed class ProfileLightingCodecTests
{
    private static readonly string[] BasePalette = ["#0000FF", "#6EE916"];
    private static readonly string[] OversizedPalette =
        ["#000000", "#111111", "#222222", "#333333", "#444444"];

    [TestMethod]
    public void SolidPalette_UsesBoundedCanonicalPuffcoCbor()
    {
        byte[] encoded = ProfileLightingCodec.EncodeSolid(BasePalette);

        Assert.AreEqual(
            "A1646C616D70A2646E616D6565736F6C696465706172616DA165636F6C6F72460000FF6EE916",
            Convert.ToHexString(encoded));
        CollectionAssert.AreEqual(
            BasePalette,
            ProfileLightingCodec.DecodeColors(encoded).ToArray());
    }

    [TestMethod]
    public void LightingDecoder_RejectsMalformedOrUnboundedInput()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => ProfileLightingCodec.DecodeColors([]));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            ProfileLightingCodec.DecodeColors(Convert.FromHexString("A1646C616D70")));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            ProfileLightingCodec.EncodeSolid(OversizedPalette));
    }

    [TestMethod]
    public void LightingEncoder_RejectsInvalidRgbBeforeTransport()
    {
        Assert.ThrowsExactly<ArgumentException>(() => ProfileLightingCodec.EncodeSolid(["red"]));
        Assert.ThrowsExactly<ArgumentException>(() => ProfileLightingCodec.EncodeSolid(["#12GG56"]));
    }
}
