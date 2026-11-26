using Labs626.UrTask.Hotkeys;

namespace Labs626.UrTask.Tests;

public class HotkeyServiceTests
{
    // Hotkey ids as HotkeyService registers them.
    private const int Record = 1, Play = 2, BareEsc = 3, AbortChord = 4, Routine = 5, Breakglass = 6;

    /// <summary>Stands in for RegisterHotKey / UnregisterHotKey, so no test touches a real
    /// system-wide hotkey. The two tests that used the real API failed on any machine with Ur
    /// Task running (win32 error 1409), and on a free machine they grabbed the user's keys.</summary>
    private sealed class FakeHotkeys
    {
        private readonly object _lock = new();
        private readonly HashSet<int> _held = new();
        private readonly List<int> _registerCalls = new();
        private readonly Dictionary<int, int> _fail = new();

        /// <summary>Make registering <paramref name="id"/> fail with a Win32 error, or work again with 0.</summary>
        public void Fail(int id, int win32Error) { lock (_lock) { if (win32Error == 0) _fail.Remove(id); else _fail[id] = win32Error; } }

        public int[] Held { get { lock (_lock) return _held.OrderBy(i => i).ToArray(); } }
        public int RegisterCallsFor(int id) { lock (_lock) return _registerCalls.Count(i => i == id); }

        public int Register(int id, uint modifiers, uint vk)
        {
            lock (_lock)
            {
                _registerCalls.Add(id);
                if (_fail.TryGetValue(id, out var error)) return error;
                return _held.Add(id) ? 0 : 1409; // a second registration of a held key fails, as in Windows
            }
        }

        public void Unregister(int id) { lock (_lock) _held.Remove(id); }

        public HotkeyService NewService() => new(Register, Unregister);
    }

    private static void WaitFor(Func<bool> cond) => Assert.True(SpinWait.SpinUntil(cond, 5000));

    [Fact]
    public void Start_RegistersChordHotkeysAndDisposeCleansUp()
    {
        var keys = new FakeHotkeys();
        var svc = keys.NewService();
        // Start should not throw — registers Ctrl+Shift+R, Ctrl+Shift+P, Ctrl+Shift+L,
        // Ctrl+Shift+F12, Ctrl+Shift+F11. Bare Esc is registered on demand, not here.
        svc.Start();

        try
        {
            Assert.Equal(new[] { Record, Play, AbortChord, Routine, Breakglass }, keys.Held);
            Assert.Empty(svc.Failures);

            // Dispose should unregister cleanly. Re-starting after dispose should also work.
            svc.Dispose();
            Assert.Empty(keys.Held);
            using var svc2 = keys.NewService();
            svc2.Start();
            Assert.Equal(new[] { Record, Play, AbortChord, Routine, Breakglass }, keys.Held);
        }
        finally
        {
            svc.Dispose();
        }
    }

    [Fact]
    public void Start_OneChordHeldByAnotherProgram_KeepsTheOthersAndReportsTheOneThatFailed()
    {
        // L3-04: any single RegisterHotKey failure threw, and the finally unregistered the chords
        // that had succeeded: no hotkeys at all, the abort chord included.
        var keys = new FakeHotkeys();
        keys.Fail(Routine, 1409);
        using var svc = keys.NewService();

        svc.Start(); // does not throw

        Assert.Equal(new[] { Record, Play, AbortChord, Breakglass }, keys.Held);
        Assert.Equal(new[] { new HotkeyFailure("Ctrl+Shift+L", 1409) }, svc.Failures);
    }

    [Fact]
    public void Start_EveryChordHeld_StillStartsAndReportsEachOne()
    {
        var keys = new FakeHotkeys();
        foreach (var id in new[] { Record, Play, AbortChord, Routine, Breakglass }) keys.Fail(id, 1409);
        using var svc = keys.NewService();

        svc.Start();

        Assert.Empty(keys.Held);
        Assert.Equal(new[] { "Ctrl+Shift+R", "Ctrl+Shift+P", "Ctrl+Shift+F12", "Ctrl+Shift+L", "Ctrl+Shift+F11" },
            svc.Failures.Select(f => f.Key));
        // The pump still runs, so bare Esc can still be taken for a playback.
        svc.EnableAbortKey();
        WaitFor(() => keys.Held.SequenceEqual(new[] { BareEsc }));
    }

    [Fact]
    public void Dispose_UnregistersOnlyWhatRegistered()
    {
        var keys = new FakeHotkeys();
        keys.Fail(Play, 5);
        var svc = keys.NewService();
        svc.Start();
        svc.EnableAbortKey();
        WaitFor(() => keys.Held.Contains(BareEsc));

        svc.Dispose();

        Assert.Empty(keys.Held);
    }

    [Fact]
    public void EnableAbortKey_EscHeldByAnotherProgram_SaysSoOncePerPlayback()
    {
        // The result of the on-demand Esc registration was dropped, while the activity log
        // promised "Esc ... to stop".
        var keys = new FakeHotkeys();
        keys.Fail(BareEsc, 1409);
        using var svc = keys.NewService();
        var failures = new List<HotkeyFailure>();
        svc.AbortKeyFailed += f => { lock (failures) failures.Add(f); };
        svc.Start();

        // One playback asks several times (the sequence, the player, each refresh): one report.
        svc.EnableAbortKey();
        svc.EnableAbortKey();
        svc.EnableAbortKey();
        WaitFor(() => keys.RegisterCallsFor(BareEsc) == 3);
        lock (failures) Assert.Equal(new[] { new HotkeyFailure("Esc", 1409) }, failures);

        // The playback ends, the next one asks again: reported again.
        svc.DisableAbortKey();
        svc.EnableAbortKey();
        WaitFor(() => { lock (failures) return failures.Count == 2; });

        // And once Esc is free it registers, with nothing more to report.
        keys.Fail(BareEsc, 0);
        svc.EnableAbortKey();
        WaitFor(() => keys.Held.Contains(BareEsc));
        lock (failures) Assert.Equal(2, failures.Count);
    }

    [Fact]
    public void The_lines_for_a_hotkey_that_did_not_register_say_which_why_and_what_still_works()
    {
        var held = new HotkeyFailure("Ctrl+Shift+L", 1409);
        Assert.Equal("Couldn't register Ctrl+Shift+L: another program is using it (Windows error 1409). The other hotkeys still work.",
            held.StartupLine(othersWork: true));
        Assert.Equal("Couldn't register Ctrl+Shift+L: another program is using it (Windows error 1409). None of the hotkeys work, so use the buttons in the Ur Task window.",
            held.StartupLine(othersWork: false));
        Assert.Equal("Couldn't register Ctrl+Shift+P: Windows refused it (Windows error 5). The other hotkeys still work.",
            new HotkeyFailure("Ctrl+Shift+P", 5).StartupLine(othersWork: true));

        var esc = new HotkeyFailure("Esc", 1409);
        Assert.Equal("Esc can't stop this playback: another program is using it (Windows error 1409). Press Ctrl+Shift+F12 to stop it.",
            esc.AbortKeyLine(abortChordWorks: true));
        Assert.Equal("Esc can't stop this playback: another program is using it (Windows error 1409). Use the stop button in the Ur Task window.",
            esc.AbortKeyLine(abortChordWorks: false));
    }

    [Fact]
    public void ChordHotkeyVkCodes_ContainsRAndP()
    {
        Assert.Contains(0x52, HotkeyService.ChordHotkeyVkCodes); // VK_R
        Assert.Contains(0x50, HotkeyService.ChordHotkeyVkCodes); // VK_P
        Assert.DoesNotContain(0x77, HotkeyService.ChordHotkeyVkCodes); // VK_F8 — retired
    }

    [Fact]
    public void ChordHotkeyVkCodes_ContainsAbortChord_F12()
    {
        // Ctrl+Shift+F12 is the always-on abort hotkey — the recorder must filter
        // it like the other chords so it isn't baked into a macro.
        Assert.Contains(0x7B, HotkeyService.ChordHotkeyVkCodes); // VK_F12
        Assert.DoesNotContain(0x41, HotkeyService.ChordHotkeyVkCodes); // VK_A: relayed typing produced Ctrl+Shift+A
    }

    [Fact]
    public void ChordHotkeyVkCodes_ContainsRunRoutineChord_L()
    {
        // Ctrl+Shift+L runs the selected routine (recipe/loadout) — the
        // recorder must filter it like the other chords so it isn't baked
        // into a macro.
        Assert.Contains(0x4C, HotkeyService.ChordHotkeyVkCodes); // VK_L
    }

    [Fact]
    public void AbortVkCode_IsEscape()
    {
        Assert.Equal(0x1B, HotkeyService.AbortVkCode);
    }

    [Fact]
    public void EnableAndDisableAbortKey_AfterStart_AreIdempotentAndDoNotThrow()
    {
        var keys = new FakeHotkeys();
        using var svc = keys.NewService();
        svc.Start();

        // Bare Esc is NOT registered at start — only while a macro is playing.
        // Enabling registers it on the pump thread; both calls are idempotent
        // and must never throw regardless of current registration state.
        Assert.DoesNotContain(BareEsc, keys.Held);
        svc.EnableAbortKey();
        svc.EnableAbortKey();
        WaitFor(() => keys.Held.Contains(BareEsc));
        Assert.Equal(1, keys.RegisterCallsFor(BareEsc)); // the second enable found it registered
        svc.DisableAbortKey();
        svc.DisableAbortKey();
        WaitFor(() => !keys.Held.Contains(BareEsc));
    }

    [Fact]
    public void Every_hotkey_registers_with_no_repeat_so_a_held_chord_fires_once()
    {
        // Without MOD_NOREPEAT Windows posts WM_HOTKEY again on every typematic repeat. Record,
        // play and break-glass are toggles, so a chord held half a second started, stopped and
        // started again (L3-01).
        const uint MOD_NOREPEAT = 0x4000;
        Assert.Equal(MOD_NOREPEAT, HotkeyService.ChordModifiers & MOD_NOREPEAT);
        Assert.Equal(MOD_NOREPEAT, HotkeyService.AbortKeyModifiers & MOD_NOREPEAT);
        // Still Ctrl+Shift for the chords, and still a bare key for Esc.
        Assert.Equal(0x0002u | 0x0004u, HotkeyService.ChordModifiers & ~MOD_NOREPEAT);
        Assert.Equal(0u, HotkeyService.AbortKeyModifiers & ~MOD_NOREPEAT);
    }

    [Fact]
    public void EnableDisableAbortKey_BeforeStart_AreNoOps()
    {
        using var svc = new HotkeyService();
        // No pump thread yet — calls are safely ignored, never throw.
        svc.EnableAbortKey();
        svc.DisableAbortKey();
    }
}
