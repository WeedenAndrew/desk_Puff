using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace DeskPuff.App.Controls;

public sealed class ColorWheelPicker : Control
{
    public static readonly StyledProperty<string> SelectedColorProperty = AvaloniaProperty.Register<
        ColorWheelPicker,
        string>(
        nameof(SelectedColor),
        "#0000FF",
        defaultBindingMode: BindingMode.TwoWay);

    private WriteableBitmap? wheelBitmap;
    private int renderedWheelSize;

    static ColorWheelPicker()
    {
        SelectedColorProperty.Changed.AddClassHandler<ColorWheelPicker>(
            static (control, _) => control.InvalidateVisual());
    }

    public ColorWheelPicker()
    {
        Cursor = new Cursor(StandardCursorType.Cross);
        Focusable = true;
    }

    public string SelectedColor
    {
        get => GetValue(SelectedColorProperty);
        set => SetValue(SelectedColorProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 150 : availableSize.Width;
        double height = double.IsInfinity(availableSize.Height) ? 170 : availableSize.Height;
        return new Size(Math.Min(width, 150), Math.Min(height, 170));
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        double diameter = WheelDiameter();
        if (diameter <= 2)
        {
            return;
        }

        int bitmapSize = Math.Max(2, (int)Math.Ceiling(diameter));
        if (wheelBitmap is null || renderedWheelSize != bitmapSize)
        {
            wheelBitmap?.Dispose();
            wheelBitmap = CreateWheelBitmap(bitmapSize);
            renderedWheelSize = bitmapSize;
        }

        double left = (Bounds.Width - diameter) / 2;
        Rect wheelBounds = new(left, 0, diameter, diameter);
        using (context.PushGeometryClip(new EllipseGeometry(wheelBounds)))
        {
            context.DrawImage(
                wheelBitmap,
                new Rect(0, 0, wheelBitmap.PixelSize.Width, wheelBitmap.PixelSize.Height),
                wheelBounds);
        }

        (double hue, double saturation, double value) = ParseHsv(SelectedColor);
        double radius = diameter / 2;
        double angle = hue * Math.PI / 180;
        Point marker = new(
            wheelBounds.Left + radius + (Math.Cos(angle) * radius * saturation),
            wheelBounds.Top + radius + (Math.Sin(angle) * radius * saturation));
        context.DrawEllipse(Brushes.Transparent, new Pen(Brushes.Black, 4), marker, 5, 5);
        context.DrawEllipse(Brushes.Transparent, new Pen(Brushes.White, 2), marker, 5, 5);

        Rect brightnessBounds = BrightnessBounds(diameter);
        LinearGradientBrush brightness = new()
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Colors.Black, 0),
                new GradientStop(ColorFromHsv(hue, saturation, 1), 1),
            },
        };
        context.DrawRectangle(brightness, null, brightnessBounds, 4, 4);
        double brightnessX = brightnessBounds.Left + (brightnessBounds.Width * value);
        context.DrawLine(
            new Pen(Brushes.White, 2),
            new Point(brightnessX, brightnessBounds.Top - 2),
            new Point(brightnessX, brightnessBounds.Bottom + 2));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        Focus();
        e.Pointer.Capture(this);
        UpdateFromPoint(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (e.Pointer.Captured == this && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            UpdateFromPoint(e.GetPosition(this));
            e.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.Pointer.Captured == this)
        {
            UpdateFromPoint(e.GetPosition(this));
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    private void UpdateFromPoint(Point point)
    {
        double diameter = WheelDiameter();
        double left = (Bounds.Width - diameter) / 2;
        Rect brightnessBounds = BrightnessBounds(diameter);
        (double hue, double saturation, double value) = ParseHsv(SelectedColor);
        if (brightnessBounds.Contains(point))
        {
            value = Math.Clamp((point.X - brightnessBounds.Left) / brightnessBounds.Width, 0, 1);
        }
        else
        {
            double radius = diameter / 2;
            double x = point.X - (left + radius);
            double y = point.Y - radius;
            double distance = Math.Sqrt((x * x) + (y * y));
            if (point.Y < 0 || point.Y > diameter || distance > radius + 4)
            {
                return;
            }

            hue = (Math.Atan2(y, x) * 180 / Math.PI + 360) % 360;
            saturation = Math.Clamp(distance / radius, 0, 1);
            if (value < 0.04)
            {
                value = 1;
            }
        }

        Color color = ColorFromHsv(hue, saturation, value);
        SetCurrentValue(SelectedColorProperty, $"#{color.R:X2}{color.G:X2}{color.B:X2}");
        InvalidateVisual();
    }

    private double WheelDiameter() => Math.Max(0, Math.Min(Bounds.Width, Bounds.Height - 20));

    private Rect BrightnessBounds(double wheelDiameter) => new(
        (Bounds.Width - wheelDiameter) / 2,
        wheelDiameter + 10,
        wheelDiameter,
        8);

    private static WriteableBitmap CreateWheelBitmap(int size)
    {
        byte[] pixels = new byte[size * size * 4];
        double center = (size - 1) / 2.0;
        double radius = size / 2.0;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double relativeX = x - center;
                double relativeY = y - center;
                double saturation = Math.Sqrt(
                    (relativeX * relativeX) + (relativeY * relativeY)) / radius;
                if (saturation > 1)
                {
                    continue;
                }

                double hue = (Math.Atan2(relativeY, relativeX) * 180 / Math.PI + 360) % 360;
                Color color = ColorFromHsv(hue, saturation, 1);
                int offset = ((y * size) + x) * 4;
                pixels[offset] = color.B;
                pixels[offset + 1] = color.G;
                pixels[offset + 2] = color.R;
                pixels[offset + 3] = 255;
            }
        }

        WriteableBitmap bitmap = new(
            new PixelSize(size, size),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Unpremul);
        using ILockedFramebuffer framebuffer = bitmap.Lock();
        for (int row = 0; row < size; row++)
        {
            Marshal.Copy(
                pixels,
                row * size * 4,
                framebuffer.Address + (row * framebuffer.RowBytes),
                size * 4);
        }

        return bitmap;
    }

    private static (double Hue, double Saturation, double Value) ParseHsv(string colorHex)
    {
        if (colorHex is not { Length: 7 } ||
            colorHex[0] != '#' ||
            !uint.TryParse(
                colorHex.AsSpan(1),
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out uint rgb))
        {
            return (0, 0, 1);
        }

        double red = ((rgb >> 16) & 0xFF) / 255.0;
        double green = ((rgb >> 8) & 0xFF) / 255.0;
        double blue = (rgb & 0xFF) / 255.0;
        double maximum = Math.Max(red, Math.Max(green, blue));
        double minimum = Math.Min(red, Math.Min(green, blue));
        double delta = maximum - minimum;
        double hue = delta switch
        {
            0 => 0,
            _ when maximum == red => 60 * (((green - blue) / delta) % 6),
            _ when maximum == green => 60 * (((blue - red) / delta) + 2),
            _ => 60 * (((red - green) / delta) + 4),
        };
        if (hue < 0)
        {
            hue += 360;
        }

        double saturation = maximum == 0 ? 0 : delta / maximum;
        return (hue, saturation, maximum);
    }

    private static Color ColorFromHsv(double hue, double saturation, double value)
    {
        double chroma = value * saturation;
        double x = chroma * (1 - Math.Abs(((hue / 60) % 2) - 1));
        double match = value - chroma;
        (double red, double green, double blue) = hue switch
        {
            < 60 => (chroma, x, 0d),
            < 120 => (x, chroma, 0d),
            < 180 => (0d, chroma, x),
            < 240 => (0d, x, chroma),
            < 300 => (x, 0d, chroma),
            _ => (chroma, 0d, x),
        };
        return Color.FromRgb(
            (byte)Math.Round((red + match) * 255),
            (byte)Math.Round((green + match) * 255),
            (byte)Math.Round((blue + match) * 255));
    }
}
