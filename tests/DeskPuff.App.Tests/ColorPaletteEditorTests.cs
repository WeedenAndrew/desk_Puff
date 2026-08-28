using Avalonia.Media;
using DeskPuff.App.ViewModels;
using DeskPuff.Core.Devices;
using DeskPuff.Core.Safety;
using DeskPuff.Core.Sessions;

namespace DeskPuff.App.Tests;

[TestClass]
public sealed class ColorPaletteEditorTests
{
    private static readonly string[] DevicePalette =
    [
        "#7B07FF", "#6F4FEC", "#5F8AD7", "#4CC1C7", "#2CEDB5",
        "#07FFAB", "#07F9B6", "#07E9CE", "#07D6E6", "#07C6F8",
        "#07BFFF", "#72B2F2", "#BC92D3", "#E667AD", "#FA358C",
        "#FF077D", "#FF0D8B", "#FF14A9", "#FF16CC", "#FF0FEA",
        "#FF07F7", "#F307F6", "#D707F5", "#B207F6", "#8E07FB",
    ];

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
    public void TwentyFiveStopDeviceRamp_ClickSelectsNearestStopAndWheelEditsOnlyThatStop() =>
        HeadlessRender.OnUiThread(async () =>
        {
            FakeDeviceClient client = new();
            IReadOnlyList<HeatProfile> profiles = FakeDeviceClient.DefaultProfiles();
            client.SetProfiles(
            [
                profiles[0] with
                {
                    ColorHex = DevicePalette[0],
                    ColorPalette = DevicePalette,
                    HasDeviceColor = true,
                },
                .. profiles.Skip(1),
            ]);
            await using SessionController controller = new(client, new DeviceSafetyPolicy());
            await using MainViewModel viewModel = new(controller, demoMode: true, rootPath);
            await viewModel.InitializeAsync();
            viewModel.ShowColorCommand.Execute(null);

            Assert.AreEqual("COLOR 1 OF 25", viewModel.ColorStopPositionText);
            Assert.AreEqual(DevicePalette[0], viewModel.WheelColor);
            Assert.HasCount(25, viewModel.EditorPaletteBrush.GradientStops);

            viewModel.SelectColorStopAtFraction(0.75);

            Assert.AreEqual("COLOR 19 OF 25", viewModel.ColorStopPositionText);
            Assert.AreEqual(DevicePalette[18], viewModel.WheelColor);

            viewModel.WheelColor = "#123456";

            Assert.AreEqual("#123456", viewModel.WheelColor);
            Assert.AreEqual(
                Color.Parse("#123456"),
                viewModel.EditorPaletteBrush.GradientStops[18].Color);
            Assert.AreEqual(
                Color.Parse(DevicePalette[17]),
                viewModel.EditorPaletteBrush.GradientStops[17].Color);
            Assert.IsFalse(viewModel.SaveProfileCommand.CanExecute(null));
            Assert.IsFalse(viewModel.SaveHeatingProfileCommand.CanExecute(null));
            Assert.AreEqual(0, client.TotalStateChangingCalls);
            return true;
        });

    [TestMethod]
    public void TwentyFiveStopRingSegments_JoinWithTheSameColorAndRetainEveryStop() =>
        HeadlessRender.OnUiThread(() =>
        {
            LinearGradientBrush[] arcs =
            {
                PalettePresentation.RingSegment(DevicePalette, 0, 0, 0, 1, 1),
                PalettePresentation.RingSegment(DevicePalette, 1, 1, 0, 0, 1),
                PalettePresentation.RingSegment(DevicePalette, 2, 1, 1, 0, 0),
                PalettePresentation.RingSegment(DevicePalette, 3, 0, 1, 1, 0),
            };

            for (int index = 0; index < arcs.Length; index++)
            {
                LinearGradientBrush current = arcs[index];
                LinearGradientBrush next = arcs[(index + 1) % arcs.Length];
                Assert.AreEqual(
                    current.GradientStops[^1].Color,
                    next.GradientStops[0].Color,
                    $"Ring segments {index + 1} and {(index + 1) % arcs.Length + 1} must share a boundary color.");
            }

            HashSet<Color> renderedStops = arcs
                .SelectMany(arc => arc.GradientStops)
                .Select(stop => stop.Color)
                .ToHashSet();
            foreach (string color in DevicePalette)
            {
                Assert.IsTrue(renderedStops.Contains(Color.Parse(color)), $"The ring omitted device stop {color}.");
            }

            return Task.FromResult(true);
        });
}
