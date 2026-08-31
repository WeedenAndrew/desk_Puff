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
    private static readonly string[] SlotZeroAuthoredColors =
        ["#7B07FF", "#07FFAB", "#07BFFF", "#FF077D", "#FF07F7"];
    private static readonly string[] SlotZeroAuthoredColorsLowercase =
        ["#7b07ff", "#07ffab", "#07bfff", "#ff077d", "#ff07f7"];

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
        InvalidDataException indefinite = Assert.ThrowsExactly<InvalidDataException>(() =>
            ProfileLightingCodec.DecodeColors(Convert.FromHexString("BF")));
        StringAssert.Contains(indefinite.Message, "Indefinite-length");
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            ProfileLightingCodec.EncodeSolid(OversizedPalette));
    }

    [TestMethod]
    public void LightingDecoder_DecodesObservedTwentyFiveColorPalette()
    {
        IReadOnlyList<string> colors = ProfileLightingCodec.DecodeColors(
            BuildCompletePikaledFixture(SlotZeroColorBytes),
            out ProfileLightingCodec.PaletteSource source);

        Assert.AreEqual(ProfileLightingCodec.PaletteSource.LampParamColor, source);
        Assert.HasCount(25, colors);
        CollectionAssert.AreEqual(SlotZeroFirstTen, colors.Take(10).ToArray());
    }

    [TestMethod]
    public void LightingDecoder_PrefersObservedUserColorsAndNormalizesUppercase()
    {
        byte[] fixture = BuildCompletePikaledFixtureWithUserColors(
            SlotZeroAuthoredColorsLowercase);

        ProfileLightingCodec.DecodedLighting decoded =
            ProfileLightingCodec.DecodeLighting(fixture);

        Assert.AreEqual(ProfileLightingCodec.PaletteSource.MetaUserColors, decoded.Source);
        Assert.AreEqual("DISCO", decoded.MoodName);
        Assert.AreEqual("pikaled2", decoded.LampName);
        Assert.HasCount(5, decoded.Colors);
        CollectionAssert.AreEqual(SlotZeroAuthoredColors, decoded.Colors.ToArray());
    }

    [TestMethod]
    public void LightingDecoder_MalformedUserColorFallsBackToInterpolatedRamp()
    {
        byte[] fixture = BuildCompletePikaledFixtureWithUserColors(
            ["#7b07ff", "nothex!"]);

        IReadOnlyList<string> colors = ProfileLightingCodec.DecodeColors(
            fixture,
            out ProfileLightingCodec.PaletteSource source);

        Assert.AreEqual(ProfileLightingCodec.PaletteSource.LampParamColor, source);
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
    public void LightingDecoder_MissingOptionalMetadataStillDecodes()
    {
        byte[] fixture = BuildCompleteFixtureWithoutMetadata(SlotZeroColorBytes);

        ProfileLightingCodec.DecodedLighting decoded =
            ProfileLightingCodec.DecodeLighting(fixture);

        Assert.IsNull(decoded.MoodName);
        Assert.IsNull(decoded.LampName);
        Assert.AreEqual(ProfileLightingCodec.PaletteSource.LampParamColor, decoded.Source);
        Assert.HasCount(25, decoded.Colors);
    }

    [TestMethod]
    public void LightingDecoder_DecodesCompleteCborBeyondFormerByteCeiling()
    {
        byte[] fixture = BuildLargeCompletePikaledFixture();

        Assert.IsTrue(fixture.Length > 512, "The regression fixture must exceed the former 512-byte ceiling.");
        Assert.IsTrue(fixture.Length < 4096, "The regression fixture must remain inside the new bounded ceiling.");

        IReadOnlyList<string> colors = ProfileLightingCodec.DecodeColors(fixture);

        Assert.HasCount(25, colors);
        CollectionAssert.AreEqual(SlotZeroFirstTen, colors.Take(10).ToArray());
    }

    [TestMethod]
    public void LightingDecoder_ReadsPastFloatingPointValuesBeforeObservedPalette()
    {
        IReadOnlyList<string> colors = ProfileLightingCodec.DecodeColors(
            BuildPikaledFixtureWithFloatingPointValues());

        Assert.HasCount(25, colors);
        CollectionAssert.AreEqual(SlotZeroFirstTen, colors.Take(10).ToArray());
    }

    [TestMethod]
    public void LightingDecoder_PathAnimationWithoutColorReturnsEmptyPalette()
    {
        byte[] migrationLighting = Convert.FromHexString(
            "A1646C616D70A2646E616D65676D696772746E3165706172616DA165706174687380");

        ProfileLightingCodec.DecodedLighting decoded =
            ProfileLightingCodec.DecodeLighting(migrationLighting);

        Assert.AreEqual(ProfileLightingCodec.PaletteSource.None, decoded.Source);
        Assert.IsNull(decoded.MoodName);
        Assert.AreEqual("migrtn1", decoded.LampName);
        Assert.HasCount(0, decoded.Colors);
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

    private static byte[] BuildLargeCompletePikaledFixture()
    {
        byte[] header = Convert.FromHexString(
            "A1646C616D70A2646E616D656870696B616C65643265706172616DA265636F6C6F72584B");
        byte[] colorBytes = Convert.FromHexString(SlotZeroColorBytes);
        byte[] paddingHeader = Convert.FromHexString("6770616464696E67590208");
        byte[] padding = new byte[520];
        return [.. header, .. colorBytes, .. paddingHeader, .. padding];
    }

    private static byte[] BuildCompleteFixtureWithoutMetadata(string colorBytesHex)
    {
        byte[] header = Convert.FromHexString(
            "A1646C616D70A165706172616DA165636F6C6F7258");
        byte[] colorBytes = Convert.FromHexString(colorBytesHex);
        return [.. header, checked((byte)colorBytes.Length), .. colorBytes];
    }

    private static byte[] BuildCompletePikaledFixtureWithUserColors(
        string[] userColors)
    {
        byte[] header = Convert.FromHexString(
            "A2646C616D70A2646E616D656870696B616C65643265706172616DA165636F6C6F7258");
        byte[] colorBytes = Convert.FromHexString(SlotZeroColorBytes);
        byte[] metadataHeader = Convert.FromHexString(
            "646D657461A2686D6F6F644E616D6565444953434F6A75736572436F6C6F7273");
        if (userColors.Length > 23)
        {
            throw new ArgumentOutOfRangeException(nameof(userColors));
        }

        List<byte> fixture = new(header.Length + colorBytes.Length + metadataHeader.Length + 64);
        fixture.AddRange(header);
        fixture.Add(checked((byte)colorBytes.Length));
        fixture.AddRange(colorBytes);
        fixture.AddRange(metadataHeader);
        fixture.Add((byte)(0x80 | userColors.Length));
        foreach (string color in userColors)
        {
            byte[] encoded = System.Text.Encoding.UTF8.GetBytes(color);
            if (encoded.Length > 23)
            {
                throw new ArgumentOutOfRangeException(nameof(userColors));
            }

            fixture.Add((byte)(0x60 | encoded.Length));
            fixture.AddRange(encoded);
        }

        return fixture.ToArray();
    }

    private static byte[] BuildPikaledFixtureWithFloatingPointValues()
    {
        byte[] header = Convert.FromHexString(
            "A1646C616D70A2646E616D656870696B616C65643265706172616DA4" +
            "6468616C66F938006673696E676C65FA3F000000" +
            "6974656D706F46726163FB3FE0000000000000" +
            "65636F6C6F72584B");
        byte[] colorBytes = Convert.FromHexString(SlotZeroColorBytes);
        return [.. header, .. colorBytes];
    }
}
