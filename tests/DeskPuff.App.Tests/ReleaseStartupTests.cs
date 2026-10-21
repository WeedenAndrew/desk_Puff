using DeskPuff.App.Infrastructure;
using DeskPuff.App.ViewModels;
using DeskPuff.Core.Safety;
using DeskPuff.Core.Sessions;

namespace DeskPuff.App.Tests;

[TestClass]
public sealed class ReleaseStartupTests
{
    [TestMethod]
    [DataRow("--demo")]
    [DataRow("--DEMO")]
    [DataRow("--unknown")]
    public void UnsupportedLaunchFlagReturnsBeforeStartup(string argument)
    {
        Assert.IsFalse(Program.AreArgumentsSupported([argument]));
        Assert.AreEqual(2, Program.Main([argument]));
        Assert.AreEqual(2, Program.Main(["--trace-writes", argument]));
    }

    [TestMethod]
    public void ProductionAssemblyContainsNoSimulator()
    {
        Assert.IsTrue(Program.AreArgumentsSupported([]));
        Assert.IsTrue(Program.AreArgumentsSupported(["--trace-writes"]));
        Assert.IsFalse(typeof(App).Assembly.GetTypes().Any(type =>
            type.Name.Contains("DemoDevice", StringComparison.Ordinal) ||
            type.Name.Contains("SimulatedDevice", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task InitializationScansInjectedClientWithoutConnectingOrReadingGlobalPreferences()
    {
        string root = Path.Combine(Path.GetTempPath(), "desk-puff-startup-test", Guid.NewGuid().ToString("N"));
        string preferences = Path.Combine(root, "preferences.json");
        try
        {
            await UserPreferencesStore.SaveAsync(new UserPreferences { AppAccentHex = "#123456" }, CancellationToken.None, pathOverride: preferences);
            FakeDeviceClient client = new();
            await client.DisconnectAsync(CancellationToken.None);
            await using SessionController controller = new(client, new DeviceSafetyPolicy());
            await using MainViewModel viewModel = new(controller, root, preferencesPath: preferences);
            await viewModel.InitializeAsync();
            Assert.IsFalse(viewModel.IsConnected);
            Assert.IsNotNull(viewModel.SelectedCandidate);
            Assert.AreEqual(0, client.ConnectCallCount);
            Assert.AreEqual("#123456", (await UserPreferencesStore.LoadAsync(CancellationToken.None, pathOverride: preferences)).AppAccentHex);
            Assert.AreEqual(0, client.TotalStateChangingCalls);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
