using DeskPuff.Core.Devices;

namespace DeskPuff.App.Devices;

internal sealed class DemoDeviceClient : IDeviceClient, ISessionOverrideClient
{
    private readonly List<HeatProfile> profiles =
    [
        new(0, "BLUE", 260, TimeSpan.FromSeconds(40), VaporLevel.Standard, 5, TimeSpan.FromSeconds(10), "#0000FF")
        {
            ColorPalette = ["#0000FF", "#2878FF", "#39DCE2"],
            HasDeviceColor = true,
        },
        new(1, "GREEN", 271, TimeSpan.FromSeconds(45), VaporLevel.High, 5, TimeSpan.FromSeconds(10), "#6EE916")
        {
            ColorPalette = ["#6EE916", "#C6FF62"],
            HasDeviceColor = true,
        },
        new(2, "RED", 282, TimeSpan.FromSeconds(50), VaporLevel.Max, 5, TimeSpan.FromSeconds(10), "#F80B00")
        {
            ColorPalette = ["#F80B00", "#FF7A18"],
            HasDeviceColor = true,
        },
        new(3, "WHITE", 293, TimeSpan.FromSeconds(55), VaporLevel.XL, 5, TimeSpan.FromSeconds(10), "#FFFFFF")
        {
            ColorPalette = ["#FFFFFF", "#B9D7FF"],
            HasDeviceColor = true,
        },
    ];

    private readonly object stateGate = new();
    private DateTimeOffset? heatStartedAt;
    private DeviceCandidate? connectedCandidate;
    private double sessionBoostCelsius;
    private TimeSpan sessionTimeBoost;
    private SessionOverride? sessionOverride;
    private bool disposed;

    public DeviceSnapshot Snapshot { get; private set; } = DeviceSnapshot.Disconnected;

    public event EventHandler<DeviceSnapshot>? SnapshotChanged;

    public Task<IReadOnlyList<DeviceCandidate>> ScanAsync(
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        IReadOnlyList<DeviceCandidate> candidates =
        [
            new DeviceCandidate("demo-primary", "desk_Puff Demo Peak Pro", -42),
            new DeviceCandidate("demo-backup", "desk_Puff Demo Peak Pro Backup", -55),
        ];
        return Task.FromResult(candidates);
    }

    public Task ConnectAsync(DeviceCandidate candidate, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        lock (stateGate)
        {
            connectedCandidate = candidate;
            Snapshot = CreateConnectedSnapshot(0);
        }

        RaiseSnapshotChanged();
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (stateGate)
        {
            heatStartedAt = null;
            connectedCandidate = null;
            Snapshot = DeviceSnapshot.Disconnected;
        }

        RaiseSnapshotChanged();
        return Task.CompletedTask;
    }

    public Task<DeviceSnapshot> RefreshAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (stateGate)
        {
            if (heatStartedAt is { } startedAt)
            {
                UpdateHeatState(startedAt);
            }
        }

        RaiseSnapshotChanged();
        return Task.FromResult(Snapshot);
    }

    public Task<IReadOnlyList<HeatProfile>> GetProfilesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (stateGate)
        {
            return Task.FromResult<IReadOnlyList<HeatProfile>>(profiles.ToArray());
        }
    }

    public Task SelectProfileAsync(int profileIndex, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (stateGate)
        {
            ThrowIfHeating();
            sessionOverride = null;
            HeatProfile profile = profiles.Single(item => item.Index == profileIndex);
            Snapshot = Snapshot with
            {
                ActiveProfileIndex = profile.Index,
                ActiveProfileName = profile.Name,
                Vapor = profile.Vapor,
                TargetTemperatureCelsius = profile.TargetTemperatureCelsius,
                SessionTotal = profile.Duration,
            };
        }

        RaiseSnapshotChanged();
        return Task.CompletedTask;
    }

    public Task UpdateProfileAsync(HeatProfile profile, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (stateGate)
        {
            ThrowIfHeating();
            profiles[profile.Index] = profile;
            if (Snapshot.ActiveProfileIndex == profile.Index)
            {
                Snapshot = Snapshot with
                {
                    ActiveProfileName = profile.Name,
                    Vapor = profile.Vapor,
                    TargetTemperatureCelsius = profile.TargetTemperatureCelsius,
                    SessionTotal = profile.Duration,
                };
            }
        }

        RaiseSnapshotChanged();
        return Task.CompletedTask;
    }

    public Task ApplySessionOverrideAsync(
        SessionOverride requested,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        lock (stateGate)
        {
            if (Snapshot.OperatingState != DeviceOperatingState.Idle)
            {
                throw new InvalidOperationException(
                    "Session parameters can only be applied before a session starts.");
            }

            sessionOverride = requested;
            Snapshot = Snapshot with
            {
                ActiveProfileName = requested.Name,
                TargetTemperatureCelsius = requested.TargetTemperatureCelsius,
                SessionTotal = requested.Duration,
                Vapor = requested.Vapor,
            };
        }

        RaiseSnapshotChanged();
        return Task.CompletedTask;
    }

    public Task ClearSessionOverrideAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (stateGate)
        {
            if (sessionOverride is null)
            {
                return Task.CompletedTask;
            }

            sessionOverride = null;
            RestoreSlotSnapshot();
        }

        RaiseSnapshotChanged();
        return Task.CompletedTask;
    }

    public Task StartSessionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (stateGate)
        {
            if (Snapshot.OperatingState != DeviceOperatingState.Idle)
            {
                throw new InvalidOperationException("The demo device is not idle.");
            }

            heatStartedAt = DateTimeOffset.UtcNow;
            sessionBoostCelsius = 0;
            sessionTimeBoost = TimeSpan.Zero;
            Snapshot = Snapshot with
            {
                OperatingState = DeviceOperatingState.Preheating,
                SessionElapsed = TimeSpan.Zero,
            };
        }

        RaiseSnapshotChanged();
        return Task.CompletedTask;
    }

    public Task StopSessionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (stateGate)
        {
            heatStartedAt = null;
            Snapshot = Snapshot with
            {
                OperatingState = DeviceOperatingState.Idle,
                CurrentTemperatureCelsius = 28,
                SessionElapsed = TimeSpan.Zero,
            };
        }

        RaiseSnapshotChanged();
        return Task.CompletedTask;
    }

    public Task BoostTemperatureAsync(
        double temperatureCelsius,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (stateGate)
        {
            if (Snapshot.OperatingState != DeviceOperatingState.Active)
            {
                throw new InvalidOperationException("Boost requires an active session.");
            }

            sessionBoostCelsius += temperatureCelsius;
            Snapshot = Snapshot with
            {
                TargetTemperatureCelsius = (sessionOverride?.TargetTemperatureCelsius ??
                    profiles[Snapshot.ActiveProfileIndex].TargetTemperatureCelsius) + sessionBoostCelsius,
            };
        }

        RaiseSnapshotChanged();
        return Task.CompletedTask;
    }

    public Task BoostTimeAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (stateGate)
        {
            if (Snapshot.OperatingState != DeviceOperatingState.Active)
            {
                throw new InvalidOperationException("Boost requires an active session.");
            }

            sessionTimeBoost += duration;
            Snapshot = Snapshot with
            {
                SessionTotal = (sessionOverride?.Duration ??
                    profiles[Snapshot.ActiveProfileIndex].Duration) + sessionTimeBoost,
            };
        }

        RaiseSnapshotChanged();
        return Task.CompletedTask;
    }

    public Task SetStealthModeAsync(bool enabled, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task SetLanternModeAsync(bool enabled, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        disposed = true;
        return ValueTask.CompletedTask;
    }

    private DeviceSnapshot CreateConnectedSnapshot(int profileIndex)
    {
        HeatProfile profile = profiles[profileIndex];
        return new DeviceSnapshot(
            DeviceConnectionState.ConnectedControlEnabled,
            new DeviceIdentity(
                DeviceFamily.PeakPro,
                connectedCandidate?.PlatformId == "demo-backup" ? "BACKUP PEAK" : "DESK PEAK",
                0,
                "DEMO",
                null),
            new DeviceLimits(180, 340, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(3), 20, TimeSpan.FromSeconds(30)),
            new DeviceCapabilities(true, true, true, true, 4),
            ChamberKind.ThreeDXL,
            DeviceOperatingState.Idle,
            profile.Index,
            profile.Name,
            profile.Vapor,
            84,
            false,
            profile.TargetTemperatureCelsius,
            28,
            profile.Duration,
            TimeSpan.Zero,
            true,
            true,
            null);
    }

    private void UpdateHeatState(DateTimeOffset startedAt)
    {
        TimeSpan sinceStart = DateTimeOffset.UtcNow - startedAt;
        HeatProfile profile = profiles[Snapshot.ActiveProfileIndex];
        double baseTemperature = sessionOverride?.TargetTemperatureCelsius ??
            profile.TargetTemperatureCelsius;
        TimeSpan baseDuration = sessionOverride?.Duration ?? profile.Duration;
        TimeSpan preheat = TimeSpan.FromSeconds(4);
        TimeSpan activeElapsed = sinceStart - preheat;
        TimeSpan sessionTotal = baseDuration + sessionTimeBoost;
        double ambient = 28;
        double target = baseTemperature + sessionBoostCelsius;

        if (sinceStart < preheat)
        {
            double progress = Math.Clamp(sinceStart.TotalSeconds / preheat.TotalSeconds, 0, 1);
            Snapshot = Snapshot with
            {
                OperatingState = DeviceOperatingState.Preheating,
                CurrentTemperatureCelsius = ambient + ((target - ambient) * progress),
                TargetTemperatureCelsius = target,
                SessionTotal = sessionTotal,
                SessionElapsed = TimeSpan.Zero,
            };
            return;
        }

        if (activeElapsed < sessionTotal)
        {
            Snapshot = Snapshot with
            {
                OperatingState = DeviceOperatingState.Active,
                CurrentTemperatureCelsius = target - (Math.Sin(activeElapsed.TotalSeconds * 2) * 1.2),
                TargetTemperatureCelsius = target,
                SessionTotal = sessionTotal,
                SessionElapsed = activeElapsed,
            };
            return;
        }

        heatStartedAt = null;
        sessionOverride = null;
        Snapshot = Snapshot with
        {
            OperatingState = DeviceOperatingState.Idle,
            CurrentTemperatureCelsius = 45,
            ActiveProfileName = profile.Name,
            TargetTemperatureCelsius = profile.TargetTemperatureCelsius,
            SessionTotal = profile.Duration,
            Vapor = profile.Vapor,
            SessionElapsed = TimeSpan.Zero,
        };
    }

    private void RestoreSlotSnapshot()
    {
        HeatProfile profile = profiles[Snapshot.ActiveProfileIndex];
        Snapshot = Snapshot with
        {
            ActiveProfileName = profile.Name,
            TargetTemperatureCelsius = profile.TargetTemperatureCelsius,
            SessionTotal = profile.Duration,
            Vapor = profile.Vapor,
        };
    }

    private void ThrowIfHeating()
    {
        if (Snapshot.IsHeating)
        {
            throw new InvalidOperationException("Profiles cannot change during a heat cycle.");
        }
    }

    private void RaiseSnapshotChanged() => SnapshotChanged?.Invoke(this, Snapshot);
}
