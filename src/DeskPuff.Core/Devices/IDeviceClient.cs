namespace DeskPuff.Core.Devices;

public interface IDeviceClient : IAsyncDisposable
{
    DeviceSnapshot Snapshot { get; }

    event EventHandler<DeviceSnapshot>? SnapshotChanged;

    Task<IReadOnlyList<DeviceCandidate>> ScanAsync(
        TimeSpan duration,
        CancellationToken cancellationToken);

    Task ConnectAsync(DeviceCandidate candidate, CancellationToken cancellationToken);

    Task DisconnectAsync(CancellationToken cancellationToken);

    Task<DeviceSnapshot> RefreshAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<HeatProfile>> GetProfilesAsync(CancellationToken cancellationToken);

    Task SelectProfileAsync(int profileIndex, CancellationToken cancellationToken);

    Task UpdateProfileAsync(HeatProfile profile, CancellationToken cancellationToken);

    Task StartSessionAsync(CancellationToken cancellationToken);

    Task StopSessionAsync(CancellationToken cancellationToken);

    Task BoostTemperatureAsync(double temperatureCelsius, CancellationToken cancellationToken);

    Task BoostTimeAsync(TimeSpan duration, CancellationToken cancellationToken);

    Task SetStealthModeAsync(bool enabled, CancellationToken cancellationToken);

    Task SetLanternModeAsync(bool enabled, CancellationToken cancellationToken);
}
