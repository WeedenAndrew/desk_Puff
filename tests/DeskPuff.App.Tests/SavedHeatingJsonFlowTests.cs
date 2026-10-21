using DeskPuff.App.ViewModels;
using DeskPuff.Core.Devices;
using DeskPuff.Core.Profiles;
using DeskPuff.Core.Safety;
using DeskPuff.Core.Sessions;

namespace DeskPuff.App.Tests;

[TestClass]
public sealed class SavedHeatingJsonFlowTests
{
    private string rootPath = string.Empty;

    [TestInitialize]
    public void Initialize() =>
        rootPath = Path.Combine(Path.GetTempPath(), "desk-puff-json-flow", Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(rootPath))
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UseProfile_FromSavedJson_StartsItsParametersWithoutWritingASlot(bool editAndSave)
    {
        LocalProfileLibrary library = new(rootPath);
        StoredLocalProfile<LocalHeatingProfile> stored = await library.SaveHeatingAsync(new()
        {
            Name = "Morning",
            DeviceProfileName = "Morning heat",
            TargetTemperatureCelsius = 210,
            DurationSeconds = 65,
            Vapor = VaporLevel.Standard,
            VaporMode = SessionVaporMode.Smooth,
            BoostTemperatureCelsius = 5,
            BoostDurationSeconds = 10,
            ColorProfileName = "Morning colors",
            Colors = ["#22FF88", "#1188FF"],
        }, existingFileName: null, CancellationToken.None);
        string jsonPath = Path.Combine(rootPath, "heating", stored.FileName);
        string originalJson = await File.ReadAllTextAsync(jsonPath);
        FakeDeviceClient client = new();
        RecordingOverrides overrides = new();
        await using SessionController controller = new(client, new DeviceSafetyPolicy(), sessionOverrides: overrides);
        await using MainViewModel viewModel = new(controller, demoMode: true, rootPath);
        await viewModel.InitializeAsync();
        Assert.HasCount(1, viewModel.SavedHeatingProfiles);

        await ExecuteAsync(viewModel, viewModel.ApplyHeatingProfileCommand);

        Assert.AreEqual(ProfileSource.Saved, viewModel.SelectedProfile!.Source,
            "USE PROFILE must select the loaded JSON for START, not merely populate the slot editor.");
        Assert.IsTrue(viewModel.SavedProfileCaptionVisibility);
        Assert.AreEqual(0, client.TotalStateChangingCalls, "Selecting JSON must not write or heat.");
        if (editAndSave)
        {
            if (viewModel.UseFahrenheit) { viewModel.ToggleTemperatureUnitCommand.Execute(null); }
            viewModel.EditorTemperature = 215;
            viewModel.EditorDurationSeconds = 70;
            await ExecuteAsync(viewModel, viewModel.SaveHeatingProfileCommand);
        }

        await ExecuteAsync(viewModel, viewModel.StartStopCommand);

        Assert.HasCount(1, overrides.Applied);
        Assert.AreEqual(editAndSave ? 215 : 210, overrides.Applied[0].TargetTemperatureCelsius);
        Assert.AreEqual(TimeSpan.FromSeconds(editAndSave ? 70 : 65), overrides.Applied[0].Duration);
        Assert.AreEqual(1, client.StartCallCount);
        Assert.AreEqual(0, client.UpdateProfileCallCount);
        Assert.AreEqual(0, client.SelectProfileCallCount);
        if (!editAndSave)
        {
            Assert.AreEqual(originalJson, await File.ReadAllTextAsync(jsonPath),
                "Using a saved profile must not rewrite its JSON.");
        }
    }

    private static async Task ExecuteAsync(MainViewModel viewModel, System.Windows.Input.ICommand command)
    {
        Assert.IsTrue(command.CanExecute(null));
        string before = viewModel.StatusText;
        command.Execute(null);
        for (int attempt = 0; attempt < 200 && viewModel.StatusText == before; attempt++)
        {
            await Task.Delay(10);
        }
        Assert.AreNotEqual(before, viewModel.StatusText, "The command did not complete.");
        Assert.IsFalse(viewModel.StatusIsError, viewModel.StatusText);
    }

    [TestMethod]
    [DataRow(5)]
    [DataRow(25)]
    [DataRow(32)]
    public async Task SaveHeatingJson_FromLongDevicePalette_CreatesReloadsAndRunsWithoutSlotWrite(int stopCount)
    {
        string[] colors = Enumerable.Range(0, stopCount).Select(index => $"#{index + 1:X6}").ToArray();
        FakeDeviceClient client = new();
        HeatProfile[] profiles = FakeDeviceClient.DefaultProfiles().ToArray();
        profiles[0] = profiles[0] with { ColorHex = colors[0], ColorPalette = colors, HasDeviceColor = true };
        client.SetProfiles(profiles);
        RecordingOverrides overrides = new();
        await using SessionController controller = new(client, new DeviceSafetyPolicy(), sessionOverrides: overrides);
        await using MainViewModel viewModel = new(controller, demoMode: true, rootPath);
        await viewModel.InitializeAsync();
        viewModel.ShowProfilesCommand.Execute(null);
        viewModel.HeatingProfileName = "Created from device";
        // A device's unread legacy value is not an actual-mode choice.
        viewModel.EditorVaporMode = SessionVaporMode.Smooth;

        Assert.IsTrue(viewModel.SaveHeatingProfileCommand.CanExecute(null),
            "A local JSON save must not inherit the device-write four-color limit.");
        Assert.IsFalse(viewModel.SaveProfileCommand.CanExecute(null), "Device write limits must remain unchanged.");
        await ExecuteAsync(viewModel, viewModel.SaveHeatingProfileCommand);

        LocalProfileLibrary library = new(rootPath);
        IReadOnlyList<StoredLocalProfile<LocalHeatingProfile>> saved = await library.LoadHeatingAsync(CancellationToken.None);
        Assert.HasCount(1, saved);
        CollectionAssert.AreEqual(colors, saved[0].Profile.Colors);
        Assert.AreEqual(0, client.TotalStateChangingCalls);
        await ExecuteAsync(viewModel, viewModel.ReloadLocalProfilesCommand);
        Assert.HasCount(1, viewModel.SavedHeatingProfiles);
        await ExecuteAsync(viewModel, viewModel.ApplyHeatingProfileCommand);
        await ExecuteAsync(viewModel, viewModel.StartStopCommand);
        Assert.HasCount(1, overrides.Applied);
        CollectionAssert.AreEqual(colors, overrides.Applied[0].Colors.ToArray());
        Assert.AreEqual(0, client.UpdateProfileCallCount);
        Assert.AreEqual(1, client.StartCallCount);
    }

    [TestMethod]
    public async Task DisconnectedDraft_CreatesBothJsonKindsAndRefreshesDisabledSaveAfterFieldEdits()
    {
        FakeDeviceClient client = new();
        await client.DisconnectAsync(CancellationToken.None);
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await using MainViewModel viewModel = new(controller, demoMode: false, rootPath);
        await viewModel.InitializeAsync();
        Assert.IsFalse(viewModel.IsConnected);
        Assert.IsTrue(viewModel.ShowProfilesCommand.CanExecute(null));
        viewModel.ShowProfilesCommand.Execute(null);
        Assert.IsTrue(viewModel.ProfilesVisibility);
        Assert.IsFalse(viewModel.ConnectionVisibility);
        viewModel.NewHeatingProfileCommand.Execute(null);
        int commandChanges = 0;
        viewModel.SaveHeatingProfileCommand.CanExecuteChanged += (_, _) => commandChanges++;
        viewModel.HeatingProfileName = " ";
        Assert.IsFalse(viewModel.SaveHeatingProfileCommand.CanExecute(null));
        StringAssert.Contains(viewModel.HeatingSaveValidation, "name");
        viewModel.HeatingProfileName = "Offline heat";
        viewModel.EditorDurationSeconds = 121;
        Assert.IsFalse(viewModel.SaveHeatingProfileCommand.CanExecute(null));
        StringAssert.Contains(viewModel.HeatingSaveValidation, "duration");
        viewModel.EditorDurationSeconds = 60;
        viewModel.EditorTemperature = double.NaN;
        Assert.IsFalse(viewModel.SaveHeatingProfileCommand.CanExecute(null));
        viewModel.EditorTemperature = viewModel.UseFahrenheit ? 500 : 260;
        Assert.IsTrue(commandChanges >= 5);
        await ExecuteAsync(viewModel, viewModel.SaveHeatingProfileCommand);
        Assert.IsFalse(viewModel.SaveProfileCommand.CanExecute(null));
        Assert.IsFalse(viewModel.ApplyHeatingProfileCommand.CanExecute(null));
        viewModel.NewHeatingProfileCommand.Execute(null);
        Assert.IsFalse(viewModel.EditHeatingProfileCommand.CanExecute(null));
        // NEW deselects the saved item; selecting the library entry makes editing available.
        viewModel.SelectedSavedHeatingProfile = viewModel.SavedHeatingProfiles[0];
        viewModel.EditHeatingProfileCommand.Execute(null);
        Assert.AreEqual("Offline heat", viewModel.HeatingProfileName);
        viewModel.ShowColorCommand.Execute(null);
        Assert.IsTrue(viewModel.ColorVisibility);
        viewModel.PaletteName = "";
        Assert.IsFalse(viewModel.SaveColorPaletteCommand.CanExecute(null));
        viewModel.PaletteName = "Offline colors";
        await ExecuteAsync(viewModel, viewModel.SaveColorPaletteCommand);
        viewModel.UseColorWithHeatingProfileCommand.Execute(null);
        Assert.IsTrue(viewModel.ProfilesVisibility);
        LocalProfileLibrary library = new(rootPath);
        Assert.HasCount(1, await library.LoadHeatingAsync(CancellationToken.None));
        Assert.HasCount(1, await library.LoadColorsAsync(CancellationToken.None));
        Assert.AreEqual(0, client.TotalStateChangingCalls);
    }

    private sealed class RecordingOverrides : ISessionOverrideClient
    {
        public List<SessionOverride> Applied { get; } = [];

        public Task ApplySessionOverrideAsync(SessionOverride request, CancellationToken cancellationToken)
        {
            Applied.Add(request);
            return Task.CompletedTask;
        }

        public Task ClearSessionOverrideAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
