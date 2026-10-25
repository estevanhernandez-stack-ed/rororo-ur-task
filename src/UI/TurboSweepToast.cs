using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Labs626.UrTask.UI;

/// <summary>
/// The turbo keep-alive warning: a small topmost card, bottom-right of the primary
/// work area, that counts down before a sweep starts jumping windows.
///
/// It must NEVER take focus. The sweep's safety check reads the foreground window,
/// and a toast that activated itself would both steal the user's keyboard and make
/// the confirm fail. So: ShowActivated=false, plus WS_EX_NOACTIVATE |
/// WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT (click-through, no taskbar/Alt+Tab entry).
///
/// Called from the runner's thread pool continuations, so every call marshals to
/// the UI dispatcher with BeginInvoke — never Invoke, which could deadlock against
/// a UI thread waiting on the runner. Best-effort throughout: a toast failure is
/// logged nowhere and never reaches the sweep.
/// </summary>
internal static class TurboSweepToast
{
    private static Window? _window;
    private static TextBlock? _headline;
    private static TextBlock? _detail;

    /// <summary>secondsLeft &gt; 0 shows/updates the card; 0 hides it.</summary>
    public static void Update(int secondsLeft, int windowCount)
    {
        var disp = Application.Current?.Dispatcher;
        if (disp is null || disp.HasShutdownStarted) return;
        disp.BeginInvoke(() =>
        {
            try
            {
                if (secondsLeft <= 0)
                {
                    _window?.Hide();
                    return;
                }

                EnsureWindow();
                _headline!.Text = $"Keep-alive sweep in {secondsLeft}";
                _detail!.Text = windowCount == 1
                    ? "Jumping 1 window. Ctrl+Shift+F12 stops it."
                    : $"Jumping {windowCount} windows. Ctrl+Shift+F12 stops it.";
                if (!_window!.IsVisible) _window.Show();
                PlaceBottomRight(_window);
            }
            catch { /* best-effort UI */ }
        });
    }

    private static void EnsureWindow()
    {
        if (_window is not null) return;

        _headline = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold };
        _headline.SetResourceReference(TextBlock.ForegroundProperty, "WhiteBrush");
        _detail = new TextBlock { FontSize = 11, Margin = new Thickness(0, 3, 0, 0) };
        _detail.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");

        var card = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 10, 14, 10),
            Child = new StackPanel { Children = { _headline, _detail } },
        };
        card.SetResourceReference(Border.BackgroundProperty, "BgBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "CyanBrush");

        _window = new Window
        {
            Title = "Ur Task keep-alive sweep",
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Focusable = false,
            IsHitTestVisible = false,
            Content = card,
        };
        _window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(_window).Handle;
            const long add = WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_TOPMOST;
            if (IntPtr.Size == 8)
                SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() | add));
            else
                SetWindowLong32(hwnd, GWL_EXSTYLE, GetWindowLong32(hwnd, GWL_EXSTYLE) | (int)add);
        };
        _window.SizeChanged += (_, _) => PlaceBottomRight(_window);
    }

    private static void PlaceBottomRight(Window w)
    {
        var area = SystemParameters.WorkArea;
        const double margin = 16;
        w.Left = area.Right - w.ActualWidth - margin;
        w.Top = area.Bottom - w.ActualHeight - margin;
    }

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOPMOST = 0x00000008;
    private const long WS_EX_TRANSPARENT = 0x00000020;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);
}
