using System.Diagnostics;
using System.Runtime.InteropServices;
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.Macros;

/// <summary>
/// Plays back recorded macros via SendInput, preserving original timing.
/// The structural feature: pre-flight + continuous foreground checks against
/// the explicit <c>targetUserId</c> passed to <see cref="PlayAsync"/>. Mismatch
/// at pre-flight refuses the playback outright; mismatch mid-playback aborts
/// immediately.
///
/// Threading: <see cref="PlayAsync"/> runs as a regular async Task. The
/// SendInput calls happen on whichever thread Task.Delay continues on
/// (thread pool by default) — Win32 doesn't care which thread sends input.
/// </summary>
internal sealed class MacroPlayer
{
    private readonly ForegroundWatcher _foreground;
    private CancellationTokenSource? _activeCts;

    public MacroPlayer(ForegroundWatcher foreground)
    {
        _foreground = foreground ?? throw new ArgumentNullException(nameof(foreground));
    }

    public bool IsPlaying => _activeCts is not null;

    public event EventHandler<PlaybackStartedArgs>? Started;
    public event EventHandler<PlaybackEndedArgs>? Ended;

    /// <summary>
    /// Play the given macro against the specified target user-id. Pre-flight check:
    /// foreground window's user-id must match <paramref name="targetUserId"/>.
    /// Mid-playback check: same, on every event. Returns a <see cref="PlaybackResult"/>
    /// indicating outcome.
    /// </summary>
    public async Task<PlaybackResult> PlayAsync(Macro macro, long targetUserId, CancellationToken external = default)
    {
        if (macro is null) throw new ArgumentNullException(nameof(macro));
        if (IsPlaying) return PlaybackResult.Refused("Playback already in progress.");

        var preflight = _foreground.ResolveForegroundAccount();
        if (preflight is null)
            return PlaybackResult.Refused("No RoRoRo-managed window is currently in the foreground.");
        if (preflight.RobloxUserId != targetUserId)
            return PlaybackResult.Refused(
                $"Foreground window is user {preflight.RobloxUserId} ({preflight.DisplayName}); " +
                $"target is user {targetUserId}.");

        _activeCts = CancellationTokenSource.CreateLinkedTokenSource(external);
        Started?.Invoke(this, new PlaybackStartedArgs(macro, preflight, targetUserId));

        try
        {
            var clock = Stopwatch.StartNew();
            for (int i = 0; i < macro.Events.Count; i++)
            {
                var evt = macro.Events[i];

                var wait = evt.TimestampMs - clock.ElapsedMilliseconds;
                if (wait > 0)
                {
                    await Task.Delay((int)wait, _activeCts.Token).ConfigureAwait(false);
                }

                // Continuous foreground check. If the user alt-tabs to a non-target
                // window mid-playback, refuse to send the input.
                var fg = _foreground.ResolveForegroundAccount();
                if (fg is null || fg.RobloxUserId != targetUserId)
                {
                    return PlaybackResult.Aborted(
                        $"Foreground shifted to {(fg?.DisplayName ?? "non-RoRoRo window")} at event {i + 1}/{macro.Events.Count}.");
                }

                SendMacroEvent(evt);
            }
            return PlaybackResult.Completed();
        }
        catch (OperationCanceledException)
        {
            return PlaybackResult.Aborted("Playback cancelled.");
        }
        finally
        {
            Ended?.Invoke(this, new PlaybackEndedArgs(macro));
            _activeCts?.Dispose();
            _activeCts = null;
        }
    }

    /// <summary>
    /// Abort the currently active playback (called from auto-stop on
    /// account-exited, or from the user pressing Esc). Returns false when
    /// nothing is playing.
    /// </summary>
    public bool Abort()
    {
        var cts = _activeCts;
        if (cts is null) return false;
        try { cts.Cancel(); } catch (ObjectDisposedException) { /* race with finally */ }
        return true;
    }

    private static void SendMacroEvent(MacroEvent evt)
    {
        switch (evt.Kind)
        {
            case MacroEventKind.KeyDown:
                SendKey((ushort)evt.VirtualKeyCode, keyUp: false);
                break;
            case MacroEventKind.KeyUp:
                SendKey((ushort)evt.VirtualKeyCode, keyUp: true);
                break;
            case MacroEventKind.MouseMove:
                SendMouseMove(evt.X, evt.Y);
                break;
            case MacroEventKind.MouseDown:
                SendMouseButton(evt.X, evt.Y, evt.MouseButton, isDown: true);
                break;
            case MacroEventKind.MouseUp:
                SendMouseButton(evt.X, evt.Y, evt.MouseButton, isDown: false);
                break;
            case MacroEventKind.MouseWheel:
                SendMouseWheel(evt.X, evt.Y, evt.WheelDelta);
                break;
        }
    }

    // ---------- Input synthesis helpers ----------

    private static void SendKey(ushort vkCode, bool keyUp)
    {
        // Derive scan code from the virtual-key code. Many games (Roblox
        // included) check the scan code to distinguish "real" input from
        // synthetic, and silently drop events that arrive with wScan=0.
        // wVk + wScan together is the standard SendInput pattern for game
        // macros. KEYEVENTF_SCANCODE alone (with wVk=0) would also work but
        // breaks foreign keyboard layout mapping; the dual-field form is
        // safer across locales.
        var scanCode = (ushort)MapVirtualKey(vkCode, MAPVK_VK_TO_VSC);
        var flags = (keyUp ? KEYEVENTF_KEYUP : 0u);
        if (IsExtendedKey(vkCode)) flags |= KEYEVENTF_EXTENDEDKEY;

        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            union = new InputUnion
            {
                keyboard = new KEYBDINPUT
                {
                    wVk = vkCode,
                    wScan = scanCode,
                    dwFlags = flags,
                },
            },
        };
        SendOne(ref input);
    }

    /// <summary>
    /// Extended-key set per Win32 KEYEVENTF_EXTENDEDKEY docs. Arrow keys,
    /// PageUp/Down, Home/End, Insert/Delete, Numpad Divide, Right Ctrl, Right
    /// Alt all need the flag — Win32 keyboard handlers branch on it.
    /// </summary>
    private static bool IsExtendedKey(ushort vk) => vk switch
    {
        0x21 or 0x22 or 0x23 or 0x24 => true, // PageUp, PageDown, End, Home
        0x25 or 0x26 or 0x27 or 0x28 => true, // Left, Up, Right, Down
        0x2D or 0x2E => true,                 // Insert, Delete
        0x6F => true,                         // Numpad Divide
        0xA3 or 0xA5 => true,                 // Right Ctrl, Right Alt
        _ => false,
    };

    private static void SendMouseMove(int screenX, int screenY)
    {
        var (nx, ny) = NormalizeForVirtualDesktop(screenX, screenY);
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            union = new InputUnion
            {
                mouse = new MOUSEINPUT
                {
                    dx = nx,
                    dy = ny,
                    dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
                },
            },
        };
        SendOne(ref input);
    }

    private static void SendMouseButton(int screenX, int screenY, int button, bool isDown)
    {
        // Buttons: 1=L 2=R 3=M 4=X1 5=X2
        uint flag = button switch
        {
            1 => isDown ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP,
            2 => isDown ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
            3 => isDown ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP,
            4 or 5 => isDown ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP,
            _ => 0u,
        };
        if (flag == 0u) return;

        uint mouseData = (button == 4) ? XBUTTON1 : (button == 5) ? XBUTTON2 : 0u;
        var (nx, ny) = NormalizeForVirtualDesktop(screenX, screenY);
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            union = new InputUnion
            {
                mouse = new MOUSEINPUT
                {
                    dx = nx,
                    dy = ny,
                    mouseData = mouseData,
                    dwFlags = flag | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
                },
            },
        };
        SendOne(ref input);
    }

    private static void SendMouseWheel(int screenX, int screenY, int delta)
    {
        var (nx, ny) = NormalizeForVirtualDesktop(screenX, screenY);
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            union = new InputUnion
            {
                mouse = new MOUSEINPUT
                {
                    dx = nx,
                    dy = ny,
                    mouseData = unchecked((uint)delta),
                    dwFlags = MOUSEEVENTF_WHEEL | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
                },
            },
        };
        SendOne(ref input);
    }

    private static (int x, int y) NormalizeForVirtualDesktop(int screenX, int screenY)
    {
        // Virtual desktop covers all monitors. SendInput with MOUSEEVENTF_VIRTUALDESK
        // expects coordinates in the 0–65535 range mapped over the virtual desktop.
        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        if (vw <= 0) vw = 1;
        if (vh <= 0) vh = 1;
        int nx = (int)(((screenX - vx) * 65535.0) / vw);
        int ny = (int)(((screenY - vy) * 65535.0) / vh);
        return (nx, ny);
    }

    private static unsafe void SendOne(ref INPUT input)
    {
        fixed (INPUT* p = &input)
        {
            _ = SendInput(1, p, Marshal.SizeOf<INPUT>());
        }
    }

    // ---------- Win32 interop ----------

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;

    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private const uint MAPVK_VK_TO_VSC = 0x00;

    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_XDOWN = 0x0080;
    private const uint MOUSEEVENTF_XUP = 0x0100;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    private const uint XBUTTON1 = 0x0001;
    private const uint XBUTTON2 = 0x0002;

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mouse;
        [FieldOffset(0)] public KEYBDINPUT keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern unsafe uint SendInput(uint cInputs, INPUT* pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);
}

public enum PlaybackOutcome { Refused, Completed, Aborted, Skipped }

public sealed record PlaybackResult(PlaybackOutcome Outcome, string? Reason)
{
    public static PlaybackResult Refused(string reason) => new(PlaybackOutcome.Refused, reason);
    public static PlaybackResult Completed() => new(PlaybackOutcome.Completed, null);
    public static PlaybackResult Aborted(string reason) => new(PlaybackOutcome.Aborted, reason);
    public static PlaybackResult Skipped(string reason) => new(PlaybackOutcome.Skipped, reason);
}

internal sealed record PlaybackStartedArgs(Macro Macro, AccountRegistry.AccountInfo BoundAccount, long TargetUserId);
internal sealed record PlaybackEndedArgs(Macro Macro);
