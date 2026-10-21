using DeskPuff.App.ViewModels;
using DeskPuff.App.Devices;
using DeskPuff.Core.Devices;
using DeskPuff.Core.Profiles;
using DeskPuff.Core.Safety;
using DeskPuff.Core.Sessions;

namespace DeskPuff.App.Tests;

[TestClass]
public sealed class SavedVaporCapabilityTests
{
    private string rootPath = string.Empty;
    [TestInitialize]
    public void Initialize() => rootPath = Path.Combine(Path.GetTempPath(), "desk-puff-vapor-tests", Guid.NewGuid().ToString("N"));
    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(rootPath)) { Directory.Delete(rootPath, true); }
    }

    [TestMethod]
    [DataRow(VaporLevel.High)]
    [DataRow(VaporLevel.Max)]
    [DataRow(VaporLevel.XL)]
    public async Task UnsupportedSavedVapor_RemainsSelectedAndStoredButCannotStart(VaporLevel vapor)
    {
        LocalProfileLibrary library = new(rootPath);
        await library.SaveHeatingAsync(Profile(vapor), null, CancellationToken.None);
        FakeDeviceClient client = new();
        client.SetVaporControlSupported(false);
        client.SetChamber(ChamberKind.ThreeD);
        Overrides overrides = new();
        await using SessionController controller = new(client, new DeviceSafetyPolicy(), sessionOverrides: overrides);
        await using MainViewModel viewModel = new(controller, true, rootPath);
        await viewModel.InitializeAsync();
        viewModel.ApplyHeatingProfileCommand.Execute(null);
        Assert.AreEqual(ProfileSource.Saved, viewModel.SelectedProfile!.Source);
        Assert.AreEqual(vapor, viewModel.EditorVapor);
        StringAssert.Contains(viewModel.StatusText, "choose a vapor mode");
        Assert.IsFalse(viewModel.StartStopCommand.CanExecute(null));
        viewModel.StartStopCommand.Execute(null);
        await Task.Delay(20);
        Assert.AreEqual(0, overrides.ApplyCount);
        Assert.AreEqual(0, client.TotalStateChangingCalls);
        Assert.AreEqual(vapor, (await library.LoadHeatingAsync(CancellationToken.None))[0].Profile.Vapor);
        Assert.IsTrue(viewModel.SaveHeatingProfileCommand.CanExecute(null));
        Assert.AreEqual("DEVICE VAPOR • UNREAD", viewModel.VaporText);

        // A device session can begin independently. Unsupported saved intent
        // must never disable the user's Stop control.
        client.BeginHeating(230, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(5));
        viewModel.ApplySnapshot(client.Snapshot);
        Assert.IsTrue(viewModel.StartStopCommand.CanExecute(null));
        viewModel.StartStopCommand.Execute(null);
        for (int attempt = 0; attempt < 100 && client.StopCallCount == 0; attempt++) { await Task.Delay(10); }
        Assert.AreEqual(1, client.StopCallCount);
        client.EndHeating();
        viewModel.ApplySnapshot(client.Snapshot);
        Assert.IsFalse(viewModel.StartStopCommand.CanExecute(null));

        await viewModel.SelectProfileAtAsync(0, CancellationToken.None);
        Assert.IsTrue(viewModel.StartStopCommand.CanExecute(null));
        Assert.AreEqual(ProfileSource.Device, viewModel.SelectedProfile!.Source);
    }

    private static LocalHeatingProfile Profile(VaporLevel vapor) => new()
    {
        Name = "Requested vapor", DeviceProfileName = "Requested vapor", TargetTemperatureCelsius = 220,
        DurationSeconds = 60, Vapor = vapor, BoostTemperatureCelsius = 5, BoostDurationSeconds = 10,
        ColorProfileName = "Blue", Colors = ["#1122FF"],
    };

    [TestMethod]
    public async Task OfflineDraft_StoresRequestedVaporWithoutChangingIt()
    {
        FakeDeviceClient client = new();
        await client.DisconnectAsync(CancellationToken.None);
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await using MainViewModel viewModel = new(controller, false, rootPath);
        await viewModel.InitializeAsync();
        viewModel.NewHeatingProfileCommand.Execute(null);
        viewModel.EditorVapor = VaporLevel.XL;
        Assert.IsTrue(viewModel.SaveHeatingProfileCommand.CanExecute(null));
        viewModel.SaveHeatingProfileCommand.Execute(null);
        for (int attempt = 0; attempt < 100 && viewModel.SavedHeatingProfiles.Count == 0; attempt++) { await Task.Delay(10); }
        LocalProfileLibrary library = new(rootPath);
        Assert.AreEqual(VaporLevel.XL, (await library.LoadHeatingAsync(CancellationToken.None))[0].Profile.Vapor);
        Assert.IsFalse(viewModel.IsConnected);
        Assert.AreEqual(0, client.TotalStateChangingCalls);
    }

    [TestMethod]
    public async Task Demo_CanStillSimulateSupportedVapor()
    {
        LocalProfileLibrary library = new(rootPath);
        await library.SaveHeatingAsync(Profile(VaporLevel.High) with { VaporMode = SessionVaporMode.Intense }, null, CancellationToken.None);
        DemoDeviceClient client = new();
        await using SessionController controller = new(client, new DeviceSafetyPolicy(), sessionOverrides: client);
        await using MainViewModel viewModel = new(controller, true, rootPath);
        await viewModel.InitializeAsync();
        viewModel.ApplyHeatingProfileCommand.Execute(null);
        Assert.IsTrue(viewModel.StartStopCommand.CanExecute(null));
        Assert.AreEqual(string.Empty, viewModel.SavedSessionRestriction);
        viewModel.StartStopCommand.Execute(null);
        for (int attempt = 0; attempt < 100 && !viewModel.IsHeating; attempt++) { await Task.Delay(10); }
        Assert.AreEqual("INTENSE VAPOR", viewModel.VaporText);
    }

    private sealed class Overrides : ISessionOverrideClient
    {
        internal int ApplyCount { get; private set; }
        public Task ApplySessionOverrideAsync(SessionOverride sessionOverride, CancellationToken cancellationToken)
        { ApplyCount++; return Task.CompletedTask; }
        public Task ClearSessionOverrideAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
