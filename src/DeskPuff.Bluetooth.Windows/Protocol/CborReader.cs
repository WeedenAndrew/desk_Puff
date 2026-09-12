using System.Globalization;
using System.Text;

namespace DeskPuff.Bluetooth.Windows.Protocol;

internal sealed record CborTaggedValue(ulong Tag, object? Value);

internal sealed class CborReader
{
    private const int MaximumDepth = 8;
    private const int MaximumCollectionItems = 32;
    private const int MaximumByteStringLength = 4096;
    private readonly byte[] bytes;
    private readonly List<TraceItem>? traceItems;
    private int offset;

    internal CborReader(ReadOnlySpan<byte> bytes, bool captureTrace = false)
    {
        this.bytes = bytes.ToArray();
        traceItems = captureTrace ? [] : null;
    }

    internal bool IsComplete => offset == bytes.Length;

    internal object? ReadValue(int depth)
    {
        if (depth > MaximumDepth || offset >= bytes.Length)
        {
            throw new InvalidDataException("CBOR is truncated or too deeply nested.");
        }

        int itemOffset = offset;
        byte initial = bytes[offset++];
        int majorType = initial >> 5;
        int additionalInformation = initial & 0x1F;
        ulong? argument = null;
        try
        {
            argument = ReadArgument(additionalInformation);
            object? value = majorType switch
            {
                0 => argument.Value,
                1 => checked(-1L - (long)argument.Value),
                2 => ReadBytes(argument.Value),
                3 => Encoding.UTF8.GetString(ReadBytes(argument.Value)),
                4 => ReadArray(argument.Value, depth + 1),
                5 => ReadMap(argument.Value, depth + 1),
                6 => new CborTaggedValue(argument.Value, ReadValue(depth + 1)),
                7 => ReadSimple(additionalInformation, argument.Value),
                _ => throw new InvalidDataException("CBOR uses an unsupported major type."),
            };
            CaptureTrace(itemOffset, majorType, additionalInformation, value, error: null);
            return value;
        }
        catch (Exception exception) when (exception is InvalidDataException or OverflowException)
        {
            CaptureTrace(itemOffset, majorType, additionalInformation, argument, exception.Message);
            throw;
        }
    }

    internal static IReadOnlyList<string> Dump(ReadOnlySpan<byte> bytes)
    {
        CborReader reader = new(bytes, captureTrace: true);
        try
        {
            _ = reader.ReadValue(depth: 0);
            if (!reader.IsComplete)
            {
                reader.traceItems!.Add(new TraceItem(
                    reader.offset,
                    bytes.Length - reader.offset,
                    MajorType: null,
                    AdditionalInformation: null,
                    Value: null,
                    Error: "Trailing bytes remain after the first CBOR item."));
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or OverflowException)
        {
            // The trace records the failing item. A diagnostic dump must remain available
            // for malformed input so that the raw bytes and failed interpretation stay visible.
        }

        return reader.traceItems!
            .OrderBy(item => item.Offset)
            .ThenByDescending(item => item.Length)
            .Select(reader.FormatTraceItem)
            .ToArray();
    }

    private ulong ReadArgument(int additionalInformation) => additionalInformation switch
    {
        < 24 => (ulong)additionalInformation,
        24 => ReadUnsigned(1),
        25 => ReadUnsigned(2),
        26 => ReadUnsigned(4),
        27 => ReadUnsigned(8),
        31 => throw new InvalidDataException("Indefinite-length CBOR values are not accepted."),
        _ => throw new InvalidDataException("CBOR uses a reserved additional-information value."),
    };

    private ulong ReadUnsigned(int length)
    {
        EnsureRemaining(length);
        ulong result = 0;
        for (int index = 0; index < length; index++)
        {
            result = (result << 8) | bytes[offset++];
        }

        return result;
    }

    private byte[] ReadBytes(ulong lengthValue)
    {
        int length = CheckedLength(lengthValue, MaximumByteStringLength);
        EnsureRemaining(length);
        byte[] value = bytes.AsSpan(offset, length).ToArray();
        offset += length;
        return value;
    }

    private List<object?> ReadArray(ulong countValue, int depth)
    {
        int count = CheckedLength(countValue, MaximumCollectionItems);
        List<object?> result = new(count);
        for (int index = 0; index < count; index++)
        {
            result.Add(ReadValue(depth));
        }

        return result;
    }

    private Dictionary<string, object?> ReadMap(ulong countValue, int depth)
    {
        int count = CheckedLength(countValue, MaximumCollectionItems);
        Dictionary<string, object?> result = new(count, StringComparer.Ordinal);
        for (int index = 0; index < count; index++)
        {
            if (ReadValue(depth) is not string key || !result.TryAdd(key, ReadValue(depth)))
            {
                throw new InvalidDataException("CBOR contains an invalid map key.");
            }
        }

        return result;
    }

    private static object? ReadSimple(int additionalInformation, ulong argument) => additionalInformation switch
    {
        20 => false,
        21 => true,
        22 => null,
        // Preserve the encoded precision while materialising the numeric value. Returning
        // raw bits made ordinary CBOR floats resemble plausible but incorrect integers.
        25 => BitConverter.UInt16BitsToHalf(checked((ushort)argument)),
        26 => BitConverter.Int32BitsToSingle(unchecked((int)checked((uint)argument))),
        27 => BitConverter.Int64BitsToDouble(unchecked((long)argument)),
        _ => argument,
    };

    private static int CheckedLength(ulong value, int maximum) =>
        value <= (ulong)maximum
            ? (int)value
            : throw new InvalidDataException("CBOR exceeds its collection bounds.");

    private void EnsureRemaining(int count)
    {
        if (count < 0 || offset > bytes.Length - count)
        {
            throw new InvalidDataException("CBOR is truncated.");
        }
    }

    private void CaptureTrace(
        int itemOffset,
        int majorType,
        int additionalInformation,
        object? value,
        string? error)
    {
        traceItems?.Add(new TraceItem(
            itemOffset,
            Math.Max(1, offset - itemOffset),
            majorType,
            additionalInformation,
            value,
            error));
    }

    private string FormatTraceItem(TraceItem item)
    {
        string raw = item.Length > 0
            ? Convert.ToHexString(bytes.AsSpan(item.Offset, item.Length))
            : string.Empty;
        if (item.MajorType is not int majorType ||
            item.AdditionalInformation is not int additionalInformation)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"offset={item.Offset} raw={raw} error=\"{Escape(item.Error ?? string.Empty)}\"");
        }

        string statusCandidate = item.Offset == 0
            ? $" leadingByteAsLoraxStatus=0x{bytes[0]:X2}"
            : string.Empty;
        string decoded = item.Error is null
            ? FormatValue(item.Value, majorType)
            : $"<error: {Escape(item.Error)}>";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"offset={item.Offset} raw={raw} major={majorType}({MajorTypeName(majorType)}) additional={additionalInformation}{statusCandidate} decoded={decoded}");
    }

    private static string FormatValue(object? value, int majorType) => value switch
    {
        null => "null",
        bool boolean => boolean ? "true" : "false",
        string text => $"\"{Escape(text)}\"",
        byte[] byteString => $"h'{Convert.ToHexString(byteString)}'",
        Half half => half.ToString("R", CultureInfo.InvariantCulture),
        float single => single.ToString("R", CultureInfo.InvariantCulture),
        double doubleValue => doubleValue.ToString("R", CultureInfo.InvariantCulture),
        CborTaggedValue tagged => $"tag({tagged.Tag.ToString(CultureInfo.InvariantCulture)}, {FormatScalar(tagged.Value)})",
        List<object?> array when majorType == 4 => $"array({array.Count.ToString(CultureInfo.InvariantCulture)})",
        Dictionary<string, object?> map when majorType == 5 => $"map({map.Count.ToString(CultureInfo.InvariantCulture)})",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static string FormatScalar(object? value) => value switch
    {
        null => "null",
        string text => $"\"{Escape(text)}\"",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static string Escape(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal);

    private static string MajorTypeName(int majorType) => majorType switch
    {
        0 => "unsigned",
        1 => "negative",
        2 => "byte-string",
        3 => "text-string",
        4 => "array",
        5 => "map",
        6 => "tag",
        7 => "simple-or-float",
        _ => "unknown",
    };

    private sealed record TraceItem(
        int Offset,
        int Length,
        int? MajorType,
        int? AdditionalInformation,
        object? Value,
        string? Error);
}
