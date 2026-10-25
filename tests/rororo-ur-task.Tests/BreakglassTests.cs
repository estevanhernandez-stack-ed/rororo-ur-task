using System.IO;
using Labs626.UrTask.Hotkeys;
using Labs626.UrTask.Ipc;
using Labs626.UrTask.Macros;
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.Tests;

public class BreakglassTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "urtask-breakglass-" + Guid.NewGuid().ToString("N"));
    private string Flag => Path.Combine(_dir, "breakglass");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Starts_off_when_no_flag_file()
    {
        Assert.False(new Breakglass(Flag).IsOn);
    }

    [Fact]
    public void Toggle_on_writes_the_flag_and_off_removes_it()
    {
        var bg = new Breakglass(Flag);

        var (on, _) = bg.Toggle(keepAlivePlaying: false, new DateTime(2026, 10, 9, 23, 45, 0));
        Assert.True(on);
        Assert.True(bg.IsOn);
        Assert.Equal("2026-10-09T23:45:00", File.ReadAllText(Flag));

        var (on2, _) = bg.Toggle(keepAlivePlaying: false, DateTime.Now);
        Assert.False(on2);
        Assert.False(bg.IsOn);
        Assert.False(File.Exists(Flag));
    }

    [Fact]
    public void Release_restores_keep_alive_only_if_it_was_playing_when_it_went_on()
    {
        var bg = new Breakglass(Flag);
        bg.Toggle(keepAlivePlaying: true, DateTime.Now);
        // The state at release time does not matter: break-glass stopped the loop itself.
        Assert.True(bg.Toggle(keepAlivePlaying: false, DateTime.Now).RestoreKeepAlive);

        bg.Toggle(keepAlivePlaying: false, DateTime.Now);
        Assert.False(bg.Toggle(keepAlivePlaying: true, DateTime.Now).RestoreKeepAlive);
    }

    [Fact]
    public void A_flag_left_from_a_restart_keeps_hands_off()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Flag, "2026-10-09T23:45:00");

        var bg = new Breakglass(Flag);
        Assert.True(bg.IsOn);
        // Released after a restart: nothing remembered to restore, so keep-alive stays as it is.
        var (on, restore) = bg.Toggle(keepAlivePlaying: false, DateTime.Now);
        Assert.False(on);
        Assert.False(restore);
        Assert.False(File.Exists(Flag));
    }

    [Fact]
    public void Lines_name_the_chord_and_what_happened()
    {
        Assert.Equal("Break-glass ON (Ctrl+Shift+F11): you have the wheel. Stopped 2 playback(s), keep-alive paused (comes back on release). The bridge refuses macros until Ctrl+Shift+F11 again.",
            Breakglass.OnLine(2, keepAliveWasPlaying: true));
        Assert.Equal("Break-glass OFF (Ctrl+Shift+F11): hands back. Restarting keep-alive.", Breakglass.OffLine(true));
        Assert.Equal("Break-glass OFF (Ctrl+Shift+F11): hands back.", Breakglass.OffLine(false));
        // Scripts read keep-alive state from "Playing assignments" / "Assignment loop stopped" lines;
        // break-glass lines must never be mistaken for either.
        Assert.DoesNotContain("Playing assignments", Breakglass.OnLine(0, false));
        Assert.DoesNotContain("Playing assignments", Breakglass.OffLine(true));
    }

    [Fact]
    public void F11_is_a_chord_the_recorder_filters()
    {
        Assert.Contains(0x7A, HotkeyService.ChordHotkeyVkCodes); // VK_F11
    }

    // ---------- bridge ----------

    private static Macro NewMacro(string id) => new(
        SchemaVersion: 2, Id: id, Name: "m", RecordMode: "PerWindow",
        RecordedAgainstUserId: null, RecordedAgainstDisplayName: null,
        InterAltDelayMs: null, RecordedAtUnixMs: 0, Events: new List<MacroEvent>());

    private static AccountRegistry.AccountInfo Alt(long userId) => new(1000 + (int)userId, userId, $"alt-{userId}", $"acct-{userId}");

    private static MacroRunInvoker Invoker(Func<bool> isBreakglass, Action? onPlay = null) => new(
        loadMacros: () => new[] { NewMacro("m1") },
        snapshot: () => new[] { Alt(1) },
        resolveForegroundUserId: () => 1L,
        isBusy: () => false,
        playWithResult: (m, t, d, ct) => { onPlay?.Invoke(); return Task.FromResult<SequenceResult?>(null); },
        isBreakglass: isBreakglass);

    [Fact]
    public async Task Bridge_refuses_every_start_path_while_on()
    {
        var plays = 0;
        var inv = Invoker(() => true, () => Interlocked.Increment(ref plays));

        var run = await inv.RunAsync(new RunMacroRequest("1.0", "RunMacro", "m1", new[] { "1" }, null, "agent"), CancellationToken.None);
        // Malformed on purpose: break-glass is checked before shape, so the answer is still breakglass.
        var clear = await inv.ClearAtAsync(new ClearAtRequest("1.0", "ClearAt", "agent", "1", null, null, null), CancellationToken.None);
        var sweep = await inv.SweepPathAsync(new SweepPathRequest("1.0", "SweepPath", "agent", "1", null, null, 0, 0, null), CancellationToken.None);

        foreach (var r in new[] { run, clear, sweep })
        {
            Assert.False(r.Ok);
            Assert.Equal("breakglass", r.Reason);
        }
        Assert.Equal(0, plays);
    }

    [Fact]
    public async Task Bridge_runs_normally_while_off()
    {
        var inv = Invoker(() => false);
        var run = await inv.RunAsync(new RunMacroRequest("1.0", "RunMacro", "m1", new[] { "1" }, null, "agent"), CancellationToken.None);
        Assert.True(run.Ok);
    }

    [Fact]
    public async Task Going_on_ends_a_repeat_at_the_next_pass()
    {
        var on = false;
        var plays = 0;
        var inv = Invoker(() => Volatile.Read(ref on), () => Interlocked.Increment(ref plays));

        var run = await inv.RunAsync(new RunMacroRequest("1.0", "RunMacro", "m1", new[] { "1" }, null, "agent", Repeat: true), CancellationToken.None);
        Assert.True(run.Ok);
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref plays) >= 2, 2000));

        Volatile.Write(ref on, true);
        Assert.True(SpinWait.SpinUntil(() => inv.ActivePlaybackCount == 0, 2000));
    }
}
