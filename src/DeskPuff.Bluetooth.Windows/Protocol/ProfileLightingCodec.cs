using System.Buffers;
using System.Globalization;
using System.Text;

namespace DeskPuff.Bluetooth.Windows.Protocol;

internal static class ProfileLightingCodec
{
    private const int MaximumColorCount = 16;
    private const int MaximumCborBytes = 512;

    internal static byte[] EncodeSolid(IReadOnlyList<string> colors)
    {
        ArgumentNullException.ThrowIfNull(colors);
        if (colors.Count is < 1 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(colors), "A profile palette requires one to four colors.");
        }

        byte[] rgb = new byte[colors.Count * 3];
        for (int index = 0; index < colors.Count; index++)
        {
            string color = colors[index];
            if (color.Length != 7 || color[0] != '#' ||
                !byte.TryParse(color.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgb[index * 3]) ||
                !byte.TryParse(color.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgb[(index * 3) + 1]) ||
                !byte.TryParse(color.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgb[(index * 3) + 2]))
            {
                throw new ArgumentException("Palette colors must be six-digit RGB values.", nameof(colors));
            }
        }

        ArrayBufferWriter<byte> writer = new(48);
        WriteInitial(writer, majorType: 5, 1);
        WriteText(writer, "lamp");
        WriteInitial(writer, majorType: 5, 2);
        WriteText(writer, "name");
        WriteText(writer, "solid");
        WriteText(writer, "param");
        WriteInitial(writer, majorType: 5, 1);
        WriteText(writer, "color");
        WriteInitial(writer, majorType: 2, rgb.Length);
        writer.Write(rgb);
        return writer.WrittenSpan.ToArray();
    }

    internal static IReadOnlyList<string> DecodeColors(ReadOnlySpan<byte> cbor)
    {
        if (cbor.Length is < 1 or > MaximumCborBytes)
        {
            throw new InvalidDataException("Profile lighting CBOR is empty or exceeds its bounded size.");
        }

        CborReader reader = new(cbor.ToArray());
        object? root = reader.ReadValue(depth: 0);
        if (!reader.IsComplete ||
            root is not Dictionary<string, object?> rootMap ||
            !rootMap.TryGetValue("lamp", out object? lampValue) ||
            lampValue is not Dictionary<string, object?> lamp ||
            !lamp.TryGetValue("param", out object? parameterValue) ||
            parameterValue is not Dictionary<string, object?> parameters ||
            !parameters.TryGetValue("color", out object? colorValue) ||
            colorValue is not byte[] rgb ||
            rgb.Length is < 3 ||
            rgb.Length % 3 != 0 ||
            rgb.Length / 3 > MaximumColorCount)
        {
            throw new InvalidDataException("Profile lighting CBOR does not contain a bounded RGB color array.");
        }

        string[] colors = new string[rgb.Length / 3];
        for (int index = 0; index < colors.Length; index++)
        {
            colors[index] = $"#{rgb[index * 3]:X2}{rgb[(index * 3) + 1]:X2}{rgb[(index * 3) + 2]:X2}";
        }

        return colors;
    }

    private static void WriteText(IBufferWriter<byte> writer, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        WriteInitial(writer, majorType: 3, bytes.Length);
        writer.Write(bytes);
    }

    private static void WriteInitial(IBufferWriter<byte> writer, byte majorType, int value)
    {
        if (value is < 0 or > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        Span<byte> destination = writer.GetSpan(2);
        if (value < 24)
        {
            destination[0] = (byte)((majorType << 5) | value);
            writer.Advance(1);
            return;
        }

        destination[0] = (byte)((majorType << 5) | 24);
        destination[1] = (byte)value;
        writer.Advance(2);
    }

    private sealed class CborReader(byte[] bytes)
    {
        private const int MaximumDepth = 8;
        private const int MaximumCollectionItems = 32;
        private int offset;

        internal bool IsComplete => offset == bytes.Length;

        internal object? ReadValue(int depth)
        {
            if (depth > MaximumDepth || offset >= bytes.Length)
            {
                throw new InvalidDataException("Profile lighting CBOR is truncated or too deeply nested.");
            }

            byte initial = bytes[offset++];
            int majorType = initial >> 5;
            ulong argument = ReadArgument(initial & 0x1F);
            return majorType switch
            {
                0 => argument,
                1 => checked(-1L - (long)argument),
                2 => ReadBytes(argument),
                3 => Encoding.UTF8.GetString(ReadBytes(argument)),
                4 => ReadArray(argument, depth + 1),
                5 => ReadMap(argument, depth + 1),
                6 => ReadValue(depth + 1),
                7 => ReadSimple(initial & 0x1F, argument),
                _ => throw new InvalidDataException("Profile lighting CBOR uses an unsupported major type."),
            };
        }

        private ulong ReadArgument(int additionalInformation) => additionalInformation switch
        {
            < 24 => (ulong)additionalInformation,
            24 => ReadUnsigned(1),
            25 => ReadUnsigned(2),
            26 => ReadUnsigned(4),
            _ => throw new InvalidDataException("Indefinite or oversized CBOR values are not accepted."),
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
            int length = CheckedLength(lengthValue, MaximumCborBytes);
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
                    throw new InvalidDataException("Profile lighting CBOR contains an invalid map key.");
                }
            }

            return result;
        }

        private static object? ReadSimple(int additionalInformation, ulong argument) => additionalInformation switch
        {
            20 => false,
            21 => true,
            22 => null,
            _ => argument,
        };

        private static int CheckedLength(ulong value, int maximum) =>
            value <= (ulong)maximum
                ? (int)value
                : throw new InvalidDataException("Profile lighting CBOR exceeds its collection bounds.");

        private void EnsureRemaining(int count)
        {
            if (count < 0 || offset > bytes.Length - count)
            {
                throw new InvalidDataException("Profile lighting CBOR is truncated.");
            }
        }
    }
}
