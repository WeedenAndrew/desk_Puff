using System.Globalization;
using System.Text;
using DeskPuff.Bluetooth.Windows.Protocol;

namespace DeskPuff.Bluetooth.Windows.Tests;

[TestClass]
public sealed class CapturedProfileCborInventoryTests
{
    private const string CaptureFileName = "survey-20260914-004800.log";
    private const int DisplayedByteCount = 24;

    private static readonly string[] ExpectedPaths =
    [
        "/u/app/hc/0/colr",
        "/u/app/hc/1/colr",
        "/u/app/hc/2/colr",
        "/u/app/hc/3/colr",
        "/p/app/thc/colr",
        "/p/app/ltrn/colr",
    ];

    private static readonly HashSet<string> ModeledProfileLightingPaths = new(StringComparer.Ordinal)
    {
        "lamp",
        "lamp.name",
        "lamp.param",
        "lamp.param.color",
        "meta",
        "meta.moodName",
        "meta.userColors",
    };

    private readonly List<string> reportLines = [];

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void CapturedProfileDocuments_InventoryAndCompare()
    {
        string? capturePath = FindCapturePath();
        if (capturePath is null)
        {
            Assert.Inconclusive(
                $"Set DESK_PUFF_CAPTURE_LOG or place {CaptureFileName} under tools/capture to run the local CBOR inventory.");
            return;
        }

        Dictionary<string, CapturedDocument> captures = ReadCapturedDocuments(capturePath);
        Assert.HasCount(ExpectedPaths.Length, captures, "The capture must contain exactly six complete target documents.");
        foreach (string path in ExpectedPaths)
        {
            Assert.IsTrue(captures.ContainsKey(path), $"The capture is missing {path}.");
        }

        Dictionary<string, DecodedDocument> decoded = new(StringComparer.Ordinal);
        foreach (string path in ExpectedPaths)
        {
            CapturedDocument capture = captures[path];
            Assert.AreEqual(
                capture.DeclaredLength,
                capture.Bytes.Length,
                $"Hex extraction for {path} does not match totalLength.");
            DecodedDocument document = DecodeDocument(capture);
            decoded.Add(path, document);
            WriteInventory(document);
        }

        WriteVaporComparisons(decoded);
        WriteActiveSlotComparison(captures);
        WriteUnmodeledKeys(decoded);
        WriteReportArtifact();
    }

    private static string? FindCapturePath()
    {
        string? configured = Environment.GetEnvironmentVariable("DESK_PUFF_CAPTURE_LOG");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return File.Exists(configured)
                ? Path.GetFullPath(configured)
                : throw new FileNotFoundException(
                    "DESK_PUFF_CAPTURE_LOG names a file that does not exist.",
                    configured);
        }

        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(
                directory.FullName,
                "tools",
                "capture",
                CaptureFileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static Dictionary<string, CapturedDocument> ReadCapturedDocuments(string capturePath)
    {
        const string prefix = "BLOB COMPLETE path=";
        const string totalMarker = " totalLength=";
        const string offsetMarker = " terminalOffset=";
        const string terminalMarker = " terminalLength=";
        const string hexMarker = " hex=";
        Dictionary<string, CapturedDocument> captures = new(StringComparer.Ordinal);

        foreach (string rawLine in File.ReadLines(capturePath))
        {
            string line = rawLine.TrimStart();
            if (!line.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            int totalStart = line.IndexOf(totalMarker, prefix.Length, StringComparison.Ordinal);
            int offsetStart = line.IndexOf(offsetMarker, totalStart + totalMarker.Length, StringComparison.Ordinal);
            int terminalStart = line.IndexOf(terminalMarker, offsetStart + offsetMarker.Length, StringComparison.Ordinal);
            int hexStart = line.IndexOf(hexMarker, terminalStart + terminalMarker.Length, StringComparison.Ordinal);
            Assert.IsTrue(
                totalStart > prefix.Length && offsetStart > totalStart && terminalStart > offsetStart && hexStart > terminalStart,
                "A BLOB COMPLETE record has an unexpected field layout.");

            string path = line[prefix.Length..totalStart];
            if (!ExpectedPaths.Contains(path, StringComparer.Ordinal))
            {
                continue;
            }

            int declaredLength = int.Parse(
                line.AsSpan(totalStart + totalMarker.Length, offsetStart - totalStart - totalMarker.Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture);
            string[] hexTokens = line[(hexStart + hexMarker.Length)..]
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            byte[] bytes = new byte[hexTokens.Length];
            for (int index = 0; index < hexTokens.Length; index++)
            {
                Assert.AreEqual(2, hexTokens[index].Length, $"Invalid hex token for {path} at index {index}.");
                bytes[index] = byte.Parse(hexTokens[index], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }

            Assert.IsTrue(
                captures.TryAdd(path, new CapturedDocument(path, declaredLength, bytes)),
                $"The capture contains more than one complete document for {path}.");
        }

        return captures;
    }

    private static DecodedDocument DecodeDocument(CapturedDocument capture)
    {
        CborReader reader = new(capture.Bytes);
        object? root;
        try
        {
            root = reader.ReadValue(depth: 0);
        }
        catch (InvalidDataException exception) when (
            exception.Message.Equals("CBOR exceeds its collection bounds.", StringComparison.Ordinal))
        {
            throw new AssertFailedException(
                $"{capture.Path} exceeded CborReader.MaximumCollectionItems=32. " +
                "MaximumByteStringLength=4096 cannot bind because the complete document is smaller than 4096 bytes.",
                exception);
        }
        catch (InvalidDataException exception) when (
            exception.Message.Equals("CBOR is truncated or too deeply nested.", StringComparison.Ordinal))
        {
            throw new AssertFailedException(
                $"{capture.Path} exceeded CborReader.MaximumDepth=8. Hex extraction already matched the device's " +
                "declared complete length, so this diagnostic does not classify the value as truncated.",
                exception);
        }

        Assert.IsTrue(reader.IsComplete, $"{capture.Path} left trailing bytes after its root CBOR value.");
        List<InventoryEntry> entries = [];
        Walk(root, "$", isMapKey: false, entries);
        return new DecodedDocument(capture, entries);
    }

    private static void Walk(object? value, string path, bool isMapKey, List<InventoryEntry> entries)
    {
        switch (value)
        {
            case Dictionary<string, object?> map:
                entries.Add(new InventoryEntry(path, "map", $"count={map.Count}", $"map:{map.Count}", false, isMapKey));
                foreach ((string key, object? child) in map)
                {
                    string childPath = path == "$" ? key : $"{path}.{key}";
                    Walk(child, childPath, isMapKey: true, entries);
                }

                break;
            case List<object?> array:
                entries.Add(new InventoryEntry(path, "array", $"count={array.Count}", $"array:{array.Count}", false, isMapKey));
                for (int index = 0; index < array.Count; index++)
                {
                    Walk(array[index], $"{path}[{index}]", isMapKey: false, entries);
                }

                break;
            case byte[] bytes:
                int displayed = Math.Min(bytes.Length, DisplayedByteCount);
                string suffix = displayed < bytes.Length ? "..." : string.Empty;
                string fullHex = Convert.ToHexString(bytes);
                entries.Add(new InventoryEntry(
                    path,
                    "byte-string",
                    $"length={bytes.Length} hex={Convert.ToHexString(bytes.AsSpan(0, displayed))}{suffix}",
                    $"bytes:{fullHex}",
                    true,
                    isMapKey));
                break;
            case string text:
                entries.Add(Scalar(path, "text", Quote(text), $"text:{text}", isMapKey));
                break;
            case bool boolean:
                entries.Add(Scalar(path, "boolean", boolean ? "true" : "false", $"bool:{boolean}", isMapKey));
                break;
            case null:
                entries.Add(Scalar(path, "null", "null", "null", isMapKey));
                break;
            case Half half:
                entries.Add(Scalar(
                    path,
                    "float16",
                    half.ToString(CultureInfo.InvariantCulture),
                    $"half:{BitConverter.HalfToUInt16Bits(half):X4}",
                    isMapKey));
                break;
            case float single:
                entries.Add(Scalar(
                    path,
                    "float32",
                    single.ToString("R", CultureInfo.InvariantCulture),
                    $"single:{BitConverter.SingleToInt32Bits(single):X8}",
                    isMapKey));
                break;
            case double doubleValue:
                entries.Add(Scalar(
                    path,
                    "float64",
                    doubleValue.ToString("R", CultureInfo.InvariantCulture),
                    $"double:{BitConverter.DoubleToInt64Bits(doubleValue):X16}",
                    isMapKey));
                break;
            case ulong unsigned:
                entries.Add(Scalar(path, "unsigned", unsigned.ToString(CultureInfo.InvariantCulture), $"uint:{unsigned}", isMapKey));
                break;
            case long negative:
                entries.Add(Scalar(path, "negative", negative.ToString(CultureInfo.InvariantCulture), $"int:{negative}", isMapKey));
                break;
            case CborTaggedValue tagged:
                entries.Add(new InventoryEntry(path, "tag", $"tag={tagged.Tag}", $"tag:{tagged.Tag}", false, isMapKey));
                Walk(tagged.Value, $"{path}.value", isMapKey: false, entries);
                break;
            default:
                throw new AssertFailedException($"{path} materialized as unsupported runtime type {value.GetType().FullName}.");
        }
    }

    private static InventoryEntry Scalar(
        string path,
        string type,
        string display,
        string comparable,
        bool isMapKey) =>
        new(path, type, display, comparable, true, isMapKey);

    private void WriteInventory(DecodedDocument document)
    {
        Report($"=== INVENTORY {document.Capture.Path} length={document.Capture.Bytes.Length} ===");
        foreach (InventoryEntry entry in document.Entries)
        {
            Report($"{entry.Path} type={entry.Type} value={entry.Display}");
        }
    }

    private void WriteVaporComparisons(IReadOnlyDictionary<string, DecodedDocument> documents)
    {
        Dictionary<string, InventoryEntry> zero = LeafEntries(documents["/u/app/hc/0/colr"]);
        Dictionary<string, InventoryEntry> one = LeafEntries(documents["/u/app/hc/1/colr"]);
        Dictionary<string, InventoryEntry> two = LeafEntries(documents["/u/app/hc/2/colr"]);
        Dictionary<string, InventoryEntry> three = LeafEntries(documents["/u/app/hc/3/colr"]);

        int testDifferenceCount = WriteDifferenceSet(
            "TEST slot 1 pikaled2 Bold vs slot 2 pikaled2 Extreme",
            one,
            two);
        int controlDifferenceCount = WriteDifferenceSet(
            "CONTROL slot 0 migrtn1 Extreme vs slot 3 migrtn1 Extreme",
            zero,
            three);

        Report("=== EXPECTED PALETTE DIFFERENCES slot 1 vs slot 2 ===");
        foreach (string path in new[] { "lamp.param.color", "lamp.param.colorLen" })
        {
            Report($"{path} slot1={Describe(one, path)} slot2={Describe(two, path)}");
        }

        if (controlDifferenceCount > testDifferenceCount)
        {
            Report(
                "=== VAPOR CANDIDATES not-reported pairingValid=False " +
                $"reason=\"control differences {controlDifferenceCount} exceed test differences {testDifferenceCount}\" ===");
            return;
        }

        string[] candidates = one.Keys
            .Intersect(two.Keys, StringComparer.Ordinal)
            .Intersect(zero.Keys, StringComparer.Ordinal)
            .Intersect(three.Keys, StringComparer.Ordinal)
            .Where(path =>
                !path.Equals("lamp.param.color", StringComparison.Ordinal) &&
                !path.Equals("lamp.param.colorLen", StringComparison.Ordinal) &&
                !Same(one[path], two[path]) &&
                Same(zero[path], three[path]))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Report($"=== VAPOR CANDIDATES count={candidates.Length} ===");
        foreach (string path in candidates)
        {
            Report(
                $"{path} slot1={one[path].Type}:{one[path].Display} " +
                $"slot2={two[path].Type}:{two[path].Display} " +
                $"slot0={zero[path].Type}:{zero[path].Display} " +
                $"slot3={three[path].Type}:{three[path].Display}");
        }
    }

    private int WriteDifferenceSet(
        string title,
        IReadOnlyDictionary<string, InventoryEntry> left,
        IReadOnlyDictionary<string, InventoryEntry> right)
    {
        string[] paths = left.Keys
            .Union(right.Keys, StringComparer.Ordinal)
            .Where(path => !left.TryGetValue(path, out InventoryEntry? leftValue) ||
                !right.TryGetValue(path, out InventoryEntry? rightValue) ||
                !Same(leftValue, rightValue))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Report($"=== {title} differences={paths.Length} ===");
        foreach (string path in paths)
        {
            Report($"{path} left={Describe(left, path)} right={Describe(right, path)}");
        }

        return paths.Length;
    }

    private static Dictionary<string, InventoryEntry> LeafEntries(DecodedDocument document) =>
        document.Entries
            .Where(entry => entry.IsLeaf)
            .ToDictionary(entry => entry.Path, StringComparer.Ordinal);

    private static bool Same(InventoryEntry left, InventoryEntry right) =>
        left.Type.Equals(right.Type, StringComparison.Ordinal) &&
        left.Comparable.Equals(right.Comparable, StringComparison.Ordinal);

    private static string Describe(IReadOnlyDictionary<string, InventoryEntry> entries, string path) =>
        entries.TryGetValue(path, out InventoryEntry? value)
            ? $"{value.Type}:{value.Display}"
            : "<absent>";

    private void WriteActiveSlotComparison(IReadOnlyDictionary<string, CapturedDocument> captures)
    {
        byte[] slot = captures["/u/app/hc/3/colr"].Bytes;
        byte[] active = captures["/p/app/thc/colr"].Bytes;
        bool identical = slot.AsSpan().SequenceEqual(active);
        Report(
            $"=== ACTIVE SLOT COMPARISON byteIdentical={identical} slotLength={slot.Length} activeLength={active.Length} ===");
        if (identical)
        {
            return;
        }

        int sharedLength = Math.Min(slot.Length, active.Length);
        int[] differingOffsets = Enumerable.Range(0, sharedLength)
            .Where(index => slot[index] != active[index])
            .ToArray();
        Report(
            $"differingOffsets={string.Join(',', differingOffsets)} extraSlotBytes={slot.Length - sharedLength} " +
            $"extraActiveBytes={active.Length - sharedLength}");
    }

    private void WriteUnmodeledKeys(IReadOnlyDictionary<string, DecodedDocument> documents)
    {
        foreach (string path in ExpectedPaths)
        {
            InventoryEntry[] unmodeled = documents[path].Entries
                .Where(entry => entry.IsMapKey && !ModeledProfileLightingPaths.Contains(entry.Path))
                .ToArray();
            Report($"=== UNMODELED KEYS {path} count={unmodeled.Length} ===");
            foreach (InventoryEntry entry in unmodeled)
            {
                Report($"{entry.Path} type={entry.Type} value={entry.Display}");
            }
        }
    }

    private void Report(string line)
    {
        reportLines.Add(line);
        TestContext.WriteLine(line);
    }

    private void WriteReportArtifact()
    {
        string outputDirectory = TestContext.TestRunResultsDirectory
            ?? throw new InvalidOperationException("The test run results directory is unavailable.");
        string outputPath = Path.Combine(outputDirectory, "cbor-inventory.txt");
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllLines(
            outputPath,
            reportLines,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true));
        TestContext.AddResultFile(outputPath);
    }

    private static string Quote(string value) =>
        $"\"{value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)}\"";

    private sealed record CapturedDocument(string Path, int DeclaredLength, byte[] Bytes);

    private sealed record DecodedDocument(CapturedDocument Capture, IReadOnlyList<InventoryEntry> Entries);

    private sealed record InventoryEntry(
        string Path,
        string Type,
        string Display,
        string Comparable,
        bool IsLeaf,
        bool IsMapKey);
}
