using DeskPuff.App.ViewModels;
using DeskPuff.Core.Devices;
using DeskPuff.Core.Profiles;
using DeskPuff.Core.Safety;
using DeskPuff.Core.Sessions;

namespace DeskPuff.App.Tests;

[TestClass]
public sealed class NamedVaporProfileTests
{
    [TestMethod]
    public async Task DisconnectedDraftSavesNamedModeAndPreservesLegacyIntent()
    {
        string root = Path.Combine(Path.GetTempPath(), "desk-puff-vapor-ui", Guid.NewGuid().ToString("N"));
        try
        {
            FakeDeviceClient client = new(); await client.DisconnectAsync(CancellationToken.None);
            await using SessionController controller = new(client, new DeviceSafetyPolicy());
            await using MainViewModel viewModel = new(controller, false, root);
            await viewModel.InitializeAsync(); viewModel.NewHeatingProfileCommand.Execute(null);
            Assert.AreEqual(SessionVaporMode.Smooth, viewModel.EditorVaporMode);
            viewModel.HeatingProfileName = "named"; viewModel.EditorVapor = VaporLevel.Max; viewModel.EditorVaporMode = SessionVaporMode.Intense;
            Assert.IsTrue(viewModel.SaveHeatingProfileCommand.CanExecute(null));
            viewModel.SaveHeatingProfileCommand.Execute(null);
            for (int attempt = 0; attempt < 200 && viewModel.SavedHeatingProfiles.Count == 0; attempt++) { await Task.Delay(10); }
            LocalProfileLibrary library = new(root);
            LocalHeatingProfile profile = (await library.LoadHeatingAsync(CancellationToken.None))[0].Profile;
            Assert.AreEqual(VaporLevel.Max, profile.Vapor);
            Assert.AreEqual(SessionVaporMode.Intense, profile.VaporMode);
            Assert.AreEqual(0, client.TotalStateChangingCalls);
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, true); } }
    }

    [TestMethod]
    public async Task LocalJsonEditingInAppOnlyContextDoesNotWriteDevice()
    {
        string root = Path.Combine(Path.GetTempPath(), "desk-puff-ff-edit", Guid.NewGuid().ToString("N"));
        try
        {
            FakeDeviceClient client = new();
            await using SessionController controller = new(client, new DeviceSafetyPolicy());
            await using MainViewModel viewModel = new(controller, true, root);
            await viewModel.InitializeAsync();
            viewModel.NewHeatingProfileCommand.Execute(null);
            viewModel.HeatingProfileName = "named";
            viewModel.EditorVaporMode = SessionVaporMode.Bold;
            viewModel.SaveHeatingProfileCommand.Execute(null);
            for (int attempt = 0; attempt < 200 && viewModel.SavedHeatingProfiles.Count == 0; attempt++) { await Task.Delay(10); }
            viewModel.ApplySnapshot(client.Snapshot with { ActiveProfileIndex = 255 });
            viewModel.EditHeatingProfileCommand.Execute(null);
            Assert.IsFalse(viewModel.StatusIsError, viewModel.StatusText);
            Assert.AreEqual(SessionVaporMode.Bold, viewModel.EditorVaporMode);
            viewModel.EditorVaporMode = SessionVaporMode.Intense;
            viewModel.SaveHeatingProfileCommand.Execute(null);
            for (int attempt = 0; attempt < 200 && viewModel.SavedHeatingProfiles[0].VaporMode != SessionVaporMode.Intense; attempt++) { await Task.Delay(10); }
            Assert.AreEqual(SessionVaporMode.Intense, (await new LocalProfileLibrary(root).LoadHeatingAsync(CancellationToken.None))[0].Profile.VaporMode);
            Assert.AreEqual(0, client.TotalStateChangingCalls);
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, true); } }
    }

    [TestMethod]
    public async Task LegacyFileRequiresExplicitChoiceWithoutLosingItsOldValue()
    {
        string root = Path.Combine(Path.GetTempPath(), "desk-puff-legacy-vapor", Guid.NewGuid().ToString("N"));
        try
        {
            LocalProfileLibrary library = new(root);
            await library.SaveHeatingAsync(new()
            {
                Name = "legacy",
                DeviceProfileName = "legacy",
                TargetTemperatureCelsius = 220,
                DurationSeconds = 60,
                Vapor = VaporLevel.Max,
                BoostTemperatureCelsius = 5,
                BoostDurationSeconds = 10,
                ColorProfileName = "blue",
                Colors = ["#0000FF"]
            }, null, CancellationToken.None);
            FakeDeviceClient client = new();
            await using SessionController controller = new(client, new DeviceSafetyPolicy());
            await using MainViewModel viewModel = new(controller, true, root);
            await viewModel.InitializeAsync(); viewModel.ApplyHeatingProfileCommand.Execute(null);
            Assert.IsNull(viewModel.EditorVaporMode);
            Assert.AreEqual(VaporLevel.Max, viewModel.EditorVapor);
            Assert.IsFalse(viewModel.StartStopCommand.CanExecute(null));
            StringAssert.Contains(viewModel.SavedSessionRestriction, "choose a vapor mode");
            viewModel.EditorVaporMode = SessionVaporMode.Bold; viewModel.SaveHeatingProfileCommand.Execute(null);
            for (int attempt = 0; attempt < 200 && viewModel.SavedHeatingProfiles[0].VaporMode is null; attempt++) { await Task.Delay(10); }
            LocalHeatingProfile saved = (await library.LoadHeatingAsync(CancellationToken.None))[0].Profile;
            Assert.AreEqual(VaporLevel.Max, saved.Vapor); Assert.AreEqual(SessionVaporMode.Bold, saved.VaporMode);
            Assert.IsTrue(viewModel.StartStopCommand.CanExecute(null));
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, true); } }
    }
}
