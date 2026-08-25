using Avalonia;

namespace DeskPuff.App.Tests;

/// <summary>
/// Renders the interface and looks at the pixels. Every other test in this
/// project can pass while the window draws nothing, because none of them render
/// a frame: a bare <c>ContentPresenter</c> once left the session button showing
/// its ring and no content at all, and the suite stayed green.
/// </summary>
/// <remarks>
/// The thresholds are deliberately far below what the interface actually
/// measures. They exist to catch "nothing rendered", not to police layout. A
/// pixel-exact test would be deleted the first time a margin moved by two.
/// </remarks>
[TestClass]
public sealed class InterfaceRenderTests
{
    /// <summary>Measured: home 2980, profiles 1916, color 4727, settings 1632.</summary>
    private const int MinimumFrameColors = 64;

    /// <summary>
    /// Measured: 251 with the content present, and exactly 1 with the session
    /// button's ContentPresenter left bare, which is the bug this guards.
    /// </summary>
    private const int MinimumCircleInteriorColors = 32;

    /// <summary>A page that never switched is identical, scoring zero.</summary>
    private const double MinimumPageDifference = 0.05;

    /// <summary>
    /// Inside the session ring and clear of the ring itself. The ring is centred
    /// near (229, 323) with an inner edge around radius 94, so this square's
    /// corners sit at 85 and never sample the ring. Getting this wrong is not
    /// academic: a larger box catches the ring's own gradient and reports a
    /// healthy colour count for a button that is drawing nothing inside.
    /// </summary>
    private static readonly PixelRect CircleInterior = new(169, 263, 120, 120);

    /// <summary>Bands the design leaves empty; the control for the numbers above.</summary>
    private static readonly PixelRect KnownEmptyBand = new(0, 152, 460, 48);

    [TestMethod]
    public void EveryPage_RendersSomethingRatherThanAFlatFrame()
    {
        foreach ((string page, RenderedFrame frame) in Ordered())
        {
            int colors = frame.DistinctColors(frame.Everything);
            Console.WriteLine($"{page,-9} {colors,5} colours");
            Assert.AreEqual(HeadlessRender.WindowWidth, frame.Width, $"{page} is the wrong width.");
            Assert.AreEqual(HeadlessRender.WindowHeight, frame.Height, $"{page} is the wrong height.");
            Assert.IsGreaterThanOrEqualTo(
                MinimumFrameColors,
                colors,
                $"The {page} frame has {colors} distinct colours. A frame the renderer " +
                "never drew into is one flat colour; this one is close to blank.");
        }
    }

    [TestMethod]
    public void TheHomeCircle_HasContentInsideItsRing()
    {
        RenderedFrame home = PageCaptures.Of(PageCaptures.Home);
        int inside = home.DistinctColors(CircleInterior);
        int empty = home.DistinctColors(KnownEmptyBand);
        Console.WriteLine($"circle interior {inside} colours, known-empty band {empty} colours");

        Assert.IsGreaterThanOrEqualTo(
            MinimumCircleInteriorColors,
            inside,
            $"The middle of the session circle has {inside} distinct colours. The ring " +
            "can render while everything inside it is missing, which is exactly what a " +
            "bare ContentPresenter in the button's template produced.");
        Assert.IsGreaterThan(
            empty,
            inside,
            "The middle of the circle is no busier than a band the design leaves empty.");
    }

    [TestMethod]
    public void EveryPage_LooksDifferentFromEveryOther()
    {
        (string Page, RenderedFrame Frame)[] frames = [.. Ordered()];
        for (int first = 0; first < frames.Length; first++)
        {
            for (int second = first + 1; second < frames.Length; second++)
            {
                double difference = frames[first].Frame.DifferenceFrom(frames[second].Frame);
                Console.WriteLine(
                    $"{frames[first].Page,-9} vs {frames[second].Page,-9} {difference,7:P2}");
                Assert.IsGreaterThanOrEqualTo(
                    MinimumPageDifference,
                    difference,
                    $"{frames[first].Page} and {frames[second].Page} render almost the same " +
                    "frame, so one of them did not switch.");
            }
        }
    }

    [TestMethod]
    public void EveryPage_IsWrittenToDocsMedia()
    {
        string media = PageCaptures.MediaDirectory();
        foreach ((string page, _) in Ordered())
        {
            string path = Path.Combine(media, $"{page}.png");
            Assert.IsTrue(File.Exists(path), $"No file at {path}.");
            long size = new FileInfo(path).Length;
            Console.WriteLine($"{page,-9} {size,7} bytes  {path}");
            Assert.IsGreaterThan(2048, size, $"{path} is too small to be a rendered page.");
        }
    }

    private static IEnumerable<(string Page, RenderedFrame Frame)> Ordered() =>
        PageCaptures.All()
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => (entry.Key, entry.Value));
}
