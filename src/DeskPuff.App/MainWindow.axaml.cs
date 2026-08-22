using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using DeskPuff.App.ViewModels;

namespace DeskPuff.App;

public sealed partial class MainWindow : Window
{
    private readonly MainViewModel viewModel;
    private bool closing;

    internal MainWindow(MainViewModel viewModel)
    {
        AvaloniaXamlLoader.Load(this);
        this.viewModel = viewModel;
        DataContext = viewModel;
        Opened += WindowOpened;
        Closing += WindowClosing;
        AddHandler(KeyDownEvent, WindowKeyDown, RoutingStrategies.Tunnel);
    }

    private async void WindowOpened(object? sender, EventArgs e)
    {
        try
        {
            await viewModel.InitializeAsync();
        }
        catch (Exception exception)
        {
            await ShowInitializationErrorAsync(exception.Message);
        }
    }

    private async void WindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (closing)
        {
            return;
        }

        e.Cancel = true;
        closing = true;
        IsEnabled = false;
        await viewModel.DisposeAsync();
        Close();
    }

    private void TitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        PointerPoint point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            e.Handled = true;
            return;
        }

        BeginMoveDrag(e);
    }

    private async void WindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        e.Handled = await viewModel.HandleShortcutAsync(e.Key);
    }

    private void MinimizeButtonClick(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void MaximizeButtonClick(object? sender, RoutedEventArgs e)
    {
        ToggleMaximize();
        Button? maximize = this.FindControl<Button>("MaximizeButton");
        if (maximize is not null)
        {
            bool maximized = WindowState == WindowState.Maximized;
            maximize.Content = maximized ? "❐" : "□";
            ToolTip.SetTip(maximize, maximized ? "Restore" : "Maximize");
        }
    }

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void CloseButtonClick(object? sender, RoutedEventArgs e) => Close();

    private async Task ShowInitializationErrorAsync(string message)
    {
        Window dialog = new()
        {
            Title = "desk_Puff could not initialize",
            Width = 360,
            Height = 190,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.Parse("#161920")),
        };
        Button close = new()
        {
            Content = "Close",
            HorizontalAlignment = HorizontalAlignment.Right,
            MinWidth = 84,
        };
        close.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(22),
            Spacing = 18,
            Children =
            {
                new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                },
                close,
            },
        };
        await dialog.ShowDialog(this);
    }
}
