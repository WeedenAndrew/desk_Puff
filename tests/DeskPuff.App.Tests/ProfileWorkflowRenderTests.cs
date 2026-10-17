using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using DeskPuff.App.ViewModels;

namespace DeskPuff.App.Tests;

[TestClass]
public sealed class ProfileWorkflowRenderTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void LocalPages_SaveAndNavigationFitDefaultWindow(bool colors) =>
        HeadlessRender.OnUiThread(async () =>
        {
            string page = colors ? "color" : "profiles";
            RenderedFrame frame = await HeadlessRender.CaptureAsync(page, viewModel =>
            {
                (colors ? viewModel.ShowColorCommand : viewModel.ShowProfilesCommand).Execute(null);
                return Task.CompletedTask;
            }, window =>
            {
                AssertVisibleWithinWindow(window, colors ? "SaveColorPaletteButton" : "SaveHeatingProfileButton");
                AssertVisibleWithinWindow(window, colors ? "PaletteNameEditor" : "HeatingProfileNameEditor");
                AssertVisibleWithinWindow(window, "ProfilesNavButton");
                AssertVisibleWithinWindow(window, "ColorNavButton");
            });
            frame.Save(Path.Combine(PageCaptures.MediaDirectory(), $"{page}-default.png"));
            return true;
        });

    [TestMethod]
    public void Profiles_InvalidDraftAndPopulatedLibraryRender() =>
        HeadlessRender.OnUiThread(async () =>
        {
            RenderedFrame invalid = await HeadlessRender.CaptureAsync("profiles-invalid", viewModel =>
            {
                viewModel.ShowProfilesCommand.Execute(null);
                viewModel.HeatingProfileName = "";
                Assert.IsFalse(viewModel.SaveHeatingProfileCommand.CanExecute(null));
                return Task.CompletedTask;
            });
            invalid.Save(Path.Combine(PageCaptures.MediaDirectory(), "profiles-invalid.png"));
            RenderedFrame populated = await HeadlessRender.CaptureAsync("profiles-library", async viewModel =>
            {
                viewModel.ShowProfilesCommand.Execute(null);
                viewModel.HeatingProfileName = "Evening heat";
                viewModel.SaveHeatingProfileCommand.Execute(null);
                for (int attempt = 0; attempt < 200 && viewModel.SavedHeatingProfiles.Count == 0; attempt++)
                {
                    await Task.Delay(10);
                }
                Assert.HasCount(1, viewModel.SavedHeatingProfiles);
            });
            populated.Save(Path.Combine(PageCaptures.MediaDirectory(), "profiles-library.png"));
            return true;
        });

    private static void AssertVisibleWithinWindow(MainWindow window, string automationId)
    {
        Control control = window.GetVisualDescendants().OfType<Control>()
            .Single(control => AutomationProperties.GetAutomationId(control) == automationId);
        Assert.IsTrue(control.IsEffectivelyVisible, automationId);
        Point? origin = control.TranslatePoint(default, window);
        Assert.IsNotNull(origin, automationId);
        Assert.IsTrue(origin.Value.X >= 0 && origin.Value.Y >= 0 &&
            origin.Value.X + control.Bounds.Width <= HeadlessRender.WindowWidth &&
            origin.Value.Y + control.Bounds.Height <= HeadlessRender.WindowHeight, automationId);
        Assert.IsGreaterThan(0d, control.Bounds.Width, automationId);
        Assert.IsGreaterThan(0d, control.Bounds.Height, automationId);
    }
}
