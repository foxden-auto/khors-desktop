using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Khors.Core.Qr;

namespace Khors.App.Services;

/// <summary>Картинки ⇄ пиксели BGRA для <see cref="QrCodes"/>.</summary>
public static class BitmapPixels
{
    /// <summary>Пиксели картинки в BGRA без выравнивания строк (формат исходника приводится при копировании).</summary>
    public static (byte[] Bgra, int Width, int Height) ToBgra(Bitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        var size = bitmap.PixelSize;
        using var target = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using var buffer = target.Lock();
        bitmap.CopyPixels(buffer);

        var rowBytes = size.Width * 4;
        var pixels = new byte[rowBytes * size.Height];
        for (var y = 0; y < size.Height; y++)
        {
            Marshal.Copy(buffer.Address + (y * buffer.RowBytes), pixels, y * rowBytes, rowBytes);
        }

        return (pixels, size.Width, size.Height);
    }

    /// <summary>QR-код картинкой: модуль — <paramref name="scale"/> пикселей, поле тишины — 4 модуля.</summary>
    public static WriteableBitmap Render(QrMatrix matrix, int scale, Color dark, Color light)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        const int quiet = 4;
        var size = (matrix.Size + (2 * quiet)) * scale;
        var bitmap = new WriteableBitmap(new PixelSize(size, size), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using var buffer = bitmap.Lock();
        var row = new byte[size * 4];
        for (var y = 0; y < size; y++)
        {
            var my = (y / scale) - quiet;
            for (var x = 0; x < size; x++)
            {
                var mx = (x / scale) - quiet;
                var color = mx >= 0 && my >= 0 && mx < matrix.Size && my < matrix.Size && matrix[mx, my] ? dark : light;
                row[(x * 4) + 0] = color.B;
                row[(x * 4) + 1] = color.G;
                row[(x * 4) + 2] = color.R;
                row[(x * 4) + 3] = 0xFF;
            }

            Marshal.Copy(row, 0, buffer.Address + (y * buffer.RowBytes), row.Length);
        }

        return bitmap;
    }
}
