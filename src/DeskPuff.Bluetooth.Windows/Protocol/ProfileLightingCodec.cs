using System.Buffers;
using System.Globalization;
using System.Text;

namespace DeskPuff.Bluetooth.Windows.Protocol;

internal static class ProfileLightingCodec
{
    internal enum PaletteSource
    {
        None,
        MetaUserColors,
        LampParamColor,
    }

    internal sealed record DecodedLighting(
        IReadOnlyList<string> Colors,
        PaletteSource Source,
        string? MoodName,
        string? LampName);

    // A PEAKSHI V2 on firmware 39 returned 25 colors on 2026-08-28. Thirty-two
    // keeps the decoder bounded while leaving headroom above the observed maximum.
    private const int MaximumColorCount = 32;
    // PEAKSHI V2 firmware 39 returned 517, 466, 517, and 799-byte documents for profile
    // slots 0-3 on 2026-08-31. 4096 remains bounded headroom above the observed maximum, 799.
    private const int MaximumCborBytes = 4096;

    // Do not reserialize unknown device fields: their wire types, precision and
    // ordering can matter. Only equal-length payload edits are supported until
    // animation/ramp generation and file truncation semantics are established.
    internal static byte[] PrepareColorUpdate(ReadOnlySpan<byte> original, IReadOnlyList<string> colors)
    {
        _ = EncodeSolid(colors); // Validate the requested palette, never send this replacement document.
        DecodedLighting decoded = DecodeLighting(original);
        if (decoded.Colors.SequenceEqual(colors, StringComparer.OrdinalIgnoreCase))
        {
            return original.ToArray();
        }

        const string unsupported = "This device colorway cannot be edited safely. Keep its existing colors " +
            "to change heating settings; color edits require an existing solid RGB palette with the same number of stops.";
        CborReader reader = new(original, captureRanges: true);
        if (reader.ReadValue(0) is not Dictionary<string, object?> root || !reader.IsComplete ||
            !root.TryGetValue("lamp", out object? lampValue) || lampValue is not Dictionary<string, object?> lamp ||
            !lamp.TryGetValue("name", out object? name) || name is not string program || program != "solid" ||
            !TryGetInterpolatedColor(root, out object? colorValue) || colorValue is not byte[] rgb ||
            rgb.Length != colors.Count * 3)
        {
            throw new InvalidDataException(unsupported);
        }

        byte[] result = original.ToArray();
        var rgbRange = reader.GetEncodedRange(rgb);
        int rgbOffset = rgbRange.Offset + rgbRange.Length - rgb.Length;
        for (int index = 0; index < colors.Count; index++)
        {
            Convert.FromHexString(colors[index].AsSpan(1)).CopyTo(result, rgbOffset + (index * 3));
        }

        if (root.TryGetValue("meta", out object? metadataValue))
        {
            if (metadataValue is not Dictionary<string, object?> metadata)
            {
                throw new InvalidDataException(unsupported);
            }

            if (metadata.TryGetValue("userColors", out object? userColorsValue))
            {
                if (userColorsValue is not List<object?> userColors || userColors.Count != colors.Count ||
                    !TryDecodeUserColors(root, out IReadOnlyList<string> anchors))
                {
                    throw new InvalidDataException(unsupported);
                }

                for (int index = 0; index < colors.Count; index++)
                {
                    string existingRgb = $"#{rgb[index * 3]:X2}{rgb[(index * 3) + 1]:X2}{rgb[(index * 3) + 2]:X2}";
                    if (!string.Equals(existingRgb, anchors[index], StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(unsupported);
                    }

                    string existing = (string)userColors[index]!;
                    var range = reader.GetEncodedRange(existing);
                    Encoding.ASCII.GetBytes(colors[index]).CopyTo(result, range.Offset + range.Length - 7);
                }
            }
        }

        return result;
    }

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

    internal static IReadOnlyList<string> DecodeColors(ReadOnlySpan<byte> cbor) =>
        DecodeLighting(cbor).Colors;

    internal static IReadOnlyList<string> DecodeColors(
        ReadOnlySpan<byte> cbor,
        out PaletteSource source)
    {
        DecodedLighting decoded = DecodeLighting(cbor);
        source = decoded.Source;
        return decoded.Colors;
    }

    internal static DecodedLighting DecodeLighting(ReadOnlySpan<byte> cbor)
    {
        if (cbor.Length is < 1 or > MaximumCborBytes)
        {
            throw new InvalidDataException("Profile lighting CBOR is empty or exceeds its bounded size.");
        }

        CborReader reader = new(cbor);
        object? root = reader.ReadValue(depth: 0);
        if (!reader.IsComplete ||
            root is not Dictionary<string, object?> rootMap)
        {
            throw new InvalidDataException("Profile lighting CBOR does not contain a bounded RGB color array.");
        }

        string? moodName = ReadOptionalString(rootMap, "meta", "moodName");
        string? lampName = ReadOptionalString(rootMap, "lamp", "name");
        if (TryDecodeUserColors(rootMap, out IReadOnlyList<string> userColors))
        {
            return new DecodedLighting(
                userColors,
                PaletteSource.MetaUserColors,
                moodName,
                lampName);
        }

        if (!TryGetInterpolatedColor(rootMap, out object? colorValue))
        {
            return new DecodedLighting([], PaletteSource.None, moodName, lampName);
        }

        if (colorValue is not byte[] rgb ||
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

        return new DecodedLighting(
            colors,
            PaletteSource.LampParamColor,
            moodName,
            lampName);
    }

    private static string? ReadOptionalString(
        Dictionary<string, object?> root,
        string containerKey,
        string valueKey) =>
        root.TryGetValue(containerKey, out object? containerValue) &&
        containerValue is Dictionary<string, object?> container &&
        container.TryGetValue(valueKey, out object? value) &&
        value is string text &&
        text.Length > 0
            ? text
            : null;

    private static bool TryDecodeUserColors(
        Dictionary<string, object?> root,
        out IReadOnlyList<string> colors)
    {
        colors = [];
        if (!root.TryGetValue("meta", out object? metaValue) ||
            metaValue is not Dictionary<string, object?> metadata ||
            !metadata.TryGetValue("userColors", out object? userColorsValue) ||
            userColorsValue is not List<object?> userColors ||
            userColors.Count == 0)
        {
            return false;
        }

        string[] normalized = new string[userColors.Count];
        for (int index = 0; index < userColors.Count; index++)
        {
            if (!TryNormalizeColor(userColors[index], out normalized[index]))
            {
                return false;
            }
        }

        colors = normalized;
        return true;
    }

    private static bool TryGetInterpolatedColor(
        Dictionary<string, object?> root,
        out object? colorValue)
    {
        colorValue = null;
        return root.TryGetValue("lamp", out object? lampValue) &&
            lampValue is Dictionary<string, object?> lamp &&
            lamp.TryGetValue("param", out object? parameterValue) &&
            parameterValue is Dictionary<string, object?> parameters &&
            parameters.TryGetValue("color", out colorValue);
    }

    private static bool TryNormalizeColor(object? value, out string normalized)
    {
        normalized = string.Empty;
        if (value is not string color ||
            color.Length != 7 ||
            color[0] != '#' ||
            !uint.TryParse(
                color.AsSpan(1),
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out _))
        {
            return false;
        }

        // Device metadata is lowercase while app palettes are uppercase; normalize so
        // visually identical device and local colors also compare equal.
        normalized = color.ToUpperInvariant();
        return true;
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

}
