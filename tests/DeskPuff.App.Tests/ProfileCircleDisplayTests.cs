using DeskPuff.App.ViewModels;
using DeskPuff.Core.Devices;
using DeskPuff.Core.Safety;
using DeskPuff.Core.Sessions;

namespace DeskPuff.App.Tests;

/// <summary>
/// The circle is the main display of a device that heats, so what it means has
/// to be pinned down. A selection is a plan; a running session is a fact, and
/// the fact always wins.
/// </summary>
[TestClass]
public sealed class ProfileCircleDisplayTests
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
    public async Task IdleOnADeviceSlot_TheCircleShowsTheDevice()
    {
        FakeDeviceClient client = new();
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await using MainViewModel viewModel = new(controller, demoMode: true, rootPath);
        await ConnectAsync(viewModel, Saved("Morning"));

        Assert.AreEqual("CLASSIC", viewModel.ProfileName);
        Assert.AreEqual("220°", viewModel.TemperatureText);
        Assert.AreEqual("SET TEMPERATURE", viewModel.TemperatureCaption);
        Assert.AreEqual("00:40", viewModel.SessionTimeText);
        Assert.IsFalse(viewModel.SavedProfileCaptionVisibility);
    }

    [TestMethod]
    public async Task IdleOnASavedProfile_TheCircleShowsTheSavedProfile()
    {
        FakeDeviceClient client = new();
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await using MainViewModel viewModel = new(controller, demoMode: true, rootPath);
        await ConnectAsync(viewModel, Saved("Morning", celsius: 210, durationSeconds: 65));

        await viewModel.SelectProfileAtAsync(4, CancellationToken.None);

        Assert.AreEqual("MORNING", viewModel.ProfileName);
        Assert.AreEqual("210°", viewModel.TemperatureText);
        Assert.AreEqual("01:05", viewModel.SessionTimeText);
        Assert.AreEqual("#22FF88", viewModel.ActiveProfileColor);
        Assert.AreEqual(0, client.TotalStateChangingCalls);
    }

    [TestMethod]
    public async Task IdleOnASavedProfile_TheCircleSaysItIsNotOnTheDevice()
    {
        FakeDeviceClient client = new();
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await using MainViewModel viewModel = new(controller, demoMode: true, rootPath);
        await ConnectAsync(viewModel, Saved("Morning"));

        await viewModel.SelectProfileAtAsync(4, CancellationToken.None);

        Assert.IsTrue(viewModel.SavedProfileCaptionVisibility);
    }

    [TestMethod]
    public async Task MovingBackToADeviceSlot_TheCaptionGoesAway()
    {
        FakeDeviceClient client = new();
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await using MainViewModel viewModel = new(controller, demoMode: true, rootPath);
        await ConnectAsync(viewModel, Saved("Morning"));
        await viewModel.SelectProfileAtAsync(4, CancellationToken.None);
        Assert.IsTrue(viewModel.SavedProfileCaptionVisibility);

        await viewModel.SelectProfileAtAsync(1, CancellationToken.None);

        Assert.IsFalse(viewModel.SavedProfileCaptionVisibility);
        Assert.AreEqual("BALANCED", viewModel.ProfileName);
    }

    // ---- invariant one: heat beats the plan --------------------------------

    [TestMethod]
    public async Task WhileHeating_TheCircleShowsDeviceTelemetryNotTheSelection()
    {
        FakeDeviceClient client = new();
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await using MainViewModel viewModel = new(controller, demoMode: true, rootPath);
        await ConnectAsync(viewModel, Saved("Morning", celsius: 210, durationSeconds: 65));
        await viewModel.SelectProfileAtAsync(4, CancellationToken.None);
        Assert.AreEqual("210°", viewModel.TemperatureText);

        client.BeginHeating(
            currentCelsius: 188,
            total: TimeSpan.FromSeconds(40),
            elapsed: TimeSpan.FromSeconds(12));
        viewModel.ApplySnapshot(client.Snapshot);

        Assert.AreEqual("188°", viewModel.TemperatureText);
        Assert.AreEqual("LIVE CHAMBER", viewModel.TemperatureCaption);
        Assert.AreEqual("00:28", viewModel.SessionTimeText);
        Assert.AreEqual("CLASSIC", viewModel.ProfileName);
        Assert.IsFalse(
            viewModel.SavedProfileCaptionVisibility,
            "A running session is a fact; the circle must not claim to be showing a plan.");
    }

    [TestMethod]
    public async Task WhileHeating_TheRingColourComesFromTheDeviceSlot()
    {
        FakeDeviceClient client = new();
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await using MainViewModel viewModel = new(controller, demoMode: true, rootPath);
        await ConnectAsync(viewModel, Saved("Morning"));
        await viewModel.SelectProfileAtAsync(4, CancellationToken.None);
        Assert.AreEqual("#22FF88", viewModel.ActiveProfileColor);

        client.BeginHeating(200, TimeSpan.FromSeconds(40), TimeSpan.Zero);
        viewModel.ApplySnapshot(client.Snapshot);

        Assert.AreEqual("#0000FF", viewModel.ActiveProfileColor);
    }

    // ---- invariant two: start/stop is always about the device --------------

    [TestMethod]
    public async Task StartStopText_FollowsTheDeviceWhicheverProfileIsSelected()
    {
        FakeDeviceClient client = new();
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await using MainViewModel viewModel = new(controller, demoMode: true, rootPath);
        await ConnectAsync(viewModel, Saved("Morning"));

        Assert.AreEqual("START", viewModel.StartStopText);

        await viewModel.SelectProfileAtAsync(4, CancellationToken.None);
        Assert.AreEqual("START", viewModel.StartStopText);

        client.BeginHeating(200, TimeSpan.FromSeconds(40), TimeSpan.Zero);
        viewModel.ApplySnapshot(client.Snapshot);

        Assert.AreEqual("STOP", viewModel.StartStopText);
    }

    [TestMethod]
    public async Task StartStopGating_FollowsTheDeviceNotTheSelection()
    {
        FakeDeviceClient client = new();
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await using MainViewModel viewModel = new(controller, demoMode: true, rootPath);
        await ConnectAsync(viewModel, Saved("Morning"));
        Assert.IsTrue(viewModel.CanStartOrStop);
        Assert.IsTrue(viewModel.StartStopCommand.CanExecute(null));

        await viewModel.SelectProfileAtAsync(4, CancellationToken.None);

        Assert.IsTrue(
            viewModel.CanStartOrStop,
            "Selecting a saved profile must not change whether the device can be started.");
        Assert.IsTrue(viewModel.StartStopCommand.CanExecute(null));

        client.BeginHeating(200, TimeSpan.FromSeconds(40), TimeSpan.Zero);
        viewModel.ApplySnapshot(client.Snapshot);

        Assert.IsTrue(viewModel.CanStartOrStop, "A running session must always be stoppable.");
        Assert.IsTrue(viewModel.StartStopCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task WithNoChamber_StartStopStaysBlockedEvenWithASavedProfileSelected()
    {
        FakeDeviceClient client = new();
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await using MainViewModel viewModel = new(controller, demoMode: true, rootPath);
        await ConnectAsync(viewModel, Saved("Morning"));
        await viewModel.SelectProfileAtAsync(4, CancellationToken.None);

        client.SetChamber(ChamberKind.None);
        viewModel.ApplySnapshot(client.Snapshot);

        Assert.IsFalse(viewModel.StartStopCommand.CanExecute(null));
        Assert.IsTrue(
            viewModel.SavedProfileCaptionVisibility,
            "The selection survives; only the ability to run it is gated.");
    }

    // ---- the strip ---------------------------------------------------------

    [TestMethod]
    public async Task EachStripChip_CarriesItsOwnProfileColours()
    {
        FakeDeviceClient client = new();
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await using MainViewModel viewModel = new(controller, demoMode: true, rootPath);
        await ConnectAsync(viewModel, Saved("Morning"));

        ProfileSelectionOption savedChip = viewModel.SelectableProfiles[4];
        ProfileSelectionOption deviceChip = viewModel.SelectableProfiles[0];

        Assert.AreEqual("#22FF88", savedChip.ColorOne);
        Assert.AreEqual("#1188FF", savedChip.ColorTwo);
        // One and two colours only, so three and four fall back to the last.
        Assert.AreEqual("#1188FF", savedChip.ColorFour);
        Assert.AreEqual("#0000FF", deviceChip.ColorOne);
        Assert.AreNotEqual(savedChip.ColorOne, deviceChip.ColorOne);
    }

    [TestMethod]
    public async Task TappingAStripChip_MovesTheSelectionToIt()
    {
        FakeDeviceClient client = new();
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await using MainViewModel viewModel = new(controller, demoMode: true, rootPath);
        await ConnectAsync(viewModel, Saved("Morning"));
        Assert.AreEqual(0, viewModel.SelectedProfileIndex);

        // What the ListBox does when the user taps the third chip.
        viewModel.SelectedProfile = viewModel.SelectableProfiles[2];
        await WaitForAsync(() => viewModel.SelectedProfileIndex == 2);

        Assert.AreEqual(ProfileSource.Device, viewModel.SelectedProfile!.Source);
        Assert.AreEqual(2, viewModel.SelectedProfile.DeviceProfileIndex);
        Assert.AreEqual(1, client.SelectProfileCallCount);
    }

    [TestMethod]
    public async Task TappingTheSavedChip_MovesTheSelectionWithoutTouchingTheDevice()
    {
        FakeDeviceClient client = new();
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await using MainViewModel viewModel = new(controller, demoMode: true, rootPath);
        await ConnectAsync(viewModel, Saved("Morning"));

        viewModel.SelectedProfile = viewModel.SelectableProfiles[4];
        await WaitForAsync(() => viewModel.SelectedProfileIndex == 4);

        Assert.AreEqual(ProfileSource.Saved, viewModel.SelectedProfile!.Source);
        Assert.AreEqual(0, client.TotalStateChangingCalls);
    }

    private static HeatingProfileOption Saved(
        string name,
        double celsius = 220,
        double durationSeconds = 40) =>
        new(
            name,
            "Balanced",
            celsius,
            durationSeconds,
            VaporLevel.Standard,
            10,
            10,
            "Custom colorway",
            ["#22FF88", "#1188FF"]);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }

        Assert.IsTrue(condition(), "The view model did not reach the expected state in time.");
    }

    private static async Task ConnectAsync(
        MainViewModel viewModel,
        params HeatingProfileOption[] savedProfiles)
    {
        await viewModel.InitializeAsync();

        // Assert in Celsius so the expected values read as the stored values.
        if (viewModel.UseFahrenheit)
        {
            viewModel.ToggleTemperatureUnitCommand.Execute(null);
        }

        viewModel.SavedHeatingProfiles.Clear();
        foreach (HeatingProfileOption savedProfile in savedProfiles)
        {
            viewModel.SavedHeatingProfiles.Add(savedProfile);
        }
    }
}
