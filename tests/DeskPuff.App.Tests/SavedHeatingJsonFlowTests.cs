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
