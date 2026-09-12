using DeskPuff.Bluetooth.Windows.Protocol;

namespace DeskPuff.Bluetooth.Windows.Tests;

[TestClass]
public sealed class CborReaderTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Reader_DecodesEveryRfc8949AdditionalInformationWidth()
    {
        (string Hex, ulong Expected)[] vectors =
        [
            ("0A", 10UL),
            ("1818", 24UL),
            ("1903E8", 1000UL),
            ("1A000F4240", 1_000_000UL),
            ("1B000000E8D4A51000", 1_000_000_000_000UL),
        ];

        foreach ((string hex, ulong expected) in vectors)
        {
            Assert.AreEqual(expected, DecodeComplete(hex), $"RFC 8949 vector {hex} decoded incorrectly.");
        }
    }

    [TestMethod]
    public void Reader_MaterializesRfc8949HalfSingleAndDoubleFloats()
    {
        object? half = DecodeComplete("F93C00");
        object? single = DecodeComplete("FA47C35000");
        object? doubleValue = DecodeComplete("FB3FF199999999999A");

        Assert.IsInstanceOfType<Half>(half);
        Assert.AreEqual((Half)1.0, (Half)half);
        Assert.IsInstanceOfType<float>(single);
        Assert.AreEqual(100_000.0f, (float)single);
        Assert.IsInstanceOfType<double>(doubleValue);
        Assert.AreEqual(1.1, (double)doubleValue);
    }

    [TestMethod]
    public void Reader_PreservesRfc8949TagAndTaggedValue()
    {
        object? value = DecodeComplete("C11A514B67B0");

        CborTaggedValue tagged = Assert.IsInstanceOfType<CborTaggedValue>(value);
        Assert.AreEqual(1UL, tagged.Tag);
        Assert.AreEqual(1_363_896_240UL, tagged.Value);
    }

    [TestMethod]
    public void DiagnosticDump_ReportsRfc8949VectorsAndLeadingByteAmbiguity()
    {
        (string Name, string Hex)[] vectors =
        [
            ("inline unsigned", "0A"),
            ("one-byte argument", "1818"),
            ("two-byte argument", "1903E8"),
            ("four-byte argument", "1A000F4240"),
            ("eight-byte argument", "1B000000E8D4A51000"),
            ("half", "F93C00"),
            ("single", "FA47C35000"),
            ("double", "FB3FF199999999999A"),
            ("tag", "C11A514B67B0"),
        ];

        foreach ((string name, string hex) in vectors)
        {
            TestContext.WriteLine($"RFC 8949 {name} hex={hex}");
            foreach (string line in CborReader.Dump(Convert.FromHexString(hex)))
            {
                TestContext.WriteLine(line);
            }
        }

        IReadOnlyList<string> ambiguityDump = CborReader.Dump(
            Convert.FromHexString("7A0000000161"));
        Assert.HasCount(1, ambiguityDump);
        string ambiguity = ambiguityDump[0];
        StringAssert.Contains(ambiguity, "major=3(text-string) additional=26");
        StringAssert.Contains(ambiguity, "leadingByteAsLoraxStatus=0x7A");
        StringAssert.Contains(ambiguity, "decoded=\"a\"");
        TestContext.WriteLine("Ambiguous leading byte hex=7A0000000161");
        TestContext.WriteLine(ambiguity);
    }

    private static object? DecodeComplete(string hex)
    {
        CborReader reader = new(Convert.FromHexString(hex));
        object? value = reader.ReadValue(depth: 0);
        Assert.IsTrue(reader.IsComplete, $"RFC 8949 vector {hex} left trailing bytes.");
        return value;
    }
}
