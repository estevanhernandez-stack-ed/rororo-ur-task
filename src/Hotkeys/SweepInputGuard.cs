using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Labs626.UrTask.Hotkeys;

/// <summary>
/// What a turbo sweep does with one key or button event the user makes while it holds the
/// foreground. Pure, so the rules are tested without a hook. Ur Task's own input (injected) always
/// passes. The user's presses are dropped instead of landing in whichever account's window is in
/// front: a W, A, S or D typed mid-sweep walked that account, and Shift toggles shift-lock. A
/// release passes unless its press was dropped, so a key held from before the sweep is never left
/// stuck down. The stop chords are dropped too (they would reach a game window) and reported, so
/// the caller can act on them itself: a dropped key never reaches RegisterHotKey.
/// </summary>
internal sealed class SweepKeyFilter
{
    internal const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_ESCAPE = 0x1B, VK_F11 = 0x7A, VK_F12 = 0x7B;
    private static readonly int[] Ctrls = { VK_CONTROL, 0xA2, 0xA3 };
    private static readonly int[] Shifts = { VK_SHIFT, 0xA0, 0xA1 };

    private readonly HashSet<int> _dropped = new();
    private bool _ctrl, _shift;

    public SweepKeyFilter(bool ctrlHeld = false, bool shiftHeld = false) { _ctrl = ctrlHeld; _shift = shiftHeld; }

    public int Dropped { get; private set; }

    /// <summary>True to drop the event. <paramref name="chord"/> is set when the press is one of
    /// the stop chords: Ctrl+Shift+F12 or Esc (abort), Ctrl+Shift+F11 (break-glass).</summary>
    public bool OnKey(int vk, bool down, bool injected, out HotkeyKind? chord)
    {
        chord = null;
        if (injected) return false;
        if (Array.IndexOf(Ctrls, vk) >= 0) _ctrl = down;
        if (Array.IndexOf(Shifts, vk) >= 0) _shift = down;
        if (!down) return Release(vk);

        if (_dropped.Add(vk)) Dropped++;   // auto-repeat of a dropped key counts once
        if (vk == VK_ESCAPE || (vk == VK_F12 && _ctrl && _shift)) chord = HotkeyKind.Abort;
        else if (vk == VK_F11 && _ctrl && _shift) chord = HotkeyKind.Breakglass;
        return true;
    }

    /// <summary>Mouse buttons and the wheel, by the same rule. Moves always pass. <paramref name="button"/>
    /// is any stable id per button; the wheel has no release, so pass <paramref name="isWheel"/>.</summary>
    public bool OnButton(int button, bool down, bool injected, bool isWheel = false)
    {
        if (injected) return false;
        if (isWheel) { Dropped++; return true; }
        if (!down) return Release(-1 - button);
        if (_dropped.Add(-1 - button)) Dropped++;
        return true;
    }

    private bool Release(int key) => _dropped.Remove(key);
}

/// <summary>
/// Installs low-level keyboard and mouse hooks for the length of a turbo sweep and drops the user's
/// presses through <see cref="SweepKeyFilter"/>. The hooks live on their own thread with a message
/// pump, as low-level hooks must. Dispose removes them; a sweep that never disposes is cut off at
/// <see cref="MaxHold"/> so a stuck sweep can never keep the keyboard. Every call is best-effort: a
/// hook that fails to install means a sweep without the guard, never a failed sweep.
/// </summary>
internal sealed class SweepInputGuard : IDisposable
{
    /// <summary>The stop chords pressed during a sweep. The runtime routes them to its hotkey handler.</summary>
    public static event Action<HotkeyKind, string>? ChordPressed;

    internal static readonly TimeSpan MaxHold = TimeSpan.FromSeconds(15);

    private const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205,
        WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208, WM_MOUSEWHEEL = 0x020A, WM_XBUTTONDOWN = 0x020B,
        WM_XBUTTONUP = 0x020C, WM_MOUSEHWHEEL = 0x020E;
    private const uint LLKHF_INJECTED = 0x10, LLMHF_INJECTED = 0x01, WM_QUIT = 0x0012;

    private readonly SweepKeyFilter _filter;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly Stopwatch _held = Stopwatch.StartNew();
    private HookProc? _keyProc, _mouseProc;   // fields: the delegates must outlive the hooks
    private uint _threadId;
    private int _disposed;

    private SweepInputGuard()
    {
        _filter = new SweepKeyFilter(IsDown(SweepKeyFilter.VK_CONTROL), IsDown(SweepKeyFilter.VK_SHIFT));
        _thread = new Thread(Pump) { IsBackground = true, Name = "RoRoRoUrTask-SweepGuard" };
    }

    public static IDisposable Begin()
    {
        try
        {
            var g = new SweepInputGuard();
            g._thread.Start();
            g._ready.Wait(TimeSpan.FromSeconds(1));
            return g;
        }
        catch { return NoGuard.Instance; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        if (Environment.CurrentManagedThreadId != _thread.ManagedThreadId) _thread.Join(TimeSpan.FromSeconds(1));
        if (_filter.Dropped > 0)
            Diagnostics.DiagLog.Write($"turbo keep-alive: dropped {_filter.Dropped} key or click press(es) made during the sweep, so none reached an account's window");
    }

    private void Pump()
    {
        IntPtr kb = IntPtr.Zero, ms = IntPtr.Zero;
        try
        {
            _threadId = GetCurrentThreadId();
            _keyProc = KeyProc;
            _mouseProc = MouseProc;
            var module = GetModuleHandle(null);
            kb = SetWindowsHookEx(WH_KEYBOARD_LL, _keyProc, module, 0);
            ms = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, module, 0);
            if (kb == IntPtr.Zero) Diagnostics.DiagLog.Write("turbo keep-alive: could not hook the keyboard; keys typed during the sweep reach the game");
            _ready.Set();
            // A timer message bounds the pump, so MaxHold is enforced even with no input at all.
            var timer = SetTimer(IntPtr.Zero, IntPtr.Zero, 500, IntPtr.Zero);
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (_held.Elapsed > MaxHold) break;
                DispatchMessage(ref msg);
            }
            KillTimer(IntPtr.Zero, timer);
        }
        catch { _ready.Set(); }
        finally
        {
            if (kb != IntPtr.Zero) UnhookWindowsHookEx(kb);
            if (ms != IntPtr.Zero) UnhookWindowsHookEx(ms);
        }
    }

    private IntPtr KeyProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && _held.Elapsed <= MaxHold)
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            var msg = (int)wParam;
            var down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
            if (down || msg is WM_KEYUP or WM_SYSKEYUP)
            {
                if (_filter.OnKey((int)info.vkCode, down, (info.flags & LLKHF_INJECTED) != 0, out var chord))
                {
                    if (chord is { } k) Raise(k, info.vkCode);
                    return (IntPtr)1;
                }
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private IntPtr MouseProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && _held.Elapsed <= MaxHold)
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            var injected = (info.flags & LLMHF_INJECTED) != 0;
            var drop = (int)wParam switch
            {
                WM_LBUTTONDOWN => _filter.OnButton(1, true, injected),
                WM_LBUTTONUP => _filter.OnButton(1, false, injected),
                WM_RBUTTONDOWN => _filter.OnButton(2, true, injected),
                WM_RBUTTONUP => _filter.OnButton(2, false, injected),
                WM_MBUTTONDOWN => _filter.OnButton(3, true, injected),
                WM_MBUTTONUP => _filter.OnButton(3, false, injected),
                WM_XBUTTONDOWN => _filter.OnButton(4 + (int)(info.mouseData >> 16), true, injected),
                WM_XBUTTONUP => _filter.OnButton(4 + (int)(info.mouseData >> 16), false, injected),
                WM_MOUSEWHEEL or WM_MOUSEHWHEEL => _filter.OnButton(0, true, injected, isWheel: true),
                _ => false,
            };
            if (drop) return (IntPtr)1;
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    // Off the hook thread: the abort cancels the sweep, whose finally disposes this guard.
    private static void Raise(HotkeyKind kind, uint vk)
    {
        var name = kind == HotkeyKind.Breakglass ? "Ctrl+Shift+F11" : vk == SweepKeyFilter.VK_ESCAPE ? "Esc" : "Ctrl+Shift+F12";
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { ChordPressed?.Invoke(kind, $"{name} during a turbo sweep"); }
            catch { }
        });
    }

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private sealed class NoGuard : IDisposable
    {
        public static readonly NoGuard Instance = new();
        public void Dispose() { }
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT { public int x, y; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, HookProc fn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr SetTimer(IntPtr hwnd, IntPtr id, uint ms, IntPtr fn);
    [DllImport("user32.dll")] private static extern bool KillTimer(IntPtr hwnd, IntPtr id);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
