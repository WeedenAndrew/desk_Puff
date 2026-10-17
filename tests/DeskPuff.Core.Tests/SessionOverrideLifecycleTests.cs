using DeskPuff.Core.Devices;
using DeskPuff.Core.Safety;
using DeskPuff.Core.Sessions;

namespace DeskPuff.Core.Tests;

[TestClass]
public sealed class SessionOverrideLifecycleTests
{
    [TestMethod]
    public async Task NaturalSessionEnd_ClearsAppliedOverrideOnceAfterHeatingWasObserved()
    {
        LifecycleClient client = new();
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await controller.ApplySessionOverrideAsync(Request(), CancellationToken.None);
        await controller.RefreshAsync(CancellationToken.None);
        Assert.AreEqual(0, client.ClearCalls, "An idle poll before ignition must not discard the requested session.");

        client.State = DeviceOperatingState.Active;
        await controller.RefreshAsync(CancellationToken.None);
        client.State = DeviceOperatingState.Idle;
        await controller.RefreshAsync(CancellationToken.None);
        await controller.RefreshAsync(CancellationToken.None);

        Assert.AreEqual(1, client.ClearCalls, "The device retains THC parameters after natural completion until hcs reload.");
    }

    [TestMethod]
    public async Task DeviceSlotSession_DoesNotIssueOverrideCleanup()
    {
        LifecycleClient client = new() { State = DeviceOperatingState.Active };
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await controller.RefreshAsync(CancellationToken.None);
        client.State = DeviceOperatingState.Idle;
        await controller.RefreshAsync(CancellationToken.None);
        Assert.AreEqual(0, client.ClearCalls);
    }

    [TestMethod]
    public async Task ExplicitClear_PreventsDuplicateCleanupAtNaturalEnd()
    {
        LifecycleClient client = new();
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await controller.ApplySessionOverrideAsync(Request(), CancellationToken.None);
        client.State = DeviceOperatingState.Active;
        await controller.RefreshAsync(CancellationToken.None);
        client.State = DeviceOperatingState.Idle;
        await controller.ClearSessionOverrideAsync(CancellationToken.None);
        await controller.RefreshAsync(CancellationToken.None);
        Assert.AreEqual(1, client.ClearCalls);
        await controller.StartAsync(CancellationToken.None);
        Assert.AreEqual(1, client.StartCalls, "A confirmed clear must leave normal starts available.");
    }

    [TestMethod]
    public async Task AutomaticCleanup_HoldsCommandGateUntilReloadCompletes()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        LifecycleClient client = new() { ClearPause = release.Task };
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await controller.ApplySessionOverrideAsync(Request(), CancellationToken.None);
        client.State = DeviceOperatingState.Active;
        await controller.RefreshAsync(CancellationToken.None);
        client.State = DeviceOperatingState.Idle;
        Task refresh = controller.RefreshAsync(CancellationToken.None);
        Task select = controller.SelectProfileAsync(1, CancellationToken.None);
        try
        {
            Assert.AreEqual(1, client.ClearCalls);
            Assert.IsFalse(refresh.IsCompleted);
            Assert.IsFalse(select.IsCompleted);
            Assert.AreEqual(0, client.SelectCalls);
        }
        finally { release.SetResult(); }
        await Task.WhenAll(refresh, select);
        Assert.AreEqual(1, client.SelectCalls);
    }

    [TestMethod]
    public async Task FailedAutomaticCleanup_IsReportedAndNeverBlindlyRetriedByPolling()
    {
        IOException failure = new("hcs reply lost; outcome unknown");
        LifecycleClient client = new() { ClearFailure = failure };
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await controller.ApplySessionOverrideAsync(Request(), CancellationToken.None);
        client.State = DeviceOperatingState.Active;
        await controller.RefreshAsync(CancellationToken.None);
        client.State = DeviceOperatingState.Idle;
        Assert.AreSame(failure, await Assert.ThrowsExactlyAsync<IOException>(() => controller.RefreshAsync(CancellationToken.None)));
        await controller.RefreshAsync(CancellationToken.None);
        Assert.AreEqual(1, client.ClearCalls);
        await Assert.ThrowsExactlyAsync<DeviceSafetyException>(() => controller.StartAsync(CancellationToken.None));
        Assert.AreEqual(0, client.StartCalls, "Unknown cleanup must not let a later app start reuse stale parameters.");
        await controller.SelectProfileAsync(0, CancellationToken.None);
        await controller.StartAsync(CancellationToken.None);
        Assert.AreEqual(1, client.StartCalls, "A verified explicit slot selection recovers the session.");
    }

    private static SessionOverride Request() => new("Saved", 220, TimeSpan.FromSeconds(60), VaporLevel.Standard, ["#112233"]);

    private sealed class LifecycleClient : IDeviceClient, ISessionOverrideClient
    {
        public DeviceOperatingState State { get; set; } = DeviceOperatingState.Idle;
        public int ClearCalls { get; private set; }
        public int StartCalls { get; private set; }
        public int SelectCalls { get; private set; }
        public IOException? ClearFailure { get; init; }
        public Task? ClearPause { get; init; }

        public DeviceSnapshot Snapshot => DeviceSnapshot.Disconnected with
        {
            ConnectionState = DeviceConnectionState.ConnectedControlEnabled,
            Identity = new(DeviceFamily.PeakPro, "Test", 13, "AN", null),
            Limits = new(210, 300, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(120), 5, TimeSpan.FromSeconds(30)),
            Chamber = ChamberKind.ThreeDXL,
            IsAuthenticated = true,
            IsFirmwareVerified = true,
            OperatingState = State,
            CurrentTemperatureCelsius = 25,
        };

        public event EventHandler<DeviceSnapshot>? SnapshotChanged { add { } remove { } }

        public Task ApplySessionOverrideAsync(SessionOverride request, CancellationToken cancellationToken) => Task.CompletedTask;
        public async Task ClearSessionOverrideAsync(CancellationToken cancellationToken)
        {
            ClearCalls++;
            if (ClearPause is not null) { await ClearPause.WaitAsync(cancellationToken); }
            if (ClearFailure is not null) { throw ClearFailure; }
        }
        public Task<DeviceSnapshot> RefreshAsync(CancellationToken cancellationToken) => Task.FromResult(Snapshot);
        public Task<IReadOnlyList<DeviceCandidate>> ScanAsync(TimeSpan duration, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DeviceCandidate>>([]);
        public Task ConnectAsync(DeviceCandidate candidate, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<HeatProfile>> GetProfilesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<HeatProfile>>([]);
        public Task SelectProfileAsync(int profileIndex, CancellationToken cancellationToken)
        {
            SelectCalls++;
            return Task.CompletedTask;
        }
        public Task UpdateProfileAsync(HeatProfile profile, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StartSessionAsync(CancellationToken cancellationToken)
        {
            StartCalls++;
            return Task.CompletedTask;
        }
        public Task StopSessionAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task BoostTemperatureAsync(double temperatureCelsius, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task BoostTimeAsync(TimeSpan duration, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetStealthModeAsync(bool enabled, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetLanternModeAsync(bool enabled, CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
