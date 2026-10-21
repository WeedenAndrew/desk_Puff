using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using DeskPuff.Core.Devices;

namespace DeskPuff.App.Tests;

[TestClass]
public sealed class NamedVaporRenderTests
{
    private static readonly string[] RequiredControls = ["SessionVaporSelector", "VaporCapabilityNotice", "SaveHeatingProfileButton", "ProfilesNavButton", "HomeNavButton", "ColorNavButton"];
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ActualModesAndLegacyChoiceFitDefaultWindow(bool legacy) => HeadlessRender.OnUiThread(async () =>
    {
        RenderedFrame frame = await HeadlessRender.CaptureAsync("named-vapor", viewModel =>
        {
            viewModel.ShowProfilesCommand.Execute(null);
            viewModel.EditorVaporMode = legacy ? null : SessionVaporMode.Extreme;
            viewModel.EditorVapor = VaporLevel.Max;
            return Task.CompletedTask;
        }, window =>
        {
            Control Find(string id) => window.GetVisualDescendants().OfType<Control>().Single(control => AutomationProperties.GetAutomationId(control) == id);
            ComboBox selector = (ComboBox)Find("SessionVaporSelector");
            CollectionAssert.AreEqual(Enum.GetValues<SessionVaporMode>(), selector.Items.Cast<SessionVaporMode>().ToArray());
            foreach (string id in RequiredControls)
            {
                Control control = Find(id); Point? point = control.TranslatePoint(default, window);
                Assert.IsNotNull(point);
                Assert.IsTrue(control.IsEffectivelyVisible && control.IsHitTestVisible, id);
                Assert.IsTrue(point.Value.Y >= 0 && point.Value.Y + control.Bounds.Height <= window.ClientSize.Height + 1, id);
            }
        });
        string output = Environment.GetEnvironmentVariable("DESK_PUFF_RENDER_OUTPUT") ?? Path.GetTempPath();
        Directory.CreateDirectory(output);
        frame.Save(Path.Combine(output, legacy ? "legacy-vapor-choice.png" : "named-vapor-selector.png"));
        return true;
    });
}
