using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DeskPuff.App.Devices;
using DeskPuff.App.ViewModels;
using DeskPuff.Core.Safety;
using DeskPuff.Core.Sessions;

namespace DeskPuff.App.Tests;

/// <summary>
/// Renders the real window offscreen so a test can look at pixels. The app is
/// built from the same pair <c>App.OnFrameworkInitializationCompleted</c> uses
/// for <c>--demo</c>, so nothing here opens Bluetooth or addresses hardware.
/// </summary>
internal static class HeadlessRender
{
    internal const int WindowWidth = 460;
    internal const int WindowHeight = 760;

    private static readonly object Gate = new();
    private static bool started;

    /// <summary>
    /// Brings up one Avalonia instance for the whole test run, on its own
    /// thread with a live dispatcher. Avalonia allows exactly one per process.
    /// </summary>
    internal static void EnsureStarted()
    {
        lock (Gate)
        {
            if (started)
            {
                return;
            }

            using ManualResetEventSlim ready = new(false);
            Thread thread = new(() =>
            {
                AppBuilder.Configure<App>()
                    .UseSkia()
                    .UseHeadless(new AvaloniaHeadlessPlatformOptions
                    {
                        // Skia, not the stub renderer: the stub produces a frame
                        // with nothing drawn into it, which is the exact failure
                        // this harness exists to catch.
                        UseHeadlessDrawing = false,
                    })
                    .SetupWithoutStarting();
                ready.Set();
                Dispatcher.UIThread.MainLoop(CancellationToken.None);
            })
            {
                IsBackground = true,
                Name = "desk-puff-headless-ui",
            };
            thread.Start();
            ready.Wait();
            started = true;
        }
    }

    /// <summary>Runs work on the UI thread and waits for it from the test thread.</summary>
    internal static T OnUiThread<T>(Func<Task<T>> work)
    {
        EnsureStarted();
        return Dispatcher.UIThread.InvokeAsync(work).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Opens the window on the demo client, waits for the view model to finish
    /// connecting, then hands it to the caller to drive before capture.
    /// </summary>
    internal static async Task<RenderedFrame> CaptureAsync(
        string name,
        Func<MainViewModel, Task>? drive = null)
    {
        // An empty library of its own, not the machine's demo profiles: a
        // capture that changes with whatever someone saved last is not a test.
        string library = Path.Combine(
            Path.GetTempPath(),
            "desk-puff-render",
            Guid.NewGuid().ToString("N"));
        DemoDeviceClient client = new();
        await using SessionController controller = new(client, new DeviceSafetyPolicy());
        await using MainViewModel viewModel = new(
            controller,
            demoMode: true,
            profileLibraryRoot: library,
            sessionOverrides: client);
        MainWindow window = new(viewModel);
        window.Width = WindowWidth;
        window.Height = WindowHeight;

        try
        {
            window.Show();

            // The window's Opened handler kicks off initialization; wait for the
            // demo device to be connected rather than guessing at a delay.
            await SettleAsync(window, () => viewModel.IsConnected);

            if (drive is not null)
            {
                await drive(viewModel);
                await SettleAsync(window, () => true);
            }

            WriteableBitmap? frame = window.CaptureRenderedFrame();
            Assert.IsNotNull(frame, $"The headless renderer produced no frame for {name}.");
            return new RenderedFrame(name, frame);
        }
        finally
        {
            window.Close();
            if (Directory.Exists(library))
            {
                Directory.Delete(library, recursive: true);
            }
        }
    }

    /// <summary>
    /// Pumps the dispatcher, the layout pass and the render timer together until
    /// the condition holds, then a few more times so the last layout reaches the
    /// framebuffer. A capture taken mid-pass looks like a rendering bug.
    /// </summary>
    private static async Task SettleAsync(Window window, Func<bool> until)
    {
        for (int attempt = 0; attempt < 300; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            window.Measure(new Size(WindowWidth, WindowHeight));
            window.Arrange(new Rect(0, 0, WindowWidth, WindowHeight));
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            if (until())
            {
                break;
            }

            await Task.Delay(10);
        }

        for (int settle = 0; settle < 5; settle++)
        {
            Dispatcher.UIThread.RunJobs();
            window.Measure(new Size(WindowWidth, WindowHeight));
            window.Arrange(new Rect(0, 0, WindowWidth, WindowHeight));
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(10);
        }
    }
}
