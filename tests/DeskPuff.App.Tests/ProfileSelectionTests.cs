using System.Collections.ObjectModel;
using DeskPuff.App.ViewModels;
using DeskPuff.Core.Devices;
using DeskPuff.Core.Safety;
using DeskPuff.Core.Sessions;

namespace DeskPuff.App.Tests;

[TestClass]
public sealed class ProfileSelectionTests
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
    public async Task SelectableProfiles_ListDeviceSlotsBeforeSavedProfiles()
    {
        FakeDeviceClient client = new();
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await using MainViewModel viewModel = new(controller, demoMode: true, rootPath);
        await ConnectAsync(viewModel, client, Saved("Morning"), Saved("Evening"));

        ObservableCollection<ProfileSelectionOption> order = viewModel.SelectableProfiles;

        Assert.HasCount(6, order);
        Assert.AreEqual(ProfileSource.Device, order[0].Source);
        Assert.AreEqual(0, order[0].DeviceProfileIndex);
        Assert.AreEqual(ProfileSource.Device, order[3].Source);
        Assert.AreEqual(3, order[3].DeviceProfileIndex);
        Assert.AreEqual(ProfileSource.Saved, order[4].Source);
        Assert.AreEqual("Morning", order[4].Name);
        Assert.AreEqual(ProfileSource.Saved, order[5].Source);
        Assert.AreEqual("Evening", order[5].Name);
    }

    [TestMethod]
    public async Task SelectedProfile_StartsOnTheDeviceActiveSlot()
    {
        FakeDeviceClient client = new();
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await using MainViewModel viewModel = new(controller, demoMode: true, rootPath);
        await ConnectAsync(viewModel, client, Saved("Morning"), Saved("Evening"));

        Assert.AreEqual(0, viewModel.SelectedProfileIndex);
        Assert.AreEqual(ProfileSource.Device, viewModel.SelectedProfile!.Source);
        Assert.AreEqual(0, viewModel.SelectedProfile.DeviceProfileIndex);
        Assert.AreEqual(0, client.TotalStateChangingCalls);
    }

    [TestMethod]
    public async Task MovingForwardPastTheLastEntry_WrapsToTheFirstDeviceSlot()
    {
        FakeDeviceClient client = new();
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await using MainViewModel viewModel = new(controller, demoMode: true, rootPath);
        await ConnectAsync(viewModel, client, Saved("Morning"), Saved("Evening"));
        await viewModel.SelectProfileAtAsync(5, CancellationToken.None);
        Assert.AreEqual(5, viewModel.SelectedProfileIndex);

        await viewModel.MoveProfileSelectionAsync(1, CancellationToken.None);

        Assert.AreEqual(0, viewModel.SelectedProfileIndex);
        Assert.AreEqual(ProfileSource.Device, viewModel.SelectedProfile!.Source);
        Assert.AreEqual(0, viewModel.SelectedProfile.DeviceProfileIndex);
    }

    [TestMethod]
    public async Task MovingBackPastTheFirstEntry_WrapsToTheLastSavedProfile()
    {
        FakeDeviceClient client = new();
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await using MainViewModel viewModel = new(controller, demoMode: true, rootPath);
        await ConnectAsync(viewModel, client, Saved("Morning"), Saved("Evening"));
        await viewModel.SelectProfileAtAsync(0, CancellationToken.None);
        Assert.AreEqual(0, viewModel.SelectedProfileIndex);

        await viewModel.MoveProfileSelectionAsync(-1, CancellationToken.None);

        Assert.AreEqual(5, viewModel.SelectedProfileIndex);
        Assert.AreEqual(ProfileSource.Saved, viewModel.SelectedProfile!.Source);
        Assert.AreEqual("Evening", viewModel.SelectedProfile.Name);
    }

    [TestMethod]
    public async Task SelectingASavedProfile_ReachesNoDevicePath()
    {
        FakeDeviceClient client = new();
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await using MainViewModel viewModel = new(controller, demoMode: true, rootPath);
        await ConnectAsync(viewModel, client, Saved("Morning"), Saved("Evening"));

        await viewModel.SelectProfileAtAsync(5, CancellationToken.None);

        Assert.AreEqual(ProfileSource.Saved, viewModel.SelectedProfile!.Source);
        Assert.AreEqual("Evening", viewModel.SelectedProfile.Name);
        Assert.AreEqual(0, client.SelectProfileCallCount);
        Assert.AreEqual(0, client.TotalStateChangingCalls);
        Assert.AreEqual(0, client.Snapshot.ActiveProfileIndex);
        Assert.AreEqual("Classic", client.Snapshot.ActiveProfileName);
    }

    [TestMethod]
    public async Task SelectingASavedProfile_LoadsTheEditorTheWayApplyDoes()
    {
        FakeDeviceClient client = new();
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await using MainViewModel viewModel = new(controller, demoMode: true, rootPath);
        await ConnectAsync(viewModel, client, Saved("Morning"), Saved("Evening", durationSeconds: 55));

        await viewModel.SelectProfileAtAsync(5, CancellationToken.None);

        Assert.AreEqual("Evening", viewModel.HeatingProfileName);
        Assert.AreEqual("Balanced", viewModel.EditorName);
        Assert.AreEqual(55, viewModel.EditorDurationSeconds);
    }

    [TestMethod]
    public async Task SelectingADeviceSlot_CallsThroughToTheController()
    {
        FakeDeviceClient client = new();
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await using MainViewModel viewModel = new(controller, demoMode: true, rootPath);
        await ConnectAsync(viewModel, client, Saved("Morning"));

        await viewModel.SelectProfileAtAsync(2, CancellationToken.None);

        Assert.AreEqual(1, client.SelectProfileCallCount);
        Assert.AreEqual(2, client.Snapshot.ActiveProfileIndex);
        Assert.AreEqual(ProfileSource.Device, viewModel.SelectedProfile!.Source);
        Assert.AreEqual(2, viewModel.SelectedProfile.DeviceProfileIndex);
        Assert.AreEqual(2, viewModel.SelectedProfileIndex);
    }

    [TestMethod]
    public async Task SelectingADeviceSlotAfterASavedProfile_ReturnsTheSelectionToTheDevice()
    {
        FakeDeviceClient client = new();
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await using MainViewModel viewModel = new(controller, demoMode: true, rootPath);
        await ConnectAsync(viewModel, client, Saved("Morning"), Saved("Evening"));
        await viewModel.SelectProfileAtAsync(4, CancellationToken.None);
        Assert.AreEqual(ProfileSource.Saved, viewModel.SelectedProfile!.Source);

        await viewModel.SelectProfileAtAsync(2, CancellationToken.None);

        Assert.AreEqual(1, client.SelectProfileCallCount);
        Assert.AreEqual(ProfileSource.Device, viewModel.SelectedProfile!.Source);
        Assert.AreEqual(2, viewModel.SelectedProfile.DeviceProfileIndex);
    }

    [TestMethod]
    public async Task AnUnsafeSavedProfile_IsRejectedBeforeItBecomesTheSelection()
    {
        FakeDeviceClient client = new();
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await using MainViewModel viewModel = new(controller, demoMode: true, rootPath);
        await ConnectAsync(viewModel, client, Saved("Scorcher", celsius: 400));

        await Assert.ThrowsExactlyAsync<DeviceSafetyException>(
            () => viewModel.SelectProfileAtAsync(4, CancellationToken.None));

        Assert.AreEqual(ProfileSource.Device, viewModel.SelectedProfile!.Source);
        Assert.AreEqual(0, viewModel.SelectedProfile.DeviceProfileIndex);
        Assert.AreEqual(0, client.TotalStateChangingCalls);
    }

    [TestMethod]
    public async Task WithNoSavedProfiles_TheOrderIsTheFourDeviceSlots()
    {
        FakeDeviceClient client = new();
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await using MainViewModel viewModel = new(controller, demoMode: true, rootPath);
        await ConnectAsync(viewModel, client);

        Assert.HasCount(4, viewModel.SelectableProfiles);
        Assert.IsTrue(viewModel.SelectableProfiles.All(
            option => option.Source == ProfileSource.Device));
        Assert.AreEqual(0, viewModel.SelectedProfileIndex);
    }

    [TestMethod]
    public async Task WithNoSavedProfiles_WrappingMatchesTodaysDeviceSlotCycle()
    {
        FakeDeviceClient client = new();
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await using MainViewModel viewModel = new(controller, demoMode: true, rootPath);
        await ConnectAsync(viewModel, client);

        await viewModel.MoveProfileSelectionAsync(-1, CancellationToken.None);

        Assert.AreEqual(3, viewModel.SelectedProfileIndex);
        Assert.AreEqual(3, client.Snapshot.ActiveProfileIndex);
        Assert.AreEqual(1, client.SelectProfileCallCount);

        await viewModel.MoveProfileSelectionAsync(1, CancellationToken.None);

        Assert.AreEqual(0, viewModel.SelectedProfileIndex);
        Assert.AreEqual(0, client.Snapshot.ActiveProfileIndex);
        Assert.AreEqual(2, client.SelectProfileCallCount);
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
            ["#0000FF"]);

    private static async Task ConnectAsync(
        MainViewModel viewModel,
        FakeDeviceClient client,
        params HeatingProfileOption[] savedProfiles)
    {
        await viewModel.InitializeAsync();

        // The library load is disk-backed; replace its result with a known list so
        // the order under test does not depend on what is on this machine.
        viewModel.SavedHeatingProfiles.Clear();
        foreach (HeatingProfileOption savedProfile in savedProfiles)
        {
            viewModel.SavedHeatingProfiles.Add(savedProfile);
        }

        Assert.AreEqual(DeviceConnectionState.ConnectedControlEnabled, client.Snapshot.ConnectionState);
        Assert.HasCount(4, viewModel.DeviceProfiles);
    }
}
