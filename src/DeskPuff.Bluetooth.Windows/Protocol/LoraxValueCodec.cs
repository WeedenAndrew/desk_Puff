using System.Buffers.Binary;
using System.Text;

namespace DeskPuff.Bluetooth.Windows.Protocol;

internal static class LoraxValueCodec
{
    internal static float ReadSingle(ReadOnlySpan<byte> value)
    {
        if (value.Length < sizeof(float))
        {
            throw new InvalidDataException("Expected a four-byte floating-point value.");
        }

        return BinaryPrimitives.ReadSingleLittleEndian(value);
    }

    internal static uint ReadUInt32(ReadOnlySpan<byte> value)
    {
        if (value.Length < sizeof(uint))
        {
            throw new InvalidDataException("Expected a four-byte unsigned value.");
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(value);
    }

    internal static byte[] WriteSingle(double value)
    {
        if (!double.IsFinite(value) || value is < -1000 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        byte[] result = new byte[sizeof(float)];
        BinaryPrimitives.WriteSingleLittleEndian(result, checked((float)value));
        return result;
    }

    internal static string ReadString(ReadOnlySpan<byte> value) =>
        Encoding.UTF8.GetString(value).TrimEnd('\0');

    internal static byte[] WriteString(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 31 || value.Contains('\0'))
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        return Encoding.UTF8.GetBytes(value);
    }

    internal static string RevisionNumberToString(byte revision)
    {
        int number = revision;
        string result = string.Empty;
        do
        {
            result = (char)('A' + (number % 26)) + result;
            number = (number / 26) - 1;
        }
        while (number >= 0);

        return result;
    }
}
