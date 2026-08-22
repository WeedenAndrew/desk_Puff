using DeskPuff.Core.Devices;
using DeskPuff.Core.Safety;
using DeskPuff.Core.Sessions;

namespace DeskPuff.Core.Tests;

[TestClass]
public sealed class SessionControllerTests
{
    [TestMethod]
    public async Task StateChangingCommand_IsNeverRetried()
    {
        FakeDeviceClient client = new() { StartException = new IOException("transport failed") };
        await using SessionController controller = new(client, new DeviceSafetyPolicy());

        await Assert.ThrowsExactlyAsync<IOException>(() => controller.StartAsync(CancellationToken.None));

        Assert.AreEqual(1, client.StartCallCount);
    }

    [TestMethod]
    public async Task ConcurrentStateChanges_AreSerialized()
    {
        FakeDeviceClient client = new() { DelayCommands = true };
        await using SessionController controller = new(client, new DeviceSafetyPolicy());

        Task first = controller.StartAsync(CancellationToken.None);
        await client.FirstCommandEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Task second = controller.SelectProfileAsync(1, CancellationToken.None);
        await Task.Delay(50);

        Assert.AreEqual(1, client.MaximumConcurrentCommands);
        client.ReleaseCommands.TrySetResult();
        await Task.WhenAll(first, second);
        Assert.AreEqual(1, client.MaximumConcurrentCommands);
    }

    [TestMethod]
    public async Task SessionBoostLimit_IsEnforcedBeforeFifthWrite()
    {
        FakeDeviceClient client = new()
        {
            Snapshot = FakeDeviceClient.CreateSafeSnapshot() with
            {
                OperatingState = DeviceOperatingState.Active,
                Capabilities = FakeDeviceClient.CreateSafeSnapshot().Capabilities! with
                {
                    MaximumConsecutiveBoosts = 10,
                },
            },
        };
        await using SessionController controller = new(client, new DeviceSafetyPolicy());

        for (int count = 0; count < 4; count++)
        {
            await controller.BoostTimeAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        }

        await Assert.ThrowsExactlyAsync<DeviceSafetyException>(() =>
            controller.BoostTimeAsync(TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.AreEqual(4, client.BoostCallCount);
    }

    [TestMethod]
    public async Task InvalidProfileSelection_IsRejectedBeforeClientWrite()
    {
        FakeDeviceClient client = new();
        await using SessionController controller = new(client, new DeviceSafetyPolicy());

        await Assert.ThrowsExactlyAsync<DeviceSafetyException>(() =>
            controller.SelectProfileAsync(4, CancellationToken.None));

        Assert.AreEqual(0, client.SelectProfileCallCount);
    }

    [TestMethod]
    public async Task OperationAfterDispose_IsRejectedBeforeClientCall()
    {
        FakeDeviceClient client = new();
        SessionController controller = new(client, new DeviceSafetyPolicy());
        await controller.DisposeAsync();

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() =>
            controller.RefreshAsync(CancellationToken.None));

        Assert.AreEqual(1, client.DisposeCallCount);
        Assert.AreEqual(0, client.RefreshCallCount);
    }

    private sealed class FakeDeviceClient : IDeviceClient
    {
        private int concurrentCommands;

        public DeviceSnapshot Snapshot { get; init; } = CreateSafeSnapshot();

        public event EventHandler<DeviceSnapshot>? SnapshotChanged;

        public Exception? StartException { get; init; }

        public bool DelayCommands { get; init; }

        public int StartCallCount { get; private set; }

        public int BoostCallCount { get; private set; }

        public int SelectProfileCallCount { get; private set; }

        public int DisposeCallCount { get; private set; }

        public int RefreshCallCount { get; private set; }

        public int MaximumConcurrentCommands { get; private set; }

        public TaskCompletionSource FirstCommandEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseCommands { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<DeviceCandidate>> ScanAsync(
            TimeSpan duration,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DeviceCandidate>>([]);

        public Task ConnectAsync(DeviceCandidate candidate, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<DeviceSnapshot> RefreshAsync(CancellationToken cancellationToken)
        {
            RefreshCallCount++;
            return Task.FromResult(Snapshot);
        }

        public Task<IReadOnlyList<HeatProfile>> GetProfilesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<HeatProfile>>([]);

        public Task SelectProfileAsync(int profileIndex, CancellationToken cancellationToken)
        {
            SelectProfileCallCount++;
            return ExecuteCommandAsync(cancellationToken);
        }

        public Task UpdateProfileAsync(HeatProfile profile, CancellationToken cancellationToken) =>
            ExecuteCommandAsync(cancellationToken);

        public async Task StartSessionAsync(CancellationToken cancellationToken)
        {
            StartCallCount++;
            if (StartException is not null)
            {
                throw StartException;
            }

            await ExecuteCommandAsync(cancellationToken);
        }

        public Task StopSessionAsync(CancellationToken cancellationToken) =>
            ExecuteCommandAsync(cancellationToken);

        public Task BoostTemperatureAsync(
            double temperatureCelsius,
            CancellationToken cancellationToken) =>
            ExecuteCommandAsync(cancellationToken);

        public Task BoostTimeAsync(TimeSpan duration, CancellationToken cancellationToken)
        {
            BoostCallCount++;
            return ExecuteCommandAsync(cancellationToken);
        }

        public Task SetStealthModeAsync(bool enabled, CancellationToken cancellationToken) =>
            ExecuteCommandAsync(cancellationToken);

        public Task SetLanternModeAsync(bool enabled, CancellationToken cancellationToken) =>
            ExecuteCommandAsync(cancellationToken);

        public ValueTask DisposeAsync()
        {
            GC.KeepAlive(SnapshotChanged);
            DisposeCallCount++;
            return ValueTask.CompletedTask;
        }

        private async Task ExecuteCommandAsync(CancellationToken cancellationToken)
        {
            int concurrent = Interlocked.Increment(ref concurrentCommands);
            MaximumConcurrentCommands = Math.Max(MaximumConcurrentCommands, concurrent);
            FirstCommandEntered.TrySetResult();
            try
            {
                if (DelayCommands)
                {
                    await ReleaseCommands.Task.WaitAsync(cancellationToken);
                }
            }
            finally
            {
                Interlocked.Decrement(ref concurrentCommands);
            }
        }

        internal static DeviceSnapshot CreateSafeSnapshot() => new(
            DeviceConnectionState.ConnectedControlEnabled,
            new DeviceIdentity(DeviceFamily.PeakPro, "Test", 0, "verified", null),
            new DeviceLimits(180, 340, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(3), 20, TimeSpan.FromSeconds(30)),
            new DeviceCapabilities(true, true, true, true, 3),
            ChamberKind.ThreeDXL,
            DeviceOperatingState.Idle,
            0,
            "Blue",
            VaporLevel.Standard,
            80,
            false,
            260,
            25,
            TimeSpan.FromSeconds(40),
            TimeSpan.Zero,
            true,
            true,
            null);
    }
}
