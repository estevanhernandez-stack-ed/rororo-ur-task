using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Labs626.UrTask.UI;

/// <summary>
/// Gives a borderless, <c>AllowsTransparency="True"</c> window (WindowStyle="None")
/// full edge + corner drag-resize. AllowsTransparency forces the underlying HWND to
/// WS_POPUP, which drops WS_THICKFRAME — so Windows never offers a resize border on
/// its own, and ResizeMode="CanResizeWithGrip" alone only gets you the bottom-right
/// grip control (a WPF-drawn Thumb, not a native edge). This answers WM_NCHITTEST
/// ourselves for points near the window's outer edge so the OS treats a drag there
/// as a native resize — HTLEFT/HTRIGHT/HTTOP/etc. work on a WS_POPUP window even
/// without WS_THICKFRAME, and WPF's own WM_GETMINMAXINFO handling still enforces
/// Window.MinWidth/MinHeight regardless of how the resize was initiated, so callers
/// (e.g. RecorderWindow's compact-mode min sizes) don't need to do anything extra.
/// </summary>
internal static class NativeResizeBehavior
{
    private const int WM_NCHITTEST = 0x0084;
    private const int HTLEFT = 10;
    private const int HTRIGHT = 11;
    private const int HTTOP = 12;
    private const int HTTOPLEFT = 13;
    private const int HTTOPRIGHT = 14;
    private const int HTBOTTOM = 15;
    private const int HTBOTTOMLEFT = 16;
    private const int HTBOTTOMRIGHT = 17;

    /// <summary>Resize hot-zone width in DIPs. Scaled per-window to physical pixels
    /// off that window's own DPI (GetDpiForWindow) — this app is PerMonitorV2
    /// DPI-aware (app.manifest), so the margin stays a consistent ~6px regardless
    /// of which monitor the window is currently on.</summary>
    private const double ResizeMarginDip = 6;

    /// <summary>
    /// Wires the WM_NCHITTEST hook. Call once from the window's constructor (right
    /// after InitializeComponent) — it defers the actual hookup to SourceInitialized,
    /// the first point at which the Win32 HWND exists.
    /// </summary>
    public static void Attach(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            var source = HwndSource.FromHwnd(hwnd);
            source?.AddHook((IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                HitTest(window, h, msg, lParam, ref handled));
        };
    }

    private static IntPtr HitTest(Window window, IntPtr hwnd, int msg, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_NCHITTEST) return IntPtr.Zero;

        // A maximized window has no edges to grab (and GetWindowRect on a
        // maximized HWND can report a rect that overshoots the visible monitor
        // by the invisible resize-border compensation) — leave it to default
        // processing.
        if (window.WindowState == WindowState.Maximized) return IntPtr.Zero;

        if (!GetWindowRect(hwnd, out var rect)) return IntPtr.Zero;

        // lParam packs the cursor position in *screen* pixels as two 16-bit
        // shorts — must decode it this way, not via a DIP/Point conversion.
        int raw = lParam.ToInt32();
        short xScreen = (short)(raw & 0xFFFF);
        short yScreen = (short)((raw >> 16) & 0xFFFF);

        // Outside the window's current bounds entirely (can happen transiently
        // while a drag is already in flight) — nothing for us to report.
        if (xScreen < rect.left || xScreen >= rect.right || yScreen < rect.top || yScreen >= rect.bottom)
            return IntPtr.Zero;

        double scale = GetDpiForWindow(hwnd) / 96.0;
        int margin = Math.Max(1, (int)Math.Round(ResizeMarginDip * scale));

        bool onLeft = xScreen < rect.left + margin;
        bool onRight = xScreen >= rect.right - margin;
        bool onTop = yScreen < rect.top + margin;
        bool onBottom = yScreen >= rect.bottom - margin;

        int hit =
            onTop && onLeft ? HTTOPLEFT :
            onTop && onRight ? HTTOPRIGHT :
            onBottom && onLeft ? HTBOTTOMLEFT :
            onBottom && onRight ? HTBOTTOMRIGHT :
            onLeft ? HTLEFT :
            onRight ? HTRIGHT :
            onTop ? HTTOP :
            onBottom ? HTBOTTOM :
            0;

        if (hit == 0) return IntPtr.Zero; // interior — fall through to default (HTCLIENT) processing

        handled = true;
        return new IntPtr(hit);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left; public int top; public int right; public int bottom; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern int GetDpiForWindow(IntPtr hWnd);
}
