using DeskPuff.Core.Devices;
using DeskPuff.Core.Diagnostics;
using DeskPuff.Core.Safety;

namespace DeskPuff.Core.Sessions;

public sealed class SessionController(
    IDeviceClient client,
    DeviceSafetyPolicy safetyPolicy,
    IDiagnosticLog? diagnostics = null,
    ISessionOverrideClient? sessionOverrides = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim commandGate = new(1, 1);
    private readonly IDiagnosticLog diagnosticLog = diagnostics ?? NullDiagnosticLog.Instance;
    private readonly ISessionOverrideClient? overrideClient = sessionOverrides ?? client as ISessionOverrideClient;
    // Independent app-call counters loosen the total allowance from four to six (three each).
    // At +5 F / 10 s device steps, both cumulative ceilings bind within three steps,
    // making the count a backstop. This holds only while those step sizes reach the
    // ceilings within three steps; smaller configured steps make the count bind first.
    private int temperatureBoostsApplied;
    private int timeBoostsApplied;
    private int disposeRequested;
    private bool sessionOverridePendingClear;
    private bool sessionOverrideHeatingObserved;
    private bool sessionOverrideCleanupFailed;

    public DeviceSnapshot Snapshot => client.Snapshot;

    public bool SupportsSessionOverrides => overrideClient is not null;

    public Task ApplySessionOverrideAsync(SessionOverride request, CancellationToken cancellationToken) =>
        RunSerializedAsync(
            // The client's complete transaction, including rollback, stays under this
            // one hold. Never release and reacquire the gate to perform rollback.
            async () =>
            {
                await RequireOverrideClient("apply").ApplySessionOverrideAsync(request, cancellationToken).ConfigureAwait(false);
                sessionOverridePendingClear = true;
                sessionOverrideHeatingObserved = false;
                sessionOverrideCleanupFailed = false;
            },
            cancellationToken);

    public Task ClearSessionOverrideAsync(CancellationToken cancellationToken) =>
        RunSerializedAsync(
            () => ClearSessionOverrideCoreAsync(cancellationToken),
            cancellationToken);

    private async Task ClearSessionOverrideCoreAsync(CancellationToken cancellationToken)
    {
        ISessionOverrideClient overrides = RequireOverrideClient("clear");
        // Consume the automatic cleanup before the write: an absent reply must not
        // make the next telemetry poll blindly repeat a state-changing command.
        sessionOverridePendingClear = false;
        sessionOverrideHeatingObserved = false;
        sessionOverrideCleanupFailed = true;
        await overrides.ClearSessionOverrideAsync(cancellationToken).ConfigureAwait(false);
        sessionOverrideCleanupFailed = false;
    }

    private void ResetSessionOverrideTracking()
    {
        sessionOverridePendingClear = false;
        sessionOverrideHeatingObserved = false;
        sessionOverrideCleanupFailed = false;
    }

    private ISessionOverrideClient RequireOverrideClient(string operation)
    {
        if (overrideClient is not null) { return overrideClient; }
        const string reason = "The device client does not support session overrides.";
        diagnosticLog.Write($"SESSION OVERRIDE operation={operation} result=unsupported reason=\"{reason}\"");
        throw new DeviceSafetyException(reason);
    }

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
            async () =>
            {
                await client.ConnectAsync(candidate, cancellationToken).ConfigureAwait(false);
                ResetSessionOverrideTracking();
            },
            cancellationToken);

    public Task DisconnectAsync(CancellationToken cancellationToken) =>
        RunSerializedAsync(
            async () =>
            {
                await client.DisconnectAsync(cancellationToken).ConfigureAwait(false);
                ResetSessionOverrideTracking();
            },
            cancellationToken);

    public Task<DeviceSnapshot> RefreshAsync(CancellationToken cancellationToken) =>
        RunSerializedAsync(
            async () =>
            {
                DeviceSnapshot previous = Snapshot;
                DeviceSnapshot current = await client.RefreshAsync(cancellationToken).ConfigureAwait(false);
                if (sessionOverridePendingClear)
                {
                    sessionOverrideHeatingObserved |= previous.IsHeating || current.IsHeating;
                    if (sessionOverrideHeatingObserved && current.OperatingState == DeviceOperatingState.Idle)
                    {
                        // Firmware retains THC parameters at natural completion. This
                        // verified reload stays under the same command gate as refresh.
                        diagnosticLog.Write("SESSION OVERRIDE session-ended cleanup=attempt");
                        await ClearSessionOverrideCoreAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
                return Snapshot;
            },
            cancellationToken);

    public Task<IReadOnlyList<HeatProfile>> GetProfilesAsync(CancellationToken cancellationToken) =>
        RunSerializedAsync(
            () => client.GetProfilesAsync(cancellationToken),
            cancellationToken);

    public Task SelectProfileAsync(int profileIndex, CancellationToken cancellationToken) =>
        RunSerializedAsync(
            async () =>
            {
                EnsureAllowed(
                    DeviceAction.SelectProfile,
                    safetyPolicy.ValidateProfileSelection(profileIndex, Snapshot));
                await client.SelectProfileAsync(profileIndex, cancellationToken).ConfigureAwait(false);
                ResetSessionOverrideTracking();
            },
            cancellationToken);

    public Task UpdateProfileAsync(HeatProfile profile, CancellationToken cancellationToken)
        => RunSerializedAsync(
            async () =>
            {
                EnsureAllowed(DeviceAction.UpdateProfile, safetyPolicy.ValidateProfile(profile, Snapshot));
                await client.UpdateProfileAsync(profile, cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);

    public Task StartAsync(CancellationToken cancellationToken) =>
        RunCommandAsync(
            DeviceAction.StartSession,
            async () =>
            {
                EnsureAllowed(DeviceAction.StartSession, sessionOverrideCleanupFailed
                    ? SafetyDecision.Deny("Restoring the saved session's device profile was not confirmed. Reselect a device profile before starting.")
                    : SafetyDecision.Allow());
                await client.StartSessionAsync(cancellationToken).ConfigureAwait(false);
                temperatureBoostsApplied = 0;
                timeBoostsApplied = 0;
            },
            cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) =>
        RunCommandAsync(
            DeviceAction.StopSession,
            async () =>
            {
                await client.StopSessionAsync(cancellationToken).ConfigureAwait(false);
                temperatureBoostsApplied = 0;
                timeBoostsApplied = 0;
            },
            cancellationToken);

    // These preflight checks omit accumulated amounts. LoraxDeviceClient.ApplyBoostAsync
    // owns cumulative validation using freshly read overrides before each write.
    public Task BoostTemperatureAsync(
        double temperatureCelsius,
        CancellationToken cancellationToken) =>
        RunBoostAsync(
            DeviceAction.BoostTemperature,
            () => safetyPolicy.ValidateTemperatureBoost(
                Snapshot,
                temperatureBoostsApplied,
                temperatureCelsius),
            () => client.BoostTemperatureAsync(temperatureCelsius, cancellationToken),
            cancellationToken);

    public Task BoostTimeAsync(TimeSpan duration, CancellationToken cancellationToken) =>
        RunBoostAsync(
            DeviceAction.BoostTime,
            () => safetyPolicy.ValidateTimeBoost(Snapshot, timeBoostsApplied, duration),
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
                EnsureAllowed(action, safetyPolicy.Evaluate(action, Snapshot));
                await operation().ConfigureAwait(false);
            },
            cancellationToken);

    private Task RunBoostAsync(
        DeviceAction action,
        Func<SafetyDecision> validate,
        Func<Task> operation,
        CancellationToken cancellationToken) =>
        RunSerializedAsync(
            async () =>
            {
                EnsureAllowed(action, validate());
                await operation().ConfigureAwait(false);
                if (action == DeviceAction.BoostTemperature) { temperatureBoostsApplied++; }
                else { timeBoostsApplied++; }
            },
            cancellationToken);

    private void EnsureAllowed(DeviceAction action, SafetyDecision decision)
    {
        if (!decision.IsAllowed)
        {
            diagnosticLog.Write(
                $"WRITE BLOCKED action={action} reason=\"{decision.Reason}\"");
        }

        decision.ThrowIfDenied();
    }

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
