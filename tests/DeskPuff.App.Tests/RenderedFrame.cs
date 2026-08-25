using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace DeskPuff.App.Tests;

/// <summary>
/// One captured frame with its pixels available for inspection. Measurements
/// live here rather than in the tests, so a test reads as a claim about the
/// interface rather than about byte offsets.
/// </summary>
internal sealed class RenderedFrame
{
    private readonly byte[] pixels;

    internal RenderedFrame(string name, WriteableBitmap bitmap)
    {
        Name = name;
        Bitmap = bitmap;
        Width = bitmap.PixelSize.Width;
        Height = bitmap.PixelSize.Height;
        pixels = new byte[Width * Height * 4];
        using ILockedFramebuffer buffer = bitmap.Lock();
        System.Runtime.InteropServices.Marshal.Copy(buffer.Address, pixels, 0, pixels.Length);
    }

    internal string Name { get; }

    internal WriteableBitmap Bitmap { get; }

    internal int Width { get; }

    internal int Height { get; }

    internal PixelRect Everything => new(0, 0, Width, Height);

    internal void Save(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        Bitmap.Save(path, new PngBitmapEncoderOptions());
    }

    /// <summary>
    /// How many distinct colours appear in a region, sampled on a grid. This is
    /// the measurement the assertions rest on: a frame the renderer never drew
    /// into is one flat colour, and any region with real content is in the
    /// hundreds. Nothing about it moves when a margin changes.
    /// </summary>
    internal int DistinctColors(PixelRect region)
    {
        HashSet<uint> seen = [];
        for (int y = region.Y; y < region.Y + region.Height; y += 2)
        {
            for (int x = region.X; x < region.X + region.Width; x += 2)
            {
                seen.Add(ColorAt(x, y));
            }
        }

        return seen.Count;
    }

    /// <summary>The share of sampled pixels that differ between two frames.</summary>
    internal double DifferenceFrom(RenderedFrame other)
    {
        if (Width != other.Width || Height != other.Height)
        {
            return 1;
        }

        int differing = 0;
        int counted = 0;
        for (int y = 0; y < Height; y += 4)
        {
            for (int x = 0; x < Width; x += 4)
            {
                if (ColorAt(x, y) != other.ColorAt(x, y))
                {
                    differing++;
                }

                counted++;
            }
        }

        return counted == 0 ? 0 : (double)differing / counted;
    }

    private uint ColorAt(int x, int y)
    {
        int offset = ((y * Width) + x) * 4;
        return (uint)(pixels[offset] |
            (pixels[offset + 1] << 8) |
            (pixels[offset + 2] << 16) |
            (pixels[offset + 3] << 24));
    }
}
