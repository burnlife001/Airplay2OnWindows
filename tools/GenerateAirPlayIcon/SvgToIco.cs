using System.Drawing;
using System.Drawing.Imaging;
using SkiaSharp;
using Svg.Skia;

namespace GenerateAirPlayIcon;

internal static class SvgToIco
{
    public static void Convert(string svgPath, string icoPath, IReadOnlyList<int> sizes)
    {
        using var svg = new SKSvg();
        if (svg.Load(svgPath) is null)
        {
            throw new InvalidOperationException($"Failed to load SVG: {svgPath}");
        }

        if (svg.Picture is null)
        {
            throw new InvalidOperationException($"SVG has no picture: {svgPath}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(icoPath))!);

        var bitmaps = sizes.Select(size => Render(svg, size)).ToArray();
        try
        {
            WriteIco(icoPath, bitmaps, sizes);
        }
        finally
        {
            foreach (var bitmap in bitmaps)
            {
                bitmap.Dispose();
            }
        }
    }

    private static Bitmap Render(SKSvg svg, int size)
    {
        var bounds = svg.Picture!.CullRect;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            bounds = new SKRect(0, 0, 1024, 1024);
        }

        var scale = Math.Min(size / bounds.Width, size / bounds.Height);
        var dx = (size - bounds.Width * scale) / 2f - bounds.Left * scale;
        var dy = (size - bounds.Height * scale) / 2f - bounds.Top * scale;

        using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Translate(dx, dy);
        canvas.Scale(scale);
        canvas.DrawPicture(svg.Picture);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = new MemoryStream(data.ToArray());
        return new Bitmap(stream);
    }

    private static void WriteIco(string path, Bitmap[] bitmaps, IReadOnlyList<int> sizes)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);

        writer.Write((short)0);
        writer.Write((short)1);
        writer.Write((short)sizes.Count);

        var pngEntries = new List<byte[]>(sizes.Count);
        foreach (var bitmap in bitmaps)
        {
            using var pngStream = new MemoryStream();
            bitmap.Save(pngStream, ImageFormat.Png);
            pngEntries.Add(pngStream.ToArray());
        }

        var offset = 6 + 16 * sizes.Count;
        for (var i = 0; i < sizes.Count; i++)
        {
            var pngBytes = pngEntries[i];
            var dimension = sizes[i];

            writer.Write((byte)(dimension >= 256 ? 0 : dimension));
            writer.Write((byte)(dimension >= 256 ? 0 : dimension));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((short)1);
            writer.Write((short)32);
            writer.Write(pngBytes.Length);
            writer.Write(offset);
            offset += pngBytes.Length;
        }

        foreach (var pngBytes in pngEntries)
        {
            writer.Write(pngBytes);
        }
    }
}
