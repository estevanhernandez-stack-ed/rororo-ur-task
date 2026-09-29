using System.Runtime.InteropServices;
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.Macros.Steps;

/// <summary>IStepIo over the real window: client→screen at send time, so a window moved
/// mid-playback stays correct (the same rule as the v3 event path).</summary>
internal sealed class RealStepIo : IStepIo
{
    private readonly IntPtr _hwnd;
    private readonly IWindowMetrics _metrics;
    private readonly IScreenSampler _sampler;
    private readonly IForegroundWatcher _foreground;
    private readonly long _targetUserId;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    public RealStepIo(IntPtr hwnd, IWindowMetrics metrics, IScreenSampler sampler, IForegroundWatcher foreground, long targetUserId)
        => (_hwnd, _metrics, _sampler, _foreground, _targetUserId) = (hwnd, metrics, sampler, foreground, targetUserId);

    public long NowMs => _clock.ElapsedMilliseconds;

    /// <summary>False when the window is gone or SendInput injected nothing: the runner stops on
    /// a refused send rather than pressing on blind.</summary>
    public bool Send(MacroEvent e)
    {
        if (e.Kind is MacroEventKind.KeyDown or MacroEventKind.KeyUp) return MacroPlayer.SendMacroEvent(e);
        var origin = _metrics.ClientOrigin(_hwnd);
        if (origin is null) return false;
        var (sx, sy) = WindowSpaceMath.ToScreen((e.X, e.Y), origin.Value);
        return MacroPlayer.SendMacroEvent(e with { X = sx, Y = sy });
    }

    public bool ReleaseButton(int button)
    {
        var (x, y) = MacroPlayer.GetCurrentCursorPos();
        return MacroPlayer.SendMacroEvent(new MacroEvent(0, MacroEventKind.MouseUp, 0, x, y, button, 0));
    }

    public bool MoveRelative(int dx, int dy) => MacroPlayer.SendMouseRelative(dx, dy);

    /// <summary>A move only, never a click, and only onto the target's own frame: the screen point
    /// must lie inside the window's outer rect above its client area (a borderless window has no
    /// such row), and the window under it must be the target itself, not one covering it. Anything
    /// else refuses without moving, and the runner takes its baseline another way.</summary>
    public bool ParkPointer(int clientX, int clientY)
    {
        var origin = _metrics.ClientOrigin(_hwnd);
        var outer = _metrics.OuterRect(_hwnd);
        if (origin is null || outer is null) return false;
        var (sx, sy) = WindowSpaceMath.ToScreen((clientX, clientY), origin.Value);
        if (!WindowSpaceMath.OnFrameAboveClient((sx, sy), outer.Value, origin.Value)) return false;
        if (GetAncestor(WindowFromPoint(new POINT { x = sx, y = sy }), GA_ROOT) != _hwnd) return false;
        return MacroPlayer.SendMacroEvent(new MacroEvent(0, MacroEventKind.MouseMove, 0, sx, sy, 0, 0));
    }

    public (int X, int Y)? CursorClient()
    {
        var origin = _metrics.ClientOrigin(_hwnd);
        if (origin is null) return null;
        var (x, y) = MacroPlayer.GetCurrentCursorPos();
        return WindowSpaceMath.ToClient((x, y), origin.Value);
    }

    public (int W, int H)? ClientSize() => _metrics.ClientSize(_hwnd) is { W: > 0, H: > 0 } s ? s : null;

    /// <summary>Exactly the requested client rect, or null. A block of any other geometry is
    /// treated as a failed capture, never handed on.</summary>
    public PixelBlock? Capture(int x, int y, int w, int h)
        => _sampler.Capture(_hwnd, x, y, w, h) is { } b && b.X == x && b.Y == y && b.W == w && b.H == h ? b : null;

    /// <summary>The same test the v3 path runs before every event: the foreground resolves to the
    /// target account.</summary>
    public bool TargetInForeground() => _foreground.ResolveForegroundAccount()?.RobloxUserId == _targetUserId;

    public Task Delay(int ms, CancellationToken ct) => ms <= 0 ? Task.CompletedTask : Task.Delay(ms, ct);

    private const uint GA_ROOT = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
}
