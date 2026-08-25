using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DeskPuff.App.Devices;
using DeskPuff.App.ViewModels;
using DeskPuff.Bluetooth.Windows;
using DeskPuff.Core.Devices;
using DeskPuff.Core.Safety;
using DeskPuff.Core.Sessions;

namespace DeskPuff.App;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            bool demoMode = desktop.Args?.Any(argument =>
                string.Equals(argument, "--demo", StringComparison.OrdinalIgnoreCase)) == true;
            IDeviceClient client = demoMode ? new DemoDeviceClient() : new LoraxDeviceClient();
            SessionController controller = new(client, new DeviceSafetyPolicy());
            MainViewModel viewModel = new(
                controller,
                demoMode,
                profileLibraryRoot: null,
                sessionOverrides: client as ISessionOverrideClient);
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.MainWindow = new MainWindow(viewModel);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
