using DeskPuff.Bluetooth.Windows.Protocol;
using DeskPuff.Core.Devices;

namespace DeskPuff.Bluetooth.Windows.Transport;

internal interface ILoraxTransport : IAsyncDisposable
{
    bool IsConnected { get; }

    string AdvertisedName { get; }

    Task<IReadOnlyList<DeviceCandidate>> ScanAsync(
        TimeSpan duration,
        CancellationToken cancellationToken);

    Task ConnectAsync(DeviceCandidate candidate, CancellationToken cancellationToken);

    Task DisconnectAsync(CancellationToken cancellationToken);

    Task TriggerBondingAsync(CancellationToken cancellationToken);

    Task<ReadOnlyMemory<byte>> RunCommandAsync(
        LoraxOpcode opcode,
        ReadOnlyMemory<byte> body,
        int maximumReplyLength,
        CancellationToken cancellationToken);
}
