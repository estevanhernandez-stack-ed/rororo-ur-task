using System.Globalization;
using System.Runtime.InteropServices;

namespace Labs626.UrTask.Hotkeys;

/// <summary>
/// Registers Ctrl+Shift+R (record toggle), Ctrl+Shift+P (play assignments),
/// Ctrl+Shift+L (run routine), Ctrl+Shift+F12 (abort) and Ctrl+Shift+F11 (break-glass:
/// "I have the wheel", see <see cref="Breakglass"/>) as Win32 global hotkeys for the
/// plugin's lifetime. Bare Esc is ALSO an abort key, but registered on
/// demand only while a macro plays (EnableAbortKey/DisableAbortKey) — a
/// permanent bare-Esc grab swallows Esc system-wide for every app whenever the
/// plugin runs. v0.1 used bare F8/F5/Esc, which hijacked those keys system-wide
/// (browser refresh, IDE reload, Roblox Studio play); v0.2 moved record/play to
/// chords; v0.3.1 closed the same hole on abort. v0.12.0 moved the abort chord
/// from Ctrl+Shift+A to Ctrl+Shift+F12: typing relayed through Mouse Without
/// Borders or Chrome Remote Desktop can leave Ctrl and Shift held, and the next
/// 'a' typed then arrives as Ctrl+Shift+A (live 2026-09-30, it stopped the pulse
/// loop three times). Typing never produces F12.
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
    // Without this Windows posts WM_HOTKEY again on every typematic repeat while the key is
    // held. Record, play and break-glass are toggles: a chord held half a second started,
    // stopped and started again.
    private const uint MOD_NOREPEAT = 0x4000;

    /// <summary>The modifier flags every chord hotkey registers with, and the flags bare Esc
    /// registers with. Exposed so a test can hold them to MOD_NOREPEAT: RegisterHotKey itself
    /// cannot be read back.</summary>
    internal const uint ChordModifiers = MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT;
    internal const uint AbortKeyModifiers = MOD_NONE | MOD_NOREPEAT;

    // VK codes
    private const int VK_R = 0x52;
    private const int VK_P = 0x50;
    private const int VK_F12 = 0x7B;
    private const int VK_F11 = 0x7A;
    private const int VK_L = 0x4C;
    private const int VK_ESCAPE = 0x1B;

    // Hotkey ids — opaque to Win32; we use them to discriminate WM_HOTKEY wParam.
    private const int ID_RECORD_TOGGLE = 1;
    private const int ID_PLAY = 2;
    private const int ID_ABORT_BARE = 3;   // bare Esc — registered only while a macro plays
    private const int ID_ABORT_CHORD = 4;  // Ctrl+Shift+F12 — registered for the plugin's lifetime
    private const int ID_RUN_ROUTINE = 5;  // Ctrl+Shift+L — run the selected recipe/loadout routine
    private const int ID_BREAKGLASS = 6;   // Ctrl+Shift+F11 — toggle break-glass (the user has the wheel)

    // The chords registered for the plugin's lifetime, in the order they are registered.
    private static readonly (int Id, int Vk)[] Chords =
    {
        (ID_RECORD_TOGGLE, VK_R), (ID_PLAY, VK_P), (ID_ABORT_CHORD, VK_F12), (ID_RUN_ROUTINE, VK_L), (ID_BREAKGLASS, VK_F11),
    };

    // The seam over RegisterHotKey / UnregisterHotKey: register returns 0 or the Win32 error.
    // Tests inject a fake, so none of them touches a real system-wide hotkey.
    private readonly Func<int, uint, uint, int> _register;
    private readonly Action<int> _unregister;

    public HotkeyService() : this(RegisterReal, UnregisterReal) { }

    internal HotkeyService(Func<int, uint, uint, int> register, Action<int> unregister)
    {
        _register = register ?? throw new ArgumentNullException(nameof(register));
        _unregister = unregister ?? throw new ArgumentNullException(nameof(unregister));
    }

    private static int RegisterReal(int id, uint modifiers, uint vk)
    {
        if (RegisterHotKey(IntPtr.Zero, id, modifiers, vk)) return 0;
        var error = Marshal.GetLastWin32Error();
        return error != 0 ? error : -1;
    }

    private static void UnregisterReal(int id) => UnregisterHotKey(IntPtr.Zero, id);

    /// <summary>The hotkeys that could not be registered at <see cref="Start"/>. Empty until then.
    /// One chord another program holds no longer costs the rest: each registers on its own.</summary>
    public IReadOnlyList<HotkeyFailure> Failures { get; private set; } = Array.Empty<HotkeyFailure>();

    /// <summary>Bare Esc could not be registered when playback asked for it. Raised on the pump
    /// thread, once per playback that asked, however many times it asked.</summary>
    public event Action<HotkeyFailure>? AbortKeyFailed;

    private readonly object _lock = new();
    private Thread? _thread;
    private uint _threadId;
    private ManualResetEventSlim? _readySignal;
    private Exception? _startError;
    private volatile bool _running;

    /// <summary>A registered hotkey fired: its kind, and where it came from (AbortLog.Source): the
    /// key's name, and for an abort key also what was in front at that moment, read on this pump
    /// thread as the WM_HOTKEY arrives. WM_HOTKEY does not say whether the press was injected.</summary>
    public event Action<HotkeyKind, string>? HotkeyPressed;

    /// <summary>The key a hotkey id stands for, as the log names it.</summary>
    internal static string KeyName(int id) => id switch
    {
        ID_RECORD_TOGGLE => "Ctrl+Shift+R",
        ID_PLAY => "Ctrl+Shift+P",
        ID_ABORT_BARE => "Esc",
        ID_ABORT_CHORD => "Ctrl+Shift+F12",
        ID_RUN_ROUTINE => "Ctrl+Shift+L",
        ID_BREAKGLASS => Breakglass.Chord,
        _ => $"hotkey {id}",
    };

    /// <summary>
    /// VK codes the service has registered as chord hotkeys. MacroRecorder consults
    /// this to skip them at record time — but only when Ctrl+Shift is held
    /// (otherwise lowercase 'r'/'p' typing would get eaten).
    /// </summary>
    public static IReadOnlyCollection<int> ChordHotkeyVkCodes { get; } = new[] { VK_R, VK_P, VK_F12, VK_L, VK_F11 };

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
        // A chord that would not register is in Failures, not thrown: this is only the pump
        // thread itself failing to start.
        if (_startError is not null)
            throw new InvalidOperationException("Failed to start the hotkey listener.", _startError);
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
        // One report per playback that asked for Esc: it asks many times (the sequence, the
        // player, every refresh), and each of those retries the registration.
        bool abortKeyFailureReported = false;
        try
        {
            // Each chord registers on its own. One failure used to throw, and the finally then
            // unregistered the chords that had succeeded: one key held by another program cost
            // every hotkey, the abort chord included.
            //
            // Abort is a chord (Ctrl+Shift+F12) so it never hijacks a bare key.
            // Bare Esc is ALSO an abort key, but registered on demand only while a
            // macro plays (see EnableAbortKey/WM_ENABLE_ABORT). v0.3.0 and earlier
            // registered bare Esc here for the whole session, which swallowed Esc
            // system-wide for every app whenever the plugin was running.
            var failures = new List<HotkeyFailure>();
            foreach (var (id, vk) in Chords)
            {
                var error = _register(id, ChordModifiers, (uint)vk);
                if (error == 0) registered.Add(id);
                else failures.Add(new HotkeyFailure(KeyName(id), error));
            }
            Failures = failures;

            _running = true;
            _readySignal!.Set();

            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == WM_ENABLE_ABORT)
                {
                    if (!abortKeyRegistered)
                    {
                        var error = _register(ID_ABORT_BARE, AbortKeyModifiers, VK_ESCAPE);
                        abortKeyRegistered = error == 0;
                        if (abortKeyRegistered) abortKeyFailureReported = false;
                        else if (!abortKeyFailureReported)
                        {
                            abortKeyFailureReported = true;
                            try { AbortKeyFailed?.Invoke(new HotkeyFailure(KeyName(ID_ABORT_BARE), error)); }
                            catch { /* swallow — handler exceptions shouldn't kill the pump */ }
                        }
                    }
                    continue;
                }
                if (msg.message == WM_DISABLE_ABORT)
                {
                    if (abortKeyRegistered)
                    {
                        _unregister(ID_ABORT_BARE);
                        abortKeyRegistered = false;
                    }
                    abortKeyFailureReported = false; // the playback is over; the next one reports afresh
                    continue;
                }
                if (msg.message == WM_HOTKEY)
                {
                    var id = msg.wParam.ToInt32();
                    var kind = id switch
                    {
                        ID_RECORD_TOGGLE => HotkeyKind.RecordToggle,
                        ID_PLAY => HotkeyKind.Play,
                        ID_ABORT_BARE => HotkeyKind.Abort,
                        ID_ABORT_CHORD => HotkeyKind.Abort,
                        ID_RUN_ROUTINE => HotkeyKind.RunRoutine,
                        ID_BREAKGLASS => HotkeyKind.Breakglass,
                        _ => (HotkeyKind?)null,
                    };
                    if (kind is not null)
                    {
                        try
                        {
                            var source = kind is HotkeyKind.Abort or HotkeyKind.Breakglass
                                ? AbortLog.Source(KeyName(id), PluginHost.Win32Focus.DescribeForeground())
                                : KeyName(id);
                            HotkeyPressed?.Invoke(kind.Value, source);
                        }
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
            if (abortKeyRegistered) _unregister(ID_ABORT_BARE);
            foreach (var id in registered) _unregister(id);
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

internal enum HotkeyKind { RecordToggle, Play, Abort, RunRoutine, Breakglass }

/// <summary>A hotkey Windows would not register: the key as the log names it, and the Win32 error.</summary>
internal sealed record HotkeyFailure(string Key, int Win32Error)
{
    private const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;

    /// <summary>Why, in the user's words. 1409 is the one that happens: another program holds the key.</summary>
    private string Why => Win32Error == ERROR_HOTKEY_ALREADY_REGISTERED
        ? string.Create(CultureInfo.InvariantCulture, $"another program is using it (Windows error {Win32Error})")
        : string.Create(CultureInfo.InvariantCulture, $"Windows refused it (Windows error {Win32Error})");

    /// <summary>The activity line for a chord that did not register at startup.</summary>
    /// <param name="othersWork">False when no hotkey registered at all.</param>
    public string StartupLine(bool othersWork)
        => $"Couldn't register {Key}: {Why}. "
           + (othersWork ? "The other hotkeys still work." : "None of the hotkeys work, so use the buttons in the Ur Task window.");

    /// <summary>The activity line when bare Esc could not be registered for a playback.</summary>
    /// <param name="abortChordWorks">Whether Ctrl+Shift+F12 registered, so the line never names a dead key.</param>
    public string AbortKeyLine(bool abortChordWorks)
        => $"Esc can't stop this playback: {Why}. "
           + (abortChordWorks ? "Press Ctrl+Shift+F12 to stop it." : "Use the stop button in the Ur Task window.");
}
