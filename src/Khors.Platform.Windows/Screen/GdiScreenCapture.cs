using System.Runtime.InteropServices;

namespace Khors.Platform.Windows.Screen;

/// <summary>
/// Снимок виртуального экрана (все мониторы) через GDI: BitBlt экранного контекста в 32-битную DIB-секцию.
/// Процесс KHORS осведомлён о DPI мониторов (Avalonia), поэтому снимок — в физических пикселях.
/// </summary>
public sealed partial class GdiScreenCapture : IScreenCapture
{
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const uint SrcCopy = 0x00CC0020;
    private const uint CaptureBlt = 0x40000000;

    public ScreenImage CaptureAllScreens()
    {
        var x = GetSystemMetrics(SmXVirtualScreen);
        var y = GetSystemMetrics(SmYVirtualScreen);
        var width = GetSystemMetrics(SmCxVirtualScreen);
        var height = GetSystemMetrics(SmCyVirtualScreen);
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("The virtual screen has no size.");
        }

        var screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero)
        {
            throw new InvalidOperationException("GetDC failed.");
        }

        var memory = IntPtr.Zero;
        var section = IntPtr.Zero;
        var previous = IntPtr.Zero;
        try
        {
            memory = CreateCompatibleDC(screen);
            var header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = width,
                Height = -height, // сверху вниз
                Planes = 1,
                BitCount = 32,
            };
            section = CreateDIBSection(screen, ref header, 0, out var bits, IntPtr.Zero, 0);
            if (memory == IntPtr.Zero || section == IntPtr.Zero || bits == IntPtr.Zero)
            {
                throw new InvalidOperationException("Could not create a capture bitmap.");
            }

            previous = SelectObject(memory, section);
            if (!BitBlt(memory, 0, 0, width, height, screen, x, y, SrcCopy | CaptureBlt))
            {
                throw new InvalidOperationException($"BitBlt failed: {Marshal.GetLastPInvokeError()}.");
            }

            var pixels = new byte[width * height * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);

            // GDI не заполняет альфу; распознавание QR смешивает прозрачные пиксели с белым.
            for (var i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = 0xFF;
            }

            return new ScreenImage(pixels, width, height);
        }
        finally
        {
            if (previous != IntPtr.Zero)
            {
                SelectObject(memory, previous);
            }

            if (section != IntPtr.Zero)
            {
                DeleteObject(section);
            }

            if (memory != IntPtr.Zero)
            {
                DeleteDC(memory);
            }

            _ = ReleaseDC(IntPtr.Zero, screen);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetDC(IntPtr window);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(IntPtr window, IntPtr dc);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateCompatibleDC(IntPtr dc);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfoHeader info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint rop);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(IntPtr obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(IntPtr dc);
}
