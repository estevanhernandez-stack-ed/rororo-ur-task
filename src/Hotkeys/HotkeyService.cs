using System.Runtime.InteropServices;

namespace Labs626.UrTask.Hotkeys;

/// <summary>
/// Registers Ctrl+Shift+R (record toggle), Ctrl+Shift+P (play assignments),
/// Ctrl+Shift+L (run routine), and Ctrl+Shift+A (abort) as Win32 global hotkeys
/// for the plugin's lifetime. Bare Esc is ALSO an abort key, but registered on
/// demand only while a macro plays (EnableAbortKey/DisableAbortKey) — a
/// permanent bare-Esc grab swallows Esc system-wide for every app whenever the
/// plugin runs. v0.1 used bare F8/F5/Esc, which hijacked those keys system-wide
/// (browser refresh, IDE reload, Roblox Studio play); v0.2 moved record/play to
/// chords; v0.3.1 closed the same hole on abort.
///
/// Listens on its own background thread with a message pump — RegisterHotKey
/// delivers WM_HOTKEY through the thread's message queue and needs a GetMessage
/// loop somewhere. That loop also handles WM_ENABLE_ABORT / WM_DISABLE_ABORT so
/// the on-demand bare-Esc (un)registration runs on this thread.
///
/// Future: hotkey rebinding as a settings panel.
/// </summary>
internal sealed class HotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int WM_QUIT = 0x0012;
    // Custom thread messages to toggle the on-demand bare-Esc abort hotkey.
    // RegisterHotKey is thread-affine (the hotkey binds to the calling thread's
    // queue), so callers post these and the pump thread does the (un)registration.
    private const uint WM_ENABLE_ABORT = 0x8000 + 1;  // WM_APP + 1
    private const uint WM_DISABLE_ABORT = 0x8000 + 2; // WM_APP + 2

    // Modifier flags for RegisterHotKey
    private const uint MOD_NONE    = 0x0000;
    private const uint MOD_ALT     = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT   = 0x0004;
    private const uint MOD_WIN     = 0x0008;

    // VK codes
    private const int VK_R = 0x52;
    private const int VK_P = 0x50;
    private const int VK_A = 0x41;
    private const int VK_L = 0x4C;
    private const int VK_ESCAPE = 0x1B;

    // Hotkey ids — opaque to Win32; we use them to discriminate WM_HOTKEY wParam.
    private const int ID_RECORD_TOGGLE = 1;
    private const int ID_PLAY = 2;
    private const int ID_ABORT_BARE = 3;   // bare Esc — registered only while a macro plays
    private const int ID_ABORT_CHORD = 4;  // Ctrl+Shift+A — registered for the plugin's lifetime
    private const int ID_RUN_ROUTINE = 5;  // Ctrl+Shift+L — run the selected recipe/loadout routine

    private readonly object _lock = new();
    private Thread? _thread;
    private uint _threadId;
    private ManualResetEventSlim? _readySignal;
    private Exception? _startError;
    private volatile bool _running;

    public event Action<HotkeyKind>? HotkeyPressed;

    /// <summary>
    /// VK codes the service has registered as chord hotkeys. MacroRecorder consults
    /// this to skip them at record time — but only when Ctrl+Shift is held
    /// (otherwise lowercase 'r'/'p' typing would get eaten).
    /// </summary>
    public static IReadOnlyCollection<int> ChordHotkeyVkCodes { get; } = new[] { VK_R, VK_P, VK_A, VK_L };

    /// <summary>VK code for the bare Esc hotkey — always filtered at record time.</summary>
    public static int AbortVkCode => VK_ESCAPE;

    public void Start()
    {
        lock (_lock)
        {
            if (_running) throw new InvalidOperationException("Hotkey service already started.");
            _readySignal = new ManualResetEventSlim(false);
            _startError = null;
            _thread = new Thread(MessageLoop)
            {
                IsBackground = true,
                Name = "RoRoRoUrTask-Hotkeys",
            };
            _thread.Start();
        }
        _readySignal!.Wait();
        if (_startError is not null)
            throw new InvalidOperationException("Failed to register hotkeys.", _startError);
    }

    public void Dispose()
    {
        Thread? thread;
        uint threadId;
        lock (_lock)
        {
            if (!_running) return;
            thread = _thread;
            threadId = _threadId;
            _thread = null;
            _threadId = 0;
            _running = false;
        }
        if (threadId != 0) PostThreadMessage(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        thread?.Join();
    }

    /// <summary>
    /// Register the bare-Esc abort hotkey. Call when playback starts. No-op if
    /// already enabled or the service isn't running. Registration runs on the
    /// pump thread (RegisterHotKey is thread-affine), so this posts a message and
    /// returns immediately — Esc only steals input from other apps while a macro
    /// is actually playing, never the rest of the time.
    /// </summary>
    public void EnableAbortKey() => PostAbortToggle(WM_ENABLE_ABORT);

    /// <summary>
    /// Unregister the bare-Esc abort hotkey so Esc is free for other apps. Call
    /// when playback ends. No-op if not enabled or the service isn't running.
    /// </summary>
    public void DisableAbortKey() => PostAbortToggle(WM_DISABLE_ABORT);

    private void PostAbortToggle(uint message)
    {
        uint threadId;
        lock (_lock)
        {
            if (!_running) return;
            threadId = _threadId;
        }
        if (threadId != 0) PostThreadMessage(threadId, message, IntPtr.Zero, IntPtr.Zero);
    }

    private void MessageLoop()
    {
        _threadId = GetCurrentThreadId();
        var registered = new List<int>();
        bool abortKeyRegistered = false;
        try
        {
            const uint chord = MOD_CONTROL | MOD_SHIFT;

            if (!RegisterHotKey(IntPtr.Zero, ID_RECORD_TOGGLE, chord, VK_R))
                throw new InvalidOperationException($"RegisterHotKey(Ctrl+Shift+R) failed, win32 error {Marshal.GetLastWin32Error()}");
            registered.Add(ID_RECORD_TOGGLE);

            if (!RegisterHotKey(IntPtr.Zero, ID_PLAY, chord, VK_P))
                throw new InvalidOperationException($"RegisterHotKey(Ctrl+Shift+P) failed, win32 error {Marshal.GetLastWin32Error()}");
            registered.Add(ID_PLAY);

            // Abort is a chord (Ctrl+Shift+A) so it never hijacks a bare key.
            // Bare Esc is ALSO an abort key, but registered on demand only while a
            // macro plays (see EnableAbortKey/WM_ENABLE_ABORT). v0.3.0 and earlier
            // registered bare Esc here for the whole session, which swallowed Esc
            // system-wide for every app whenever the plugin was running.
            if (!RegisterHotKey(IntPtr.Zero, ID_ABORT_CHORD, chord, VK_A))
                throw new InvalidOperationException($"RegisterHotKey(Ctrl+Shift+A) failed, win32 error {Marshal.GetLastWin32Error()}");
            registered.Add(ID_ABORT_CHORD);

            if (!RegisterHotKey(IntPtr.Zero, ID_RUN_ROUTINE, chord, VK_L))
                throw new InvalidOperationException($"RegisterHotKey(Ctrl+Shift+L) failed, win32 error {Marshal.GetLastWin32Error()}");
            registered.Add(ID_RUN_ROUTINE);

            _running = true;
            _readySignal!.Set();

            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == WM_ENABLE_ABORT)
                {
                    if (!abortKeyRegistered)
                        abortKeyRegistered = RegisterHotKey(IntPtr.Zero, ID_ABORT_BARE, MOD_NONE, VK_ESCAPE);
                    continue;
                }
                if (msg.message == WM_DISABLE_ABORT)
                {
                    if (abortKeyRegistered)
                    {
                        UnregisterHotKey(IntPtr.Zero, ID_ABORT_BARE);
                        abortKeyRegistered = false;
                    }
                    continue;
                }
                if (msg.message == WM_HOTKEY)
                {
                    var kind = msg.wParam.ToInt32() switch
                    {
                        ID_RECORD_TOGGLE => HotkeyKind.RecordToggle,
                        ID_PLAY => HotkeyKind.Play,
                        ID_ABORT_BARE => HotkeyKind.Abort,
                        ID_ABORT_CHORD => HotkeyKind.Abort,
                        ID_RUN_ROUTINE => HotkeyKind.RunRoutine,
                        _ => (HotkeyKind?)null,
                    };
                    if (kind is not null)
                    {
                        try { HotkeyPressed?.Invoke(kind.Value); }
                        catch { /* swallow — handler exceptions shouldn't kill the pump */ }
                    }
                }
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        catch (Exception ex)
        {
            _startError = ex;
            _readySignal!.Set();
        }
        finally
        {
            if (abortKeyRegistered) UnregisterHotKey(IntPtr.Zero, ID_ABORT_BARE);
            foreach (var id in registered) UnregisterHotKey(IntPtr.Zero, id);
        }
    }

    // ---------- Win32 interop ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

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

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

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

internal enum HotkeyKind { RecordToggle, Play, Abort, RunRoutine }
