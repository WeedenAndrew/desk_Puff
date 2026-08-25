using DeskPuff.App.Devices;
using DeskPuff.App.ViewModels;
using DeskPuff.Core.Devices;
using DeskPuff.Core.Safety;
using DeskPuff.Core.Sessions;

namespace DeskPuff.App.Tests;

/// <summary>
/// Running a saved profile applies its parameters for one session. It is not a
/// profile-slot write, and these hold that line: no UpdateProfile call ever
/// reaches the device, and the device's own slots come back untouched.
/// </summary>
[TestClass]
public sealed class SessionOverrideTests
{
    private static readonly string[] SavedColors = ["#22FF88", "#1188FF"];

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
    public async Task RunningASavedProfile_AppliesItsParametersWithoutWritingASlot()
    {
        RecordingOverrideClient overrides = new();
        FakeDeviceClient client = new();
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await using MainViewModel viewModel = new(controller, demoMode: true, rootPath, overrides);
        await ConnectAsync(viewModel, Saved("Morning", celsius: 210, durationSeconds: 65));
        await viewModel.SelectProfileAtAsync(4, CancellationToken.None);

        await StartAsync(viewModel);

        Assert.HasCount(1, overrides.Applied);
        SessionOverride applied = overrides.Applied[0];
        Assert.AreEqual("Morning", applied.Name);
        Assert.AreEqual(210, applied.TargetTemperatureCelsius);
        Assert.AreEqual(TimeSpan.FromSeconds(65), applied.Duration);
        Assert.AreEqual(VaporLevel.Standard, applied.Vapor);
        Assert.AreEqual(1, client.StartCallCount);
        Assert.AreEqual(
            0,
            client.UpdateProfileCallCount,
            "Running a profile must never write a profile slot.");
        Assert.AreEqual(
            0,
            client.SelectProfileCallCount,
            "Running a profile must not move the device onto a different slot either.");
    }

    [TestMethod]
    public async Task RunningASavedProfile_CarriesVaporAndColourTheBoostPathCannot()
    {
        RecordingOverrideClient overrides = new();
        FakeDeviceClient client = new();
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await using MainViewModel viewModel = new(controller, demoMode: true, rootPath, overrides);
        await ConnectAsync(viewModel, Saved("Evening", vapor: VaporLevel.Max));
        await viewModel.SelectProfileAtAsync(4, CancellationToken.None);

        await StartAsync(viewModel);

        SessionOverride applied = overrides.Applied[0];
        Assert.AreEqual(VaporLevel.Max, applied.Vapor);
        CollectionAssert.AreEqual(SavedColors, applied.Colors.ToArray());
    }

    [TestMethod]
    public async Task StartingOnADeviceSlot_AppliesNoOverrideAtAll()
    {
        RecordingOverrideClient overrides = new();
        FakeDeviceClient client = new();
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await using MainViewModel viewModel = new(controller, demoMode: true, rootPath, overrides);
        await ConnectAsync(viewModel, Saved("Morning"));

        await StartAsync(viewModel);

        Assert.HasCount(0, overrides.Applied);
        Assert.AreEqual(1, client.StartCallCount);
    }

    [TestMethod]
    public async Task StoppingASession_DropsTheOverride()
    {
        RecordingOverrideClient overrides = new();
        FakeDeviceClient client = new();
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await using MainViewModel viewModel = new(controller, demoMode: true, rootPath, overrides);
        await ConnectAsync(viewModel, Saved("Morning"));
        await viewModel.SelectProfileAtAsync(4, CancellationToken.None);
        await StartAsync(viewModel);

        client.BeginHeating(200, TimeSpan.FromSeconds(40), TimeSpan.Zero);
        viewModel.ApplySnapshot(client.Snapshot);
        await StartAsync(viewModel);

        Assert.AreEqual(1, client.StopCallCount);
        Assert.AreEqual(1, overrides.ClearCount);
    }

    // ---- the ceilings ------------------------------------------------------

    [TestMethod]
    public async Task AProfileAboveSixHundredFahrenheit_IsRefusedAndNothingStarts()
    {
        RecordingOverrideClient overrides = new();
        FakeDeviceClient client = new();
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await using MainViewModel viewModel = new(controller, demoMode: true, rootPath, overrides);
        // 320 C is 608 F: inside the safety policy's 327 C envelope, above ours.
        await ConnectAsync(viewModel, Saved("Scorcher", celsius: 320));
        await viewModel.SelectProfileAtAsync(4, CancellationToken.None);

        await StartAsync(viewModel);

        Assert.HasCount(0, overrides.Applied);
        Assert.AreEqual(0, client.StartCallCount, "A refused profile must not start a session.");
        StringAssert.Contains(viewModel.StatusText, "600");
    }

    [TestMethod]
    public async Task AProfileAtSixHundredFahrenheit_IsStillAllowed()
    {
        RecordingOverrideClient overrides = new();
        FakeDeviceClient client = new();
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await using MainViewModel viewModel = new(controller, demoMode: true, rootPath, overrides);
        // 315.5 C is a shade under 600 F.
        await ConnectAsync(viewModel, Saved("Hot", celsius: 315.5));
        await viewModel.SelectProfileAtAsync(4, CancellationToken.None);

        await StartAsync(viewModel);

        Assert.HasCount(1, overrides.Applied);
        Assert.AreEqual(1, client.StartCallCount);
    }

    [TestMethod]
    public async Task AProfileLongerThanTwoMinutes_IsRefusedBeforeItCanBecomeTheSelection()
    {
        RecordingOverrideClient overrides = new();
        FakeDeviceClient client = new();
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await using MainViewModel viewModel = new(controller, demoMode: true, rootPath, overrides);
        await ConnectAsync(viewModel, Saved("Marathon", durationSeconds: 121));

        // The 2:00 session ceiling is the safety policy's own absolute maximum,
        // so it is enforced before the profile can even become the selection.
        await Assert.ThrowsExactlyAsync<DeviceSafetyException>(
            () => viewModel.SelectProfileAtAsync(4, CancellationToken.None));

        await StartAsync(viewModel);

        Assert.HasCount(0, overrides.Applied);
    }

    [TestMethod]
    public async Task AProfileAtExactlyTwoMinutes_IsAllowed()
    {
        RecordingOverrideClient overrides = new();
        FakeDeviceClient client = new();
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await using MainViewModel viewModel = new(controller, demoMode: true, rootPath, overrides);
        await ConnectAsync(viewModel, Saved("TwoMinutes", durationSeconds: 120));
        await viewModel.SelectProfileAtAsync(4, CancellationToken.None);

        await StartAsync(viewModel);

        Assert.HasCount(1, overrides.Applied);
        Assert.AreEqual(TimeSpan.FromMinutes(2), overrides.Applied[0].Duration);
    }

    // ---- hardware that cannot do this --------------------------------------

    [TestMethod]
    public async Task WithoutAnOverrideCapableClient_RunningIsRefusedRatherThanFaked()
    {
        FakeDeviceClient client = new();
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await using MainViewModel viewModel = new(controller, demoMode: true, rootPath);
        await ConnectAsync(viewModel, Saved("Morning"));
        await viewModel.SelectProfileAtAsync(4, CancellationToken.None);

        await StartAsync(viewModel);

        Assert.AreEqual(
            0,
            client.StartCallCount,
            "Without a session override path the app must refuse, not start the device's own slot.");
        StringAssert.Contains(viewModel.StatusText, "awaits hardware validation");
    }

    private static HeatingProfileOption Saved(
        string name,
        double celsius = 220,
        double durationSeconds = 40,
        VaporLevel vapor = VaporLevel.Standard) =>
        new(
            name,
            "Balanced",
            celsius,
            durationSeconds,
            vapor,
            10,
            10,
            "Custom colorway",
            SavedColors);

    // Goes through the command so a refusal lands in StatusText the way it does
    // for the user, rather than surfacing as an exception the test swallows.
    private static async Task StartAsync(MainViewModel viewModel)
    {
        string before = viewModel.StatusText;
        viewModel.StartStopCommand.Execute(null);
        for (int attempt = 0;
            attempt < 200 && string.Equals(viewModel.StatusText, before, StringComparison.Ordinal);
            attempt++)
        {
            await Task.Delay(10);
        }
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

    private sealed class RecordingOverrideClient : ISessionOverrideClient
    {
        public List<SessionOverride> Applied { get; } = [];

        public int ClearCount { get; private set; }

        public Task ApplySessionOverrideAsync(
            SessionOverride sessionOverride,
            CancellationToken cancellationToken)
        {
            Applied.Add(sessionOverride);
            return Task.CompletedTask;
        }

        public Task ClearSessionOverrideAsync(CancellationToken cancellationToken)
        {
            ClearCount++;
            return Task.CompletedTask;
        }
    }
}
