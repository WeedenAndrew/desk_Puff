using Avalonia;
using DeskPuff.Bluetooth.Windows;
using DeskPuff.Core.Diagnostics;

namespace DeskPuff.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (!AreArgumentsSupported(args))
        {
            return 2;
        }

        bool driveProbe = args.Any(argument => string.Equals(argument, "--probe-thc-drive", StringComparison.OrdinalIgnoreCase));
        bool idleProbe = args.Any(argument => string.Equals(argument, "--probe-thc", StringComparison.OrdinalIgnoreCase));
        bool mcProbe = args.Any(argument => string.Equals(argument, "--probe-thc-mc", StringComparison.OrdinalIgnoreCase));
        if (driveProbe || idleProbe || mcProbe)
        {
            using FileDiagnosticLog log = FileDiagnosticLog.CreateBesideExecutable();
            if ((Convert.ToInt32(driveProbe) + Convert.ToInt32(idleProbe) + Convert.ToInt32(mcProbe) != 1) || args.Any(argument =>
                !string.Equals(argument, "--probe-thc", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(argument, "--probe-thc-drive", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(argument, "--probe-thc-mc", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(argument, "--trace-writes", StringComparison.OrdinalIgnoreCase)))
            {
                log.Write("THC PROBE refused: choose one probe mode, with optional --trace-writes only.");
                return 1;
            }
            bool traceWrites = args.Any(argument => string.Equals(argument, "--trace-writes", StringComparison.OrdinalIgnoreCase));
            using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(driveProbe || mcProbe ? 10 : 2));
            return (mcProbe
                ? ThcDiagnosticProbe.RunMcAsync(log, traceWrites, timeout.Token)
                : driveProbe
                ? ThcDiagnosticProbe.RunDriveAsync(log, traceWrites, timeout.Token)
                : ThcDiagnosticProbe.RunAsync(log, traceWrites, timeout.Token)).GetAwaiter().GetResult();
        }
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    internal static bool AreArgumentsSupported(IEnumerable<string> args) => args.All(argument =>
        string.Equals(argument, "--trace-writes", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(argument, "--probe-thc", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(argument, "--probe-thc-drive", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(argument, "--probe-thc-mc", StringComparison.OrdinalIgnoreCase));

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
