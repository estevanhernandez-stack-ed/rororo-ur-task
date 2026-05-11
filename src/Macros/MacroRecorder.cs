using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Labs626.UrTask.Macros;

/// <summary>
/// Captures keyboard + mouse events via Win32 low-level hooks (WH_KEYBOARD_LL +
/// WH_MOUSE_LL). Hooks must run on a thread with a message pump — kernel
/// injects callbacks through GetMessage — so the recorder owns a dedicated
/// background thread for its lifetime.
///
/// Threading: <see cref="Start"/> spins the message-loop thread and waits for
/// the hooks to install. The hook callbacks run on that thread and append to
/// <c>_events</c>. <see cref="Stop"/> posts WM_QUIT to break the loop, joins
/// the thread, and returns the captured event list (no live readers while
/// recording — captured events are only read after Stop completes).
/// </summary>
public sealed class MacroRecorder
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;
    private const int WM_QUIT = 0x0012;

    // Keyboard messages
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    // Mouse messages
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_MBUTTONUP = 0x0208;
    private const int WM_MOUSEWHEEL = 0x020A;
    private const int WM_XBUTTONDOWN = 0x020B;
    private const int WM_XBUTTONUP = 0x020C;

    private readonly object _lock = new();
    private List<MacroEvent>? _events;
    private Stopwatch? _clock;
    private HashSet<int>? _ignoredVkCodes;
    private Thread? _hookThread;
    private uint _hookThreadId;
    private IntPtr _keyboardHook = IntPtr.Zero;
    private IntPtr _mouseHook = IntPtr.Zero;
    private HookProc? _keyboardProc;
    private HookProc? _mouseProc;
    private ManualResetEventSlim? _readySignal;
    private Exception? _installError;

    /// <summary>True between <see cref="Start"/> and <see cref="Stop"/>.</summary>
    public bool IsRecording => _events is not null;

    /// <summary>
    /// Begin recording. Blocks until the hooks are installed (or installation
    /// fails). Throws on installation failure so the caller can surface it
    /// rather than recording into a black hole.
    ///
    /// <paramref name="ignoredVkCodes"/> — vkCodes (typically the hotkeys
    /// registered with HotkeyService) that the recorder will silently drop
    /// rather than capture. Without this, pressing F8 to stop recording
    /// would bake the F8 keystroke into the macro and playback would echo
    /// it back, triggering record-stop mid-playback.
    /// </summary>
    public void Start(IReadOnlyCollection<int>? ignoredVkCodes = null)
    {
        lock (_lock)
        {
            if (IsRecording) throw new InvalidOperationException("Already recording.");

            _events = new List<MacroEvent>(capacity: 1024);
            _clock = Stopwatch.StartNew();
            _ignoredVkCodes = ignoredVkCodes is { Count: > 0 }
                ? new HashSet<int>(ignoredVkCodes)
                : null;
            _readySignal = new ManualResetEventSlim(false);
            _installError = null;

            _hookThread = new Thread(MessageLoop)
            {
                IsBackground = true,
                Name = "RoRoRoUrTask-MacroRecorder",
            };
            _hookThread.Start();
        }

        _readySignal!.Wait();
        if (_installError is not null)
        {
            // Surface the install error and clean up partial state.
            var err = _installError;
            _events = null;
            _clock = null;
            throw new InvalidOperationException("Failed to install hooks.", err);
        }
    }

    /// <summary>
    /// Stop recording, join the hook thread, and return the captured event
    /// list. Safe to call when not recording — returns an empty list.
    /// </summary>
    public IReadOnlyList<MacroEvent> Stop()
    {
        Thread? thread;
        List<MacroEvent>? captured;
        uint threadId;
        lock (_lock)
        {
            if (!IsRecording) return Array.Empty<MacroEvent>();
            thread = _hookThread;
            captured = _events;
            threadId = _hookThreadId;
            _events = null;
            _clock = null;
            _hookThread = null;
            _hookThreadId = 0;
        }

        // Post WM_QUIT to break GetMessage; the loop unhooks before returning.
        if (threadId != 0) PostThreadMessage(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        thread?.Join();

        return captured ?? (IReadOnlyList<MacroEvent>)Array.Empty<MacroEvent>();
    }

    private void MessageLoop()
    {
        _hookThreadId = GetCurrentThreadId();
        try
        {
            // Hold delegate references in fields so the GC doesn't collect them
            // while the kernel is holding the function pointer.
            _keyboardProc = OnKeyboardEvent;
            _mouseProc = OnMouseEvent;

            _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, IntPtr.Zero, 0);
            if (_keyboardHook == IntPtr.Zero)
                throw new InvalidOperationException($"SetWindowsHookEx(WH_KEYBOARD_LL) failed, win32 error {Marshal.GetLastWin32Error()}");

            _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, IntPtr.Zero, 0);
            if (_mouseHook == IntPtr.Zero)
            {
                UnhookWindowsHookEx(_keyboardHook);
                _keyboardHook = IntPtr.Zero;
                throw new InvalidOperationException($"SetWindowsHookEx(WH_MOUSE_LL) failed, win32 error {Marshal.GetLastWin32Error()}");
            }

            _readySignal!.Set();

            // Standard Win32 message pump. Exits when WM_QUIT is dispatched.
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        catch (Exception ex)
        {
            _installError = ex;
            _readySignal!.Set();
        }
        finally
        {
            if (_keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(_keyboardHook);
            if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
            _keyboardHook = IntPtr.Zero;
            _mouseHook = IntPtr.Zero;
        }
    }

    private IntPtr OnKeyboardEvent(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _events is not null && _clock is not null)
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            var vkCode = (int)info.vkCode;

            // Drop the configured hotkeys (F8 / F5 / Esc) so pressing them to
            // control the plugin doesn't bake the keystroke into the macro.
            if (_ignoredVkCodes is null || !_ignoredVkCodes.Contains(vkCode))
            {
                var msg = wParam.ToInt32();
                MacroEventKind? kind = msg switch
                {
                    WM_KEYDOWN or WM_SYSKEYDOWN => MacroEventKind.KeyDown,
                    WM_KEYUP or WM_SYSKEYUP => MacroEventKind.KeyUp,
                    _ => null,
                };
                if (kind is not null)
                {
                    _events.Add(new MacroEvent(
                        TimestampMs: _clock.ElapsedMilliseconds,
                        Kind: kind.Value,
                        VirtualKeyCode: vkCode,
                        X: 0, Y: 0, MouseButton: 0, WheelDelta: 0));
                }
            }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private IntPtr OnMouseEvent(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _events is not null && _clock is not null)
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            var msg = wParam.ToInt32();
            switch (msg)
            {
                case WM_MOUSEMOVE:
                    _events.Add(new MacroEvent(_clock.ElapsedMilliseconds,
                        MacroEventKind.MouseMove, 0, info.pt.x, info.pt.y, 0, 0));
                    break;
                case WM_LBUTTONDOWN:
                    _events.Add(new MacroEvent(_clock.ElapsedMilliseconds,
                        MacroEventKind.MouseDown, 0, info.pt.x, info.pt.y, 1, 0));
                    break;
                case WM_LBUTTONUP:
                    _events.Add(new MacroEvent(_clock.ElapsedMilliseconds,
                        MacroEventKind.MouseUp, 0, info.pt.x, info.pt.y, 1, 0));
                    break;
                case WM_RBUTTONDOWN:
                    _events.Add(new MacroEvent(_clock.ElapsedMilliseconds,
                        MacroEventKind.MouseDown, 0, info.pt.x, info.pt.y, 2, 0));
                    break;
                case WM_RBUTTONUP:
                    _events.Add(new MacroEvent(_clock.ElapsedMilliseconds,
                        MacroEventKind.MouseUp, 0, info.pt.x, info.pt.y, 2, 0));
                    break;
                case WM_MBUTTONDOWN:
                    _events.Add(new MacroEvent(_clock.ElapsedMilliseconds,
                        MacroEventKind.MouseDown, 0, info.pt.x, info.pt.y, 3, 0));
                    break;
                case WM_MBUTTONUP:
                    _events.Add(new MacroEvent(_clock.ElapsedMilliseconds,
                        MacroEventKind.MouseUp, 0, info.pt.x, info.pt.y, 3, 0));
                    break;
                case WM_MOUSEWHEEL:
                    // High word of mouseData is the wheel delta (signed short).
                    var delta = (short)((info.mouseData >> 16) & 0xFFFF);
                    _events.Add(new MacroEvent(_clock.ElapsedMilliseconds,
                        MacroEventKind.MouseWheel, 0, info.pt.x, info.pt.y, 0, delta));
                    break;
                case WM_XBUTTONDOWN:
                case WM_XBUTTONUP:
                    // High word of mouseData identifies which X button (1 or 2).
                    var xbtn = (short)((info.mouseData >> 16) & 0xFFFF);
                    var btnId = 3 + xbtn; // 4 = X1, 5 = X2
                    var k = msg == WM_XBUTTONDOWN ? MacroEventKind.MouseDown : MacroEventKind.MouseUp;
                    _events.Add(new MacroEvent(_clock.ElapsedMilliseconds,
                        k, 0, info.pt.x, info.pt.y, btnId, 0));
                    break;
            }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    // ---------- Win32 interop ----------

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
