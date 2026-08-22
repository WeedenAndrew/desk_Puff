using DeskPuff.Core.Devices;
using DeskPuff.Core.Safety;

namespace DeskPuff.Core.Sessions;

public sealed class SessionController(
    IDeviceClient client,
    DeviceSafetyPolicy safetyPolicy) : IAsyncDisposable
{
    private readonly SemaphoreSlim commandGate = new(1, 1);
    private int boostsApplied;
    private int disposeRequested;

    public DeviceSnapshot Snapshot => client.Snapshot;

    public event EventHandler<DeviceSnapshot>? SnapshotChanged
    {
        add => client.SnapshotChanged += value;
        remove => client.SnapshotChanged -= value;
    }

    public Task<IReadOnlyList<DeviceCandidate>> ScanAsync(
        TimeSpan duration,
        CancellationToken cancellationToken) =>
        client.ScanAsync(duration, cancellationToken);

    public Task ConnectAsync(DeviceCandidate candidate, CancellationToken cancellationToken) =>
        RunSerializedAsync(
            () => client.ConnectAsync(candidate, cancellationToken),
            cancellationToken);

    public Task DisconnectAsync(CancellationToken cancellationToken) =>
        RunSerializedAsync(
            () => client.DisconnectAsync(cancellationToken),
            cancellationToken);

    public Task<DeviceSnapshot> RefreshAsync(CancellationToken cancellationToken) =>
        RunSerializedAsync(
            () => client.RefreshAsync(cancellationToken),
            cancellationToken);

    public Task<IReadOnlyList<HeatProfile>> GetProfilesAsync(CancellationToken cancellationToken) =>
        RunSerializedAsync(
            () => client.GetProfilesAsync(cancellationToken),
            cancellationToken);

    public Task SelectProfileAsync(int profileIndex, CancellationToken cancellationToken) =>
        RunSerializedAsync(
            async () =>
            {
                safetyPolicy.ValidateProfileSelection(profileIndex, Snapshot).ThrowIfDenied();
                await client.SelectProfileAsync(profileIndex, cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);

    public Task UpdateProfileAsync(HeatProfile profile, CancellationToken cancellationToken)
        => RunSerializedAsync(
            async () =>
            {
                safetyPolicy.ValidateProfile(profile, Snapshot).ThrowIfDenied();
                await client.UpdateProfileAsync(profile, cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);

    public Task StartAsync(CancellationToken cancellationToken) =>
        RunCommandAsync(
            DeviceAction.StartSession,
            async () =>
            {
                await client.StartSessionAsync(cancellationToken).ConfigureAwait(false);
                boostsApplied = 0;
            },
            cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) =>
        RunCommandAsync(
            DeviceAction.StopSession,
            async () =>
            {
                await client.StopSessionAsync(cancellationToken).ConfigureAwait(false);
                boostsApplied = 0;
            },
            cancellationToken);

    public Task BoostTemperatureAsync(
        double temperatureCelsius,
        CancellationToken cancellationToken) =>
        RunBoostAsync(
            () => safetyPolicy.ValidateTemperatureBoost(
                Snapshot,
                boostsApplied,
                temperatureCelsius),
            () => client.BoostTemperatureAsync(temperatureCelsius, cancellationToken),
            cancellationToken);

    public Task BoostTimeAsync(TimeSpan duration, CancellationToken cancellationToken) =>
        RunBoostAsync(
            () => safetyPolicy.ValidateTimeBoost(Snapshot, boostsApplied, duration),
            () => client.BoostTimeAsync(duration, cancellationToken),
            cancellationToken);

    public Task SetStealthModeAsync(bool enabled, CancellationToken cancellationToken) =>
        RunCommandAsync(
            DeviceAction.SetStealthMode,
            () => client.SetStealthModeAsync(enabled, cancellationToken),
            cancellationToken);

    public Task SetLanternModeAsync(bool enabled, CancellationToken cancellationToken) =>
        RunCommandAsync(
            DeviceAction.SetLanternMode,
            () => client.SetLanternModeAsync(enabled, cancellationToken),
            cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposeRequested, 1) != 0)
        {
            return;
        }

        await commandGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            commandGate.Release();
            commandGate.Dispose();
        }
    }

    private Task RunCommandAsync(
        DeviceAction action,
        Func<Task> operation,
        CancellationToken cancellationToken) =>
        RunSerializedAsync(
            async () =>
            {
                safetyPolicy.Evaluate(action, Snapshot).ThrowIfDenied();
                await operation().ConfigureAwait(false);
            },
            cancellationToken);

    private Task RunBoostAsync(
        Func<SafetyDecision> validate,
        Func<Task> operation,
        CancellationToken cancellationToken) =>
        RunSerializedAsync(
            async () =>
            {
                validate().ThrowIfDenied();
                await operation().ConfigureAwait(false);
                boostsApplied++;
            },
            cancellationToken);

    private async Task RunSerializedAsync(
        Func<Task> operation,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposeRequested) != 0, this);
        await commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposeRequested) != 0, this);
            // Deliberately no retry: state-changing BLE commands are at-most-once.
            await operation().ConfigureAwait(false);
        }
        finally
        {
            commandGate.Release();
        }
    }

    private async Task<T> RunSerializedAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposeRequested) != 0, this);
        await commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposeRequested) != 0, this);
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            commandGate.Release();
        }
    }
}
