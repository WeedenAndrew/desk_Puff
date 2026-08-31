using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DeskPuff.App.Devices;
using DeskPuff.App.ViewModels;
using DeskPuff.Bluetooth.Windows;
using DeskPuff.Core.Devices;
using DeskPuff.Core.Diagnostics;
using DeskPuff.Core.Safety;
using DeskPuff.Core.Sessions;

namespace DeskPuff.App;

public sealed partial class App : Application
{
    private FileDiagnosticLog? diagnosticLog;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            string[] arguments = desktop.Args ?? [];
            bool demoMode = arguments.Any(argument =>
                string.Equals(argument, "--demo", StringComparison.OrdinalIgnoreCase));
            bool traceWrites = arguments.Any(argument =>
                string.Equals(argument, "--trace-writes", StringComparison.OrdinalIgnoreCase));
            diagnosticLog = FileDiagnosticLog.CreateBesideExecutable();
            diagnosticLog.Write(
                $"APPLICATION START demoMode={demoMode} traceWrites={traceWrites}");
            IDeviceClient client = demoMode
                ? new DemoDeviceClient()
                : new LoraxDeviceClient(diagnosticLog, traceWrites);
            SessionController controller = new(client, new DeviceSafetyPolicy(), diagnosticLog);
            MainViewModel viewModel = new(
                controller,
                demoMode,
                profileLibraryRoot: null,
                sessionOverrides: client as ISessionOverrideClient,
                diagnostics: diagnosticLog);
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.MainWindow = new MainWindow(viewModel, diagnosticLog);
            desktop.Exit += (_, _) =>
            {
                diagnosticLog?.Write("APPLICATION EXIT");
                diagnosticLog?.Dispose();
                diagnosticLog = null;
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
