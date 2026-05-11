using System.Runtime.InteropServices;

namespace Labs626.UrTask.Hotkeys;

/// <summary>
/// Registers F8 (record toggle) + F5 (play) + Esc (abort) as Win32 global
/// hotkeys, fires <see cref="HotkeyPressed"/> on each. Listens on its own
/// background thread with a message pump — same pattern as MacroRecorder,
/// because RegisterHotKey delivers WM_HOTKEY through the thread's message
/// queue and needs a GetMessage loop somewhere.
///
/// Configurable hotkey assignment lands in v0.2; v0.1 ships fixed F8/F5/Esc
/// to match TinyTask muscle memory. The vkCodes are exposed via
/// <see cref="RegisteredVkCodes"/> so MacroRecorder can filter them out at
/// recording time (otherwise pressing F8 to stop a recording would bake
/// the F8 keypress into the macro).
/// </summary>
internal sealed class HotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int WM_QUIT = 0x0012;

    // VK codes
    private const int VK_F5 = 0x74;
    private const int VK_F8 = 0x77;
    private const int VK_ESCAPE = 0x1B;

    // Hotkey ids — opaque to Win32; we use them to discriminate WM_HOTKEY wParam.
    private const int ID_RECORD_TOGGLE = 1;
    private const int ID_PLAY = 2;
    private const int ID_ABORT = 3;

    private readonly object _lock = new();
    private Thread? _thread;
    private uint _threadId;
    private ManualResetEventSlim? _readySignal;
    private Exception? _startError;
    private volatile bool _running;

    public event Action<HotkeyKind>? HotkeyPressed;

    /// <summary>VK codes the service has registered. MacroRecorder consults this to skip them at record time.</summary>
    public static IReadOnlyCollection<int> RegisteredVkCodes { get; } = new[] { VK_F8, VK_F5, VK_ESCAPE };

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

    private void MessageLoop()
    {
        _threadId = GetCurrentThreadId();
        var registered = new List<int>();
        try
        {
            if (!RegisterHotKey(IntPtr.Zero, ID_RECORD_TOGGLE, 0, VK_F8))
                throw new InvalidOperationException($"RegisterHotKey(F8) failed, win32 error {Marshal.GetLastWin32Error()}");
            registered.Add(ID_RECORD_TOGGLE);

            if (!RegisterHotKey(IntPtr.Zero, ID_PLAY, 0, VK_F5))
                throw new InvalidOperationException($"RegisterHotKey(F5) failed, win32 error {Marshal.GetLastWin32Error()}");
            registered.Add(ID_PLAY);

            if (!RegisterHotKey(IntPtr.Zero, ID_ABORT, 0, VK_ESCAPE))
                throw new InvalidOperationException($"RegisterHotKey(Esc) failed, win32 error {Marshal.GetLastWin32Error()}");
            registered.Add(ID_ABORT);

            _running = true;
            _readySignal!.Set();

            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == WM_HOTKEY)
                {
                    var kind = msg.wParam.ToInt32() switch
                    {
                        ID_RECORD_TOGGLE => HotkeyKind.RecordToggle,
                        ID_PLAY => HotkeyKind.Play,
                        ID_ABORT => HotkeyKind.Abort,
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

internal enum HotkeyKind { RecordToggle, Play, Abort }
