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
                    ColorwayName = "DISCO",
                    LampName = "pikaled2",
                },
                .. profiles.Skip(1),
            ]);
            await using SessionController controller = new(client, new DeviceSafetyPolicy());
            await using MainViewModel viewModel = new(controller, demoMode: true, rootPath);
            await viewModel.InitializeAsync();
            viewModel.ShowColorCommand.Execute(null);

            Assert.AreEqual("COLOR 1 OF 25", viewModel.ColorStopPositionText);
            Assert.AreEqual(DevicePalette[0], viewModel.WheelColor);
            Assert.AreEqual("DISCO", viewModel.CurrentColorProfileName);
            StringAssert.Contains(viewModel.ColorPageContextText, "pikaled2");
            Assert.HasCount(33, viewModel.EditorPaletteBrush.GradientStops);

            viewModel.SelectColorStopAtFraction(0.75);

            Assert.AreEqual("COLOR 19 OF 25", viewModel.ColorStopPositionText);
            Assert.AreEqual(DevicePalette[18], viewModel.WheelColor);

            viewModel.WheelColor = "#123456";

            Assert.AreEqual("#123456", viewModel.WheelColor);
            Assert.AreEqual("CUSTOM COLORWAY", viewModel.CurrentColorProfileName);
            viewModel.PreviousColorStopCommand.Execute(null);
            Assert.AreEqual(DevicePalette[17], viewModel.WheelColor);
            viewModel.NextColorStopCommand.Execute(null);
            Assert.AreEqual("#123456", viewModel.WheelColor);
            Assert.IsFalse(viewModel.SaveProfileCommand.CanExecute(null));
            Assert.IsFalse(viewModel.SaveHeatingProfileCommand.CanExecute(null));
            Assert.AreEqual(0, client.TotalStateChangingCalls);
            return true;
        });

    [TestMethod]
    public void PerceptualDisplayInterpolation_StaysSaturatedAndLeavesAnchorsUnchanged() =>
        HeadlessRender.OnUiThread(() =>
        {
            string[] anchors = ["#FF00FF", "#00FF00"];
            string[] original = [.. anchors];

            LinearGradientBrush brush = PalettePresentation.Sweep(anchors);
            Color midpoint = brush.GradientStops[16].Color;
            int maximum = Math.Max(midpoint.R, Math.Max(midpoint.G, midpoint.B));
            int minimum = Math.Min(midpoint.R, Math.Min(midpoint.G, midpoint.B));
            double saturation = maximum == 0 ? 0 : (maximum - minimum) / (double)maximum;

            Assert.IsGreaterThan(0.75, saturation, "A midpoint between saturated hues must not collapse toward gray.");
            CollectionAssert.AreEqual(original, anchors, "Display interpolation must not alter stored anchors.");
            Assert.HasCount(33, brush.GradientStops);
            return Task.FromResult(true);
        });

    [TestMethod]
    public void PerceptualRingSegments_JoinAndLeaveThePaletteUnchanged() =>
        HeadlessRender.OnUiThread(() =>
        {
            string[] original = [.. DevicePalette];
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
                Assert.HasCount(9, current.GradientStops);
            }

            CollectionAssert.AreEqual(original, DevicePalette);

            return Task.FromResult(true);
        });
}
