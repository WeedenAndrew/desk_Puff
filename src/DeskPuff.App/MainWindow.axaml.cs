using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Win32;
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
    private DateTime lastTitleBarPress = DateTime.MinValue;

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

    private const uint WmSysCommand = 0x0112;
    private const uint WmNcLeftButtonDoubleClick = 0x00A3;
    private const long ScMaximize = 0xF030;

    /// <summary>
    /// Refuses every route to a maximized or full screen window that arrives as
    /// a system command rather than through Avalonia.
    /// </summary>
    /// <remarks>
    /// What a full message trace of this hook actually showed, so the next
    /// person does not rediscover it:
    ///
    /// <list type="bullet">
    /// <item>SC_MAXIMIZE never arrives. Every WM_SYSCOMMAND seen during a title
    /// bar drag was 0xF012, which masks to SC_MOVE, posted by BeginMoveDrag.</item>
    /// <item>WM_NCLBUTTONDBLCLK never arrives either. WindowDecorations="None"
    /// means there is no non client caption to double click, so that branch has
    /// nothing to catch on this path.</item>
    /// <item>WM_GETMINMAXINFO arrives in bulk, 266 times inside two drags, and
    /// only ever inside DefWindowProc's modal move loop.</item>
    /// </list>
    ///
    /// Answering WM_GETMINMAXINFO with a clamped ptMaxSize and ptMaxTrackSize
    /// was tried and measured, and does not hold the size. Neither does setting
    /// MaxWidth and MaxHeight on the Window. With the OnPropertyChanged
    /// backstop below disabled, SW_MAXIMIZE still took the window to the full
    /// 2560x1392 work area in both cases. That backstop is what is really
    /// holding the size today; the two branches here cost nothing and cover
    /// routes the trace did not exercise.
    /// </remarks>
    private static IntPtr BlockMaximizeMessages(
        IntPtr handle,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        // Windows packs status bits into the low nibble of wParam, so the
        // command has to be masked out before it is compared.
        bool maximizing = message == WmSysCommand && (wParam.ToInt64() & 0xFFF0) == ScMaximize;
        if (maximizing || message == WmNcLeftButtonDoubleClick)
        {
            handled = true;
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Backstop for anything that sets the state without going through the
    /// window message, such as an external tool or a shell extension. Posting
    /// rather than assigning inline avoids re-entering the property changed
    /// pass while the platform implementation is still mid update.
    /// </summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property != WindowStateProperty)
        {
            return;
        }

        if (change.GetNewValue<WindowState>() is WindowState.Maximized or WindowState.FullScreen)
        {
            Dispatcher.UIThread.Post(() => WindowState = WindowState.Normal);
        }
    }

    private async void WindowOpened(object? sender, EventArgs e)
    {
        if (OperatingSystem.IsWindows())
        {
            Win32Properties.AddWndProcHookCallback(this, BlockMaximizeMessages);
        }

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

        // BeginMoveDrag hands off to a modal loop inside user32, during which
        // Avalonia pumps no input and so never increments ClickCount. Rapid
        // clicks therefore all arrive reporting ClickCount 1, while Windows
        // resolves them into WM_NCLBUTTONDBLCLK on the caption and maximizes.
        // Timing the presses here is the only reliable way to spot the repeat.
        DateTime now = DateTime.UtcNow;
        TimeSpan sinceLastPress = now - lastTitleBarPress;
        lastTitleBarPress = now;
        if (sinceLastPress < TimeSpan.FromMilliseconds(500))
        {
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
