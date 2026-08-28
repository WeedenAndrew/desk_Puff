using DeskPuff.App.ViewModels;
using DeskPuff.Core.Devices;
using DeskPuff.Core.Safety;
using DeskPuff.Core.Sessions;

namespace DeskPuff.App.Tests;

[TestClass]
public sealed class PollingRecoveryTests
{
    private string rootPath = string.Empty;

    [TestInitialize]
    public void Initialize() =>
        rootPath = Path.Combine(Path.GetTempPath(), "desk-puff-app-tests", Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(rootPath))
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [TestMethod]
    public async Task Polling_OneTransientFailureRecoversAndThreeLaterFailuresDisconnectClearly()
    {
        FakeDeviceClient client = new();
        // The success between the isolated failure and the final three failures
        // proves that a good refresh resets the consecutive-failure counter.
        client.QueueRefreshFailures(true, false, true, true, true);
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await using MainViewModel viewModel = new(controller, demoMode: true, rootPath);

        await viewModel.InitializeAsync();
        await WaitForAsync(() =>
            !viewModel.IsConnected &&
            viewModel.StatusText == "Device disconnected after repeated communication failures");

        Assert.AreEqual(5, client.RefreshCallCount);
        Assert.AreEqual(1, client.DisconnectCallCount);
        Assert.AreEqual(DeviceConnectionState.Disconnected, client.Snapshot.ConnectionState);
        Assert.IsTrue(viewModel.IsDisconnected);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 500 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }

        Assert.IsTrue(condition(), "Polling did not reach the expected disconnected state in time.");
    }
}
