using DeskPuff.Core.Devices;

namespace DeskPuff.Bluetooth.Windows.Protocol;

internal static class RuntimeVaporCodec
{
    internal static SessionVaporMode? Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 4) { return null; }
        foreach (SessionVaporMode mode in Enum.GetValues<SessionVaporMode>())
        {
            if (bytes.SequenceEqual(Encode(mode))) { return mode; }
        }
        return null;
    }

    internal static byte[] Encode(SessionVaporMode mode) => LoraxValueCodec.WriteSingle(mode switch
    {
        SessionVaporMode.Smooth => 0.0,
        SessionVaporMode.Bold => 0.5,
        SessionVaporMode.Intense => 1.0,
        SessionVaporMode.Extreme => 1.5,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    });
}
