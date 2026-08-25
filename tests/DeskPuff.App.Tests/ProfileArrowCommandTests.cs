using DeskPuff.App.ViewModels;
using DeskPuff.Core.Devices;
using DeskPuff.Core.Safety;
using DeskPuff.Core.Sessions;

namespace DeskPuff.App.Tests;

/// <summary>
/// The arrows gate on how many places there are to swipe to, and swipe through
/// the combined order. These drive them the way the app does: through the
/// commands the buttons bind to, and through the keyboard shortcut path.
/// </summary>
[TestClass]
public sealed class ProfileArrowCommandTests
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
    public async Task WithOneDeviceSlotAndNoSavedProfiles_TheArrowsStayDisabled()
    {
        FakeDeviceClient client = new();
        client.SetProfiles([OnlyProfile()]);
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await using MainViewModel viewModel = new(controller, demoMode: true, rootPath);
        await ConnectAsync(viewModel);

        Assert.HasCount(1, viewModel.SelectableProfiles);
        Assert.IsFalse(viewModel.PreviousProfileCommand.CanExecute(null));
        Assert.IsFalse(viewModel.NextProfileCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task WithOneDeviceSlotAndOneSavedProfile_TheArrowsBecomeAvailable()
    {
        FakeDeviceClient client = new();
        client.SetProfiles([OnlyProfile()]);
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await using MainViewModel viewModel = new(controller, demoMode: true, rootPath);
        await ConnectAsync(viewModel);
        viewModel.SavedHeatingProfiles.Add(Saved("Morning"));

        Assert.HasCount(2, viewModel.SelectableProfiles);
        Assert.IsTrue(viewModel.PreviousProfileCommand.CanExecute(null));
        Assert.IsTrue(viewModel.NextProfileCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task WithFourDeviceSlots_TheArrowsStayAvailableAsBefore()
    {
        FakeDeviceClient client = new();
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await using MainViewModel viewModel = new(controller, demoMode: true, rootPath);
        await ConnectAsync(viewModel);

        Assert.HasCount(4, viewModel.SelectableProfiles);
        Assert.IsTrue(viewModel.PreviousProfileCommand.CanExecute(null));
        Assert.IsTrue(viewModel.NextProfileCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task WhileDisconnected_TheArrowsAreUnavailable()
    {
        FakeDeviceClient client = new();
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await using MainViewModel viewModel = new(controller, demoMode: true, rootPath);

        Assert.IsFalse(viewModel.PreviousProfileCommand.CanExecute(null));
        Assert.IsFalse(viewModel.NextProfileCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task ThePreviousArrowCommand_StepsBackOntoTheSavedProfile()
    {
        FakeDeviceClient client = new();
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await using MainViewModel viewModel = new(controller, demoMode: true, rootPath);
        await ConnectAsync(viewModel, Saved("Morning"));
        Assert.AreEqual(0, viewModel.SelectedProfileIndex);

        viewModel.PreviousProfileCommand.Execute(null);
        await WaitForAsync(() => viewModel.SelectedProfileIndex == 4);

        Assert.AreEqual(ProfileSource.Saved, viewModel.SelectedProfile!.Source);
        Assert.AreEqual("Morning", viewModel.SelectedProfile.Name);
        Assert.AreEqual(0, client.TotalStateChangingCalls);
    }

    [TestMethod]
    public async Task TheNextArrowCommand_StepsForwardOntoTheNextDeviceSlot()
    {
        FakeDeviceClient client = new();
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await using MainViewModel viewModel = new(controller, demoMode: true, rootPath);
        await ConnectAsync(viewModel, Saved("Morning"));
        Assert.AreEqual(0, viewModel.SelectedProfileIndex);

        viewModel.NextProfileCommand.Execute(null);
        await WaitForAsync(() => viewModel.SelectedProfileIndex == 1);

        Assert.AreEqual(ProfileSource.Device, viewModel.SelectedProfile!.Source);
        Assert.AreEqual(1, viewModel.SelectedProfile.DeviceProfileIndex);
        Assert.AreEqual(1, client.SelectProfileCallCount);
        Assert.AreEqual(1, client.Snapshot.ActiveProfileIndex);
    }

    [TestMethod]
    public async Task TheNextArrowCommand_WrapsFromTheLastSavedProfileToTheFirstDeviceSlot()
    {
        FakeDeviceClient client = new();
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await using MainViewModel viewModel = new(controller, demoMode: true, rootPath);
        await ConnectAsync(viewModel, Saved("Morning"));
        await viewModel.SelectProfileAtAsync(4, CancellationToken.None);
        Assert.AreEqual(4, viewModel.SelectedProfileIndex);

        viewModel.NextProfileCommand.Execute(null);
        await WaitForAsync(() => viewModel.SelectedProfileIndex == 0);

        Assert.AreEqual(ProfileSource.Device, viewModel.SelectedProfile!.Source);
        Assert.AreEqual(0, viewModel.SelectedProfile.DeviceProfileIndex);
    }

    [TestMethod]
    public async Task TheKeyboardShortcuts_WalkTheSameCombinedOrderInBothDirections()
    {
        FakeDeviceClient client = new();
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await using MainViewModel viewModel = new(controller, demoMode: true, rootPath);
        await ConnectAsync(viewModel, Saved("Morning"), Saved("Evening"));
        Assert.AreEqual(0, viewModel.SelectedProfileIndex);

        // Back past the first entry wraps onto the last saved profile, which
        // costs the device nothing.
        Assert.IsTrue(await viewModel.HandleShortcutAsync(viewModel.PreviousProfileKey));
        await WaitForAsync(() => viewModel.SelectedProfileIndex == 5);
        Assert.AreEqual("Evening", viewModel.SelectedProfile!.Name);

        Assert.IsTrue(await viewModel.HandleShortcutAsync(viewModel.PreviousProfileKey));
        await WaitForAsync(() => viewModel.SelectedProfileIndex == 4);
        Assert.AreEqual("Morning", viewModel.SelectedProfile!.Name);

        // One more step back lands on a device slot, and that one really does
        // drive the device.
        Assert.IsTrue(await viewModel.HandleShortcutAsync(viewModel.PreviousProfileKey));
        await WaitForAsync(() => viewModel.SelectedProfileIndex == 3);
        Assert.AreEqual(3, viewModel.SelectedProfile!.DeviceProfileIndex);
        Assert.AreEqual(3, client.Snapshot.ActiveProfileIndex);

        Assert.IsTrue(await viewModel.HandleShortcutAsync(viewModel.NextProfileKey));
        await WaitForAsync(() => viewModel.SelectedProfileIndex == 4);
        Assert.AreEqual("Morning", viewModel.SelectedProfile!.Name);

        // Two saved stops in a row, and one device stop, so exactly one write.
        Assert.AreEqual(1, client.SelectProfileCallCount);
        Assert.AreEqual(1, client.TotalStateChangingCalls);
    }

    [TestMethod]
    public async Task WithNoSavedProfiles_TheKeyboardShortcutsCycleTheFourSlotsAsBefore()
    {
        FakeDeviceClient client = new();
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await using MainViewModel viewModel = new(controller, demoMode: true, rootPath);
        await ConnectAsync(viewModel);

        Assert.IsTrue(await viewModel.HandleShortcutAsync(viewModel.PreviousProfileKey));
        await WaitForAsync(() => client.Snapshot.ActiveProfileIndex == 3);
        Assert.AreEqual(3, viewModel.SelectedProfileIndex);

        Assert.IsTrue(await viewModel.HandleShortcutAsync(viewModel.NextProfileKey));
        await WaitForAsync(() => client.Snapshot.ActiveProfileIndex == 0);
        Assert.AreEqual(0, viewModel.SelectedProfileIndex);

        Assert.AreEqual(2, client.SelectProfileCallCount);
    }

    private static HeatProfile OnlyProfile() =>
        new(
            0,
            "Classic",
            220,
            TimeSpan.FromSeconds(40),
            VaporLevel.Standard,
            10,
            TimeSpan.FromSeconds(10),
            "#0000FF");

    private static HeatingProfileOption Saved(string name) =>
        new(name, "Balanced", 220, 40, VaporLevel.Standard, 10, 10, "Custom colorway", ["#0000FF"]);

    /// <summary>
    /// The arrow commands are <see cref="System.Windows.Input.ICommand"/>, so
    /// executing one is fire and forget. Wait for the effect rather than
    /// assuming the continuation has already run.
    /// </summary>
    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }

        Assert.IsTrue(condition(), "The command did not reach the expected state in time.");
    }

    private static async Task ConnectAsync(
        MainViewModel viewModel,
        params HeatingProfileOption[] savedProfiles)
    {
        await viewModel.InitializeAsync();
        viewModel.SavedHeatingProfiles.Clear();
        foreach (HeatingProfileOption savedProfile in savedProfiles)
        {
            viewModel.SavedHeatingProfiles.Add(savedProfile);
        }
    }
}
