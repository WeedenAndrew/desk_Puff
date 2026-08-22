using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DeskPuff.Bluetooth.Windows.Protocol;

internal enum LoraxOpcode : byte
{
    GetAccessSeed = 0x00,
    UnlockAccess = 0x01,
    GetLimits = 0x02,
    ReadShort = 0x10,
    WriteShort = 0x11,
}

internal enum DeviceModeCommand : byte
{
    StartHeatCycle = 7,
    AbortHeatCycle = 8,
}

internal readonly record struct LoraxReply(
    ushort Sequence,
    byte HeaderByte,
    ReadOnlyMemory<byte> Payload);

internal static class LoraxProtocol
{
    internal static readonly Guid ServiceId = new("e276967f-ea8a-478a-a92e-d78f5dd15dd5");
    internal static readonly Guid VersionCharacteristicId = new("05434bca-cc7f-4ef6-bbb3-b1c520b9800c");
    internal static readonly Guid CommandCharacteristicId = new("60133d5c-5727-4f2c-9697-d842c5292a3c");
    internal static readonly Guid ReplyCharacteristicId = new("8dc5ec05-8f7d-45ad-99db-3fbde65dbd9c");

    private static readonly byte[] HandshakeKey = Convert.FromBase64String(
        "ZMZFYlbyb1scoSc3pd1x+w==");

    internal static byte[] BuildFrame(ushort sequence, LoraxOpcode opcode, ReadOnlySpan<byte> body)
    {
        if (body.Length > 512)
        {
            throw new ArgumentOutOfRangeException(nameof(body), "Lorax command bodies are limited to 512 bytes.");
        }

        byte[] frame = new byte[3 + body.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(frame, sequence);
        frame[2] = (byte)opcode;
        body.CopyTo(frame.AsSpan(3));
        return frame;
    }

    internal static LoraxReply ParseReply(ReadOnlyMemory<byte> frame)
    {
        if (frame.Length < 3)
        {
            throw new InvalidDataException("Lorax reply is shorter than its header.");
        }

        ReadOnlySpan<byte> span = frame.Span;
        ushort sequence = BinaryPrimitives.ReadUInt16LittleEndian(span);
        return new LoraxReply(sequence, span[2], frame[3..]);
    }

    internal static byte[] BuildReadBody(string path, ushort offset, ushort size)
    {
        ValidatePath(path);
        byte[] pathBytes = Encoding.UTF8.GetBytes(path);
        byte[] body = new byte[4 + pathBytes.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(body, offset);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2), size);
        pathBytes.CopyTo(body.AsSpan(4));
        return body;
    }

    internal static byte[] BuildWriteBody(
        string path,
        ushort offset,
        byte flags,
        ReadOnlySpan<byte> value)
    {
        ValidatePath(path);
        if (!LoraxPaths.IsWriteAllowed(path))
        {
            throw new DeviceWriteBlockedException($"The Lorax path '{path}' is not allowlisted for writes.");
        }

        if (value.Length is 0 or > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Lorax writes must contain 1 to 128 bytes.");
        }

        byte[] pathBytes = Encoding.UTF8.GetBytes(path);
        byte[] body = new byte[4 + pathBytes.Length + value.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(body, offset);
        body[2] = flags;
        pathBytes.CopyTo(body.AsSpan(3));
        body[3 + pathBytes.Length] = 0;
        value.CopyTo(body.AsSpan(4 + pathBytes.Length));
        return body;
    }

    internal static byte[] DeriveUnlockKey(ReadOnlySpan<byte> accessSeed)
    {
        if (accessSeed.Length != 16)
        {
            throw new ArgumentException("The Lorax access seed must contain exactly 16 bytes.", nameof(accessSeed));
        }

        Span<byte> input = stackalloc byte[32];
        HandshakeKey.CopyTo(input);
        accessSeed.CopyTo(input[16..]);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);
        return hash[..16].ToArray();
    }

    private static void ValidatePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Length > 63 || path[0] != '/' || path.Contains('\0'))
        {
            throw new ArgumentException("Lorax path is invalid.", nameof(path));
        }
    }
}

internal sealed class DeviceWriteBlockedException(string message) : InvalidOperationException(message);
