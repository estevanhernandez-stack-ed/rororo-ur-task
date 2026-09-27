using System.Runtime.InteropServices;
using Labs626.UrTask.Macros.Steps;

namespace Labs626.UrTask.PluginHost;

/// <summary>GDI capture from the desktop DC (BitBlt + GetDIBits), the same approach as Ur OCR.</summary>
internal sealed class ScreenSampler : IScreenSampler
{
    private readonly IWindowMetrics _metrics;
    public ScreenSampler(IWindowMetrics metrics) => _metrics = metrics;

    public PixelBlock? Capture(IntPtr hwnd, int clientX, int clientY, int w, int h)
    {
        if (w <= 0 || h <= 0) return null;
        if (IsIconic(hwnd)) return null;
        var origin = _metrics.ClientOrigin(hwnd);
        if (origin is null) return null;
        int sx = origin.Value.X + clientX, sy = origin.Value.Y + clientY;

        IntPtr screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return null;
        IntPtr mem = CreateCompatibleDC(screen);
        IntPtr bmp = CreateCompatibleBitmap(screen, w, h);
        IntPtr old = SelectObject(mem, bmp);
        bool selected = true;
        try
        {
            if (!BitBlt(mem, 0, 0, w, h, screen, sx, sy, SRCCOPY)) return null;
            SelectObject(mem, old); // GetDIBits needs the bitmap out of any DC
            selected = false;
            var info = new BITMAPINFO
            {
                biSize = Marshal.SizeOf<BITMAPINFO>(), biWidth = w, biHeight = -h, // negative = top-down rows
                biPlanes = 1, biBitCount = 32, biCompression = 0,
            };
            var px = new uint[w * h];
            if (GetDIBits(mem, bmp, 0, (uint)h, px, ref info, 0) == 0) return null;
            for (int i = 0; i < px.Length; i++) px[i] |= 0xFF000000u;
            return new PixelBlock(clientX, clientY, w, h, px);
        }
        finally
        {
            // A bitmap still selected into a DC cannot be deleted. Without this, every failed
            // BitBlt would leak one GDI bitmap, once per poll.
            if (selected) SelectObject(mem, old);
            DeleteObject(bmp);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private const uint SRCCOPY = 0x00CC0020;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public int biSize; public int biWidth; public int biHeight; public short biPlanes; public short biBitCount;
        public int biCompression; public int biSizeImage; public int biXPelsPerMeter; public int biYPelsPerMeter;
        public int biClrUsed; public int biClrImportant;
    }

    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr bmp, uint start, uint lines, [Out] uint[] bits, ref BITMAPINFO info, uint usage);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
}
