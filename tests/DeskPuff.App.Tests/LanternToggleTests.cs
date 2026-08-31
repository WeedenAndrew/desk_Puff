using DeskPuff.App.ViewModels;
using DeskPuff.Core.Safety;
using DeskPuff.Core.Sessions;

namespace DeskPuff.App.Tests;

[TestClass]
public sealed class LanternToggleTests
{
    private static readonly bool[] ExpectedToggleValues = [true, false];

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
    public async Task TogglingLanternTwice_WritesOneThenZeroAndAdvancesLocalState()
    {
        FakeDeviceClient client = new();
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await using MainViewModel viewModel = new(controller, demoMode: true, rootPath);

        Assert.AreEqual("TURN LANTERN ON", viewModel.LanternButtonText);
        Assert.IsTrue(viewModel.ToggleLanternCommand.CanExecute(null));

        viewModel.ToggleLanternCommand.Execute(null);
        await WaitForAsync(() =>
            client.LanternModeValues.Count == 1 &&
            string.Equals(viewModel.LanternButtonText, "TURN LANTERN OFF", StringComparison.Ordinal));

        viewModel.ToggleLanternCommand.Execute(null);
        await WaitForAsync(() =>
            client.LanternModeValues.Count == 2 &&
            string.Equals(viewModel.LanternButtonText, "TURN LANTERN ON", StringComparison.Ordinal));

        CollectionAssert.AreEqual(
            ExpectedToggleValues,
            client.LanternModeValues.ToArray(),
            "The second toggle must send 00 rather than repeating 01.");
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }

        Assert.IsTrue(condition(), "The lantern command did not complete in time.");
    }
}
