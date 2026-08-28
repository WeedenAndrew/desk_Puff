using DeskPuff.Core.Devices;

namespace DeskPuff.App.Tests;

/// <summary>
/// Records every call the view model makes so a test can prove that selecting a
/// saved profile reaches no device path at all.
/// </summary>
internal sealed class FakeDeviceClient : IDeviceClient
{
    private IReadOnlyList<HeatProfile> profiles = DefaultProfiles();
    private readonly Queue<bool> refreshFailures = new();
    private int disconnectCallCount;
    private int refreshCallCount;

    public DeviceSnapshot Snapshot { get; private set; } = CreateSafeSnapshot();

    public event EventHandler<DeviceSnapshot>? SnapshotChanged;

    public int SelectProfileCallCount { get; private set; }

    public int UpdateProfileCallCount { get; private set; }

    public int StartCallCount { get; private set; }

    public int StopCallCount { get; private set; }

    public int BoostCallCount { get; private set; }

    public int DisconnectCallCount => Volatile.Read(ref disconnectCallCount);

    public int RefreshCallCount => Volatile.Read(ref refreshCallCount);

    public double LastBoostTemperatureCelsius { get; private set; }

    public TimeSpan LastBoostDuration { get; private set; }

    public int TotalStateChangingCalls =>
        SelectProfileCallCount +
        UpdateProfileCallCount +
        StartCallCount +
        StopCallCount +
        BoostCallCount;

    public void SetProfiles(IReadOnlyList<HeatProfile> updatedProfiles) => profiles = updatedProfiles;

    public void QueueRefreshFailures(params bool[] failures)
    {
        lock (refreshFailures)
        {
            foreach (bool failure in failures)
            {
                refreshFailures.Enqueue(failure);
            }
        }
    }

    /// <summary>Reports a chamber change, the way telemetry would.</summary>
    public void SetChamber(ChamberKind chamber)
    {
        Snapshot = Snapshot with { Chamber = chamber };
        SnapshotChanged?.Invoke(this, Snapshot);
    }

    /// <summary>Pushes the device into a running session, the way telemetry would.</summary>
    public void BeginHeating(double currentCelsius, TimeSpan total, TimeSpan elapsed)
    {
        Snapshot = Snapshot with
        {
            OperatingState = DeviceOperatingState.Active,
            CurrentTemperatureCelsius = currentCelsius,
            SessionTotal = total,
            SessionElapsed = elapsed,
        };
        SnapshotChanged?.Invoke(this, Snapshot);
    }

    public Task<IReadOnlyList<DeviceCandidate>> ScanAsync(
        TimeSpan duration,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DeviceCandidate>>([new DeviceCandidate("fake", "Fake Peak", -40)]);

    public Task ConnectAsync(DeviceCandidate candidate, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task DisconnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref disconnectCallCount);
        Snapshot = DeviceSnapshot.Disconnected;
        SnapshotChanged?.Invoke(this, Snapshot);
        return Task.CompletedTask;
    }

    public Task<DeviceSnapshot> RefreshAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref refreshCallCount);
        bool shouldFail;
        lock (refreshFailures)
        {
            shouldFail = refreshFailures.Count > 0 && refreshFailures.Dequeue();
        }

        return shouldFail
            ? Task.FromException<DeviceSnapshot>(new InvalidOperationException("Scripted transient refresh failure."))
            : Task.FromResult(Snapshot);
    }

    public Task<IReadOnlyList<HeatProfile>> GetProfilesAsync(CancellationToken cancellationToken) =>
        Task.FromResult(profiles);

    public Task SelectProfileAsync(int profileIndex, CancellationToken cancellationToken)
    {
        SelectProfileCallCount++;
        HeatProfile profile = profiles.Single(item => item.Index == profileIndex);
        Snapshot = Snapshot with
        {
            ActiveProfileIndex = profile.Index,
            ActiveProfileName = profile.Name,
            TargetTemperatureCelsius = profile.TargetTemperatureCelsius,
            SessionTotal = profile.Duration,
        };
        return Task.CompletedTask;
    }

    public Task UpdateProfileAsync(HeatProfile profile, CancellationToken cancellationToken)
    {
        UpdateProfileCallCount++;
        return Task.CompletedTask;
    }

    public Task StartSessionAsync(CancellationToken cancellationToken)
    {
        StartCallCount++;
        return Task.CompletedTask;
    }

    public Task StopSessionAsync(CancellationToken cancellationToken)
    {
        StopCallCount++;
        return Task.CompletedTask;
    }

    public Task BoostTemperatureAsync(double temperatureCelsius, CancellationToken cancellationToken)
    {
        BoostCallCount++;
        LastBoostTemperatureCelsius = temperatureCelsius;
        return Task.CompletedTask;
    }

    public Task BoostTimeAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        BoostCallCount++;
        LastBoostDuration = duration;
        return Task.CompletedTask;
    }

    public Task SetStealthModeAsync(bool enabled, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task SetLanternModeAsync(bool enabled, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    internal static IReadOnlyList<HeatProfile> DefaultProfiles() =>
    [
        NewProfile(0, "Classic", 220),
        NewProfile(1, "Balanced", 230),
        NewProfile(2, "Bold", 240),
        NewProfile(3, "Daily", 250),
    ];

    internal static DeviceSnapshot CreateSafeSnapshot() => new(
        DeviceConnectionState.ConnectedControlEnabled,
        new DeviceIdentity(DeviceFamily.PeakPro, "Fake Peak", 0, "verified", null),
        new DeviceLimits(190, 327, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(2), 20, TimeSpan.FromSeconds(30)),
        new DeviceCapabilities(true, true, true, true, 3),
        ChamberKind.ThreeDXL,
        DeviceOperatingState.Idle,
        0,
        "Classic",
        VaporLevel.Standard,
        80,
        false,
        220,
        25,
        TimeSpan.FromSeconds(40),
        TimeSpan.Zero,
        true,
        true,
        null);

    private static HeatProfile NewProfile(int index, string name, double temperatureCelsius) =>
        new(
            index,
            name,
            temperatureCelsius,
            TimeSpan.FromSeconds(40),
            VaporLevel.Standard,
            10,
            TimeSpan.FromSeconds(10),
            "#0000FF");
}
