using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.VisualTree;
using DeskPuff.App.ViewModels;

namespace DeskPuff.App;

public sealed partial class MainWindow : Window
{
    private readonly MainViewModel viewModel;
    private bool closing;
    private ListBoxItem? pressedProfileChip;
    private Point profileDragOrigin;
    private double profileDragStartOffset;
    private bool profileDragging;
    private bool profileDragMoved;

    /// <summary>
    /// Required by the Avalonia XAML compiler, which needs a public
    /// parameterless constructor on any type carrying <c>x:Class</c>.
    /// </summary>
    /// <remarks>
    /// Never used at runtime. <see cref="App"/> always constructs the window
    /// with a view model. This overload loads the XAML so the previewer has
    /// something to render, and deliberately does not subscribe the lifecycle
    /// handlers: without a view model there is nothing for them to act on, and
    /// wiring them would give a half-live window that fails on first event.
    /// </remarks>
    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        viewModel = null!;
    }

    internal MainWindow(MainViewModel viewModel)
    {
        AvaloniaXamlLoader.Load(this);
        this.viewModel = viewModel;
        DataContext = viewModel;
        Opened += WindowOpened;
        Closing += WindowClosing;
        AddHandler(KeyDownEvent, WindowKeyDown, RoutingStrategies.Tunnel);
        WireProfileStripDragging();
    }

    /// <summary>
    /// Lets the profile queue be dragged sideways as well as tapped. The press
    /// is taken in the tunnel so a drag never reaches the item underneath:
    /// selecting a device slot asks the device to change slots, and that must
    /// not happen just because someone scrolled the strip. A press that turns
    /// out to be a tap selects on release instead.
    /// </summary>
    private void WireProfileStripDragging()
    {
        ListBox? strip = this.FindControl<ListBox>("HomeProfileStrip");
        if (strip is null)
        {
            return;
        }

        strip.AddHandler(PointerPressedEvent, ProfileStripPointerPressed, RoutingStrategies.Tunnel);
        strip.AddHandler(PointerMovedEvent, ProfileStripPointerMoved, RoutingStrategies.Tunnel);
        strip.AddHandler(PointerReleasedEvent, ProfileStripPointerReleased, RoutingStrategies.Tunnel);
    }

    private void ProfileStripPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ListBox strip ||
            !e.GetCurrentPoint(strip).Properties.IsLeftButtonPressed)
        {
            return;
        }

        ScrollViewer? scroll = ProfileStripScroll(strip);
        if (scroll is null)
        {
            return;
        }

        pressedProfileChip = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>();
        profileDragOrigin = e.GetPosition(strip);
        profileDragStartOffset = scroll.Offset.X;
        profileDragging = true;
        profileDragMoved = false;
        e.Pointer.Capture(strip);
        e.Handled = true;
    }

    private void ProfileStripPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!profileDragging || sender is not ListBox strip)
        {
            return;
        }

        ScrollViewer? scroll = ProfileStripScroll(strip);
        if (scroll is null)
        {
            return;
        }

        double travelled = profileDragOrigin.X - e.GetPosition(strip).X;
        if (Math.Abs(travelled) > 4)
        {
            profileDragMoved = true;
        }

        double furthest = Math.Max(0, scroll.Extent.Width - scroll.Viewport.Width);
        double wanted = Math.Clamp(profileDragStartOffset + travelled, 0, furthest);
        scroll.Offset = new Vector(wanted, scroll.Offset.Y);
        e.Handled = true;
    }

    private void ProfileStripPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!profileDragging || sender is not ListBox strip)
        {
            return;
        }

        profileDragging = false;
        e.Pointer.Capture(null);
        if (!profileDragMoved && pressedProfileChip?.DataContext is { } chip)
        {
            strip.SelectedItem = chip;
        }

        pressedProfileChip = null;
        e.Handled = true;
    }

    private static ScrollViewer? ProfileStripScroll(ListBox strip) =>
        strip.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

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
