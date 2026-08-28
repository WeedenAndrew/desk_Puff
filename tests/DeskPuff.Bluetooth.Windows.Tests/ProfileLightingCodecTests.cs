using DeskPuff.Bluetooth.Windows.Protocol;

namespace DeskPuff.Bluetooth.Windows.Tests;

[TestClass]
public sealed class ProfileLightingCodecTests
{
    private static readonly string[] BasePalette = ["#0000FF", "#6EE916"];
    private static readonly string[] OversizedPalette =
        ["#000000", "#111111", "#222222", "#333333", "#444444"];
    private const string SlotZeroColorBytes =
        "7B07FF6F4FEC5F8AD74CC1C72CEDB507FFAB07F9B607E9CE07D6E607C6F807BFFF72B2F2BC92D3E667ADFA358CFF077DFF0D8BFF14A9FF16CCFF0FEAFF07F7F307F6D707F5B207F68E07FB";
    private const string SlotOneColorBytes =
        "4D023E4D0641533574476DC41393FC0795FF1394FF537DFC7B51FB9112FF9207FF9209FD8B1DDC8128AD792B8F792B8D782A8C6C2076590F554E033F";
    private static readonly string[] SlotZeroFirstTen =
        ["#7B07FF", "#6F4FEC", "#5F8AD7", "#4CC1C7", "#2CEDB5", "#07FFAB", "#07F9B6", "#07E9CE", "#07D6E6", "#07C6F8"];
    private static readonly string[] SlotOneFirstTen =
        ["#4D023E", "#4D0641", "#533574", "#476DC4", "#1393FC", "#0795FF", "#1394FF", "#537DFC", "#7B51FB", "#9112FF"];

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
    public void LightingDecoder_DecodesObservedTwentyFiveColorPalette()
    {
        IReadOnlyList<string> colors = ProfileLightingCodec.DecodeColors(
            BuildCompletePikaledFixture(SlotZeroColorBytes));

        Assert.HasCount(25, colors);
        CollectionAssert.AreEqual(SlotZeroFirstTen, colors.Take(10).ToArray());
    }

    [TestMethod]
    public void LightingDecoder_DecodesObservedTwentyColorPalette()
    {
        IReadOnlyList<string> colors = ProfileLightingCodec.DecodeColors(
            BuildCompletePikaledFixture(SlotOneColorBytes));

        Assert.HasCount(20, colors);
        CollectionAssert.AreEqual(SlotOneFirstTen, colors.Take(10).ToArray());
    }

    [TestMethod]
    public void LightingDecoder_PathAnimationWithoutColorReturnsEmptyPalette()
    {
        byte[] migrationLighting = Convert.FromHexString(
            "A1646C616D70A2646E616D65676D696772746E3165706172616DA165706174687380");

        IReadOnlyList<string> colors = ProfileLightingCodec.DecodeColors(migrationLighting);

        Assert.HasCount(0, colors);
    }

    [TestMethod]
    public void LightingEncoder_RejectsInvalidRgbBeforeTransport()
    {
        Assert.ThrowsExactly<ArgumentException>(() => ProfileLightingCodec.EncodeSolid(["red"]));
        Assert.ThrowsExactly<ArgumentException>(() => ProfileLightingCodec.EncodeSolid(["#12GG56"]));
    }

    private static byte[] BuildCompletePikaledFixture(string colorBytesHex)
    {
        byte[] header = Convert.FromHexString(
            "A1646C616D70A2646E616D656870696B616C65643265706172616DA165636F6C6F7258");
        byte[] colorBytes = Convert.FromHexString(colorBytesHex);
        return [.. header, checked((byte)colorBytes.Length), .. colorBytes];
    }
}
