using DeskPuff.App.ViewModels;

namespace DeskPuff.App.Tests;

/// <summary>
/// Renders each page of the interface once per test run and writes it to
/// <c>docs/media</c>. Pages are reached through the same public commands the
/// navigation bar binds to, so a page that stops switching is captured as it
/// really is rather than being poked into place.
/// </summary>
internal static class PageCaptures
{
    internal const string Home = "home";
    internal const string Profiles = "profiles";
    internal const string Color = "color";
    internal const string Settings = "settings";

    private static readonly object Gate = new();
    private static Dictionary<string, RenderedFrame>? frames;

    internal static IReadOnlyDictionary<string, RenderedFrame> All()
    {
        lock (Gate)
        {
            frames ??= Capture();
            return frames;
        }
    }

    internal static RenderedFrame Of(string page) => All()[page];

    internal static string MediaDirectory()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "desk_Puff.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.IsNotNull(directory, "Could not find the repository root from the test output directory.");
        return Path.Combine(directory.FullName, "docs", "media");
    }

    private static Dictionary<string, RenderedFrame> Capture()
    {
        string media = MediaDirectory();
        Dictionary<string, RenderedFrame> captured = [];
        foreach ((string page, Action<MainViewModel> show) in Pages())
        {
            RenderedFrame frame = HeadlessRender.OnUiThread(
                () => HeadlessRender.CaptureAsync(page, viewModel =>
                {
                    show(viewModel);
                    return Task.CompletedTask;
                }));
            frame.Save(Path.Combine(media, $"{page}.png"));
            captured[page] = frame;
        }

        return captured;
    }

    private static IEnumerable<(string Page, Action<MainViewModel> Show)> Pages()
    {
        // Home is where the view model lands after connecting, so it needs no
        // command; the others go through the navigation bar's own bindings.
        yield return (Home, _ => { });
        yield return (Profiles, viewModel => viewModel.ShowProfilesCommand.Execute(null));
        yield return (Color, viewModel => viewModel.ShowColorCommand.Execute(null));
        yield return (Settings, viewModel => viewModel.ShowSettingsCommand.Execute(null));
    }
}
