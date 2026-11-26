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
    private readonly Func<int, int, IntPtr> _rootAt;
    private readonly Func<MacroEvent, bool> _send;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    /// <param name="rootAt">The top-level window under a screen point. Null: the real one
    /// (<see cref="WindowUnderPoint.Root"/>). A seam because the Win32 call means nothing in a test.</param>
    /// <param name="send">Injects one event already in screen space. Null: SendInput.</param>
    public RealStepIo(IntPtr hwnd, IWindowMetrics metrics, IScreenSampler sampler, IForegroundWatcher foreground, long targetUserId,
        Func<int, int, IntPtr>? rootAt = null, Func<MacroEvent, bool>? send = null)
    {
        (_hwnd, _metrics, _sampler, _foreground, _targetUserId) = (hwnd, metrics, sampler, foreground, targetUserId);
        _rootAt = rootAt ?? WindowUnderPoint.Root;
        _send = send ?? MacroPlayer.SendMacroEvent;
    }

    public long NowMs => _clock.ElapsedMilliseconds;

    public bool LastSendCovered { get; private set; }

    /// <summary>False when the window is gone, SendInput injected nothing, or another window is
    /// over the point of a press or a wheel notch (<see cref="LastSendCovered"/>): the runner stops
    /// on a refused send rather than pressing on blind.
    /// <para>Mouse input goes to whatever window is under the pointer, not to the one in front, and
    /// the runner's guard only knows which is in front. Ur Task's own compact window is
    /// always-on-top, so a point recorded on one account can sit under it on the next, and the
    /// click would land on its Stop or CLEAR button. Moves and releases are never held to this: a
    /// move presses nothing, and a button that is down must always be able to come up.</para></summary>
    public bool Send(MacroEvent e)
    {
        LastSendCovered = false;
        if (e.Kind is MacroEventKind.KeyDown or MacroEventKind.KeyUp) return _send(e);
        var origin = _metrics.ClientOrigin(_hwnd);
        if (origin is null) return false;
        var (sx, sy) = WindowSpaceMath.ToScreen((e.X, e.Y), origin.Value);
        if (WindowUnderPoint.Guards(e.Kind) && Covered(sx, sy))
        {
            LastSendCovered = true;
            return false;
        }
        return _send(e with { X = sx, Y = sy });
    }

    /// <summary>True when the top-level window under a screen point is not the target: something
    /// covers it there, or the point is off the window altogether. Holds for the client area as
    /// well as the frame: Roblox's client area is part of its main window.</summary>
    private bool Covered(int screenX, int screenY) => _rootAt(screenX, screenY) != _hwnd;

    public bool ReleaseButton(int button)
    {
        var (x, y) = MacroPlayer.GetCurrentCursorPos();
        return _send(new MacroEvent(0, MacroEventKind.MouseUp, 0, x, y, button, 0));
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
        if (Covered(sx, sy)) return false;
        return _send(new MacroEvent(0, MacroEventKind.MouseMove, 0, sx, sy, 0, 0));
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
    /// treated as a failed capture, never handed on. Null too when another window covers the
    /// middle of the box: the capture reads the screen, so it would hand back that window's pixels
    /// as the game's. With no client origin the window has gone, and the sampler says so itself.</summary>
    public PixelBlock? Capture(int x, int y, int w, int h)
    {
        if (_metrics.ClientOrigin(_hwnd) is { } origin)
        {
            var (cx, cy) = WindowSpaceMath.ToScreen((x + w / 2, y + h / 2), origin);
            if (Covered(cx, cy)) return null;
        }
        return _sampler.Capture(_hwnd, x, y, w, h) is { } b && b.X == x && b.Y == y && b.W == w && b.H == h ? b : null;
    }

    /// <summary>The same test the v3 path runs before every event: the foreground resolves to the
    /// target account.</summary>
    public bool TargetInForeground() => _foreground.ResolveForegroundAccount()?.RobloxUserId == _targetUserId;

    public Task Delay(int ms, CancellationToken ct) => ms <= 0 ? Task.CompletedTask : Task.Delay(ms, ct);
}

/// <summary>Which top-level window is under a screen point.</summary>
internal static class WindowUnderPoint
{
    /// <summary>The events that act on whatever is under the pointer, and so are held to the
    /// target being there: a button going down and a wheel notch. Shared by the step path and the
    /// event-list path so the two cannot drift.</summary>
    public static bool Guards(MacroEventKind kind) => kind is MacroEventKind.MouseDown or MacroEventKind.MouseWheel;

    public static IntPtr Root(int screenX, int screenY)
        => GetAncestor(WindowFromPoint(new POINT { x = screenX, y = screenY }), GA_ROOT);

    private const uint GA_ROOT = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
}
