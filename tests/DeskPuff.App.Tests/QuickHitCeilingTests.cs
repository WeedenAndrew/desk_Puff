using DeskPuff.App.ViewModels;
using DeskPuff.Core.Devices;
using DeskPuff.Core.Safety;
using DeskPuff.Core.Sessions;

namespace DeskPuff.App.Tests;

/// <summary>
/// A quick hit is capped at 15 F and 15 s regardless of what the setting says.
/// The clamp sits where the value reaches the device, so a preference changed
/// by any route still cannot get past it.
/// </summary>
[TestClass]
public sealed class QuickHitCeilingTests
{
    private const double CeilingCelsius = 15 / 1.8;

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
    public async Task ATemperatureQuickHitAboveTheCeiling_IsClampedOnTheWayToTheDevice()
    {
        FakeDeviceClient client = new();
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await using MainViewModel viewModel = new(controller, demoMode: true, rootPath);
        await StartHeatingAsync(viewModel, client);

        // 40 F is well past the ceiling, and past what the device would take.
        viewModel.QuickHitTemperature = 40;
        await BoostAsync(viewModel, viewModel.BoostTemperatureCommand);

        Assert.AreEqual(1, client.BoostCallCount);
        Assert.AreEqual(CeilingCelsius, client.LastBoostTemperatureCelsius, 0.001);
    }

    [TestMethod]
    public async Task ATimeQuickHitAboveTheCeiling_IsClampedOnTheWayToTheDevice()
    {
        FakeDeviceClient client = new();
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await using MainViewModel viewModel = new(controller, demoMode: true, rootPath);
        await StartHeatingAsync(viewModel, client);

        viewModel.QuickHitTimeSeconds = 45;
        await BoostAsync(viewModel, viewModel.BoostTimeCommand);

        Assert.AreEqual(1, client.BoostCallCount);
        Assert.AreEqual(TimeSpan.FromSeconds(15), client.LastBoostDuration);
    }

    [TestMethod]
    public async Task AQuickHitInsideTheCeiling_ReachesTheDeviceUnchanged()
    {
        FakeDeviceClient client = new();
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await using MainViewModel viewModel = new(controller, demoMode: true, rootPath);
        await StartHeatingAsync(viewModel, client);

        viewModel.QuickHitTemperature = 9;
        await BoostAsync(viewModel, viewModel.BoostTemperatureCommand);

        Assert.AreEqual(9 / 1.8, client.LastBoostTemperatureCelsius, 0.001);
    }

    [TestMethod]
    public async Task ATimeQuickHitInsideTheCeiling_ReachesTheDeviceUnchanged()
    {
        FakeDeviceClient client = new();
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await using MainViewModel viewModel = new(controller, demoMode: true, rootPath);
        await StartHeatingAsync(viewModel, client);

        viewModel.QuickHitTimeSeconds = 10;
        await BoostAsync(viewModel, viewModel.BoostTimeCommand);

        Assert.AreEqual(TimeSpan.FromSeconds(10), client.LastBoostDuration);
    }

    private static async Task StartHeatingAsync(MainViewModel viewModel, FakeDeviceClient client)
    {
        await viewModel.InitializeAsync();
        client.BeginHeating(200, TimeSpan.FromSeconds(40), TimeSpan.Zero);
        viewModel.ApplySnapshot(client.Snapshot);
        Assert.IsTrue(viewModel.CanBoost, "The quick hits need a running session.");
    }

    private static async Task BoostAsync(MainViewModel viewModel, System.Windows.Input.ICommand command)
    {
        string before = viewModel.StatusText;
        command.Execute(null);
        for (int attempt = 0;
            attempt < 200 && string.Equals(viewModel.StatusText, before, StringComparison.Ordinal);
            attempt++)
        {
            await Task.Delay(10);
        }
    }
}
