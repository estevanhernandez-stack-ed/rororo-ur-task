using System.IO;
using Labs626.UrTask.Macros;
using Labs626.UrTask.Macros.Steps;

namespace Labs626.UrTask.Tests.Steps;

public class PasteStepTests : IDisposable
{
    private const int Ctrl = 0x11, V = 0x56;

    private sealed class FakeIo : IStepIo
    {
        public long NowMs { get; private set; }
        public List<MacroEvent> Sent = new();
        public bool Foreground = true;
        public Action? OnDelay;
        public bool Send(MacroEvent e) { Sent.Add(e with { TimestampMs = NowMs }); return true; }
        public bool ReleaseButton(int button) => true;
        public bool MoveRelative(int dx, int dy) => true;
        public bool ParkPointer(int clientX, int clientY) => true;
        public (int X, int Y)? CursorClient() => (0, 0);
        public (int W, int H)? ClientSize() => (800, 599);
        public PixelBlock? Capture(int clientX, int clientY, int w, int h) => null;
        public bool TargetInForeground() => Foreground;
        public Task Delay(int ms, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            NowMs += ms;
            OnDelay?.Invoke();
            return Task.CompletedTask;
        }
        public IEnumerable<(int Vk, bool Down)> Keys
            => Sent.Select(e => (e.VirtualKeyCode, e.Kind == MacroEventKind.KeyDown));
    }

    /// <summary>Records every call with the io clock, so a test can say what the clipboard held
    /// when each key went out.</summary>
    private sealed class FakeClipboard(ClipboardContents start, Func<long> now) : IStepClipboard
    {
        public ClipboardContents Now = start;
        public bool SetWorks = true;
        public List<string> Calls = new();
        public List<(long T, string? Text)> History = new();
        public ClipboardContents Read() { Calls.Add("read"); return Now; }
        public bool SetText(string text)
        {
            Calls.Add("set " + text);
            if (!SetWorks) return false;
            Now = new ClipboardContents(text, false);
            History.Add((now(), text));
            return true;
        }
        public bool Clear() { Calls.Add("clear"); Now = new ClipboardContents(null, false); History.Add((now(), null)); return true; }
    }

    private static StepContext Ctx(IStepClipboard clipboard, List<string>? log = null)
        => new("CElCPapa", 5400534998, "m1", (800, 599), (800, 599), 100, _ => null, s => log?.Add(s), Clipboard: clipboard);

    [Fact]
    public async Task Pastes_its_text_with_ctrl_v_and_puts_the_old_text_back_after()
    {
        var io = new FakeIo();
        var clip = new FakeClipboard(new ClipboardContents("what you copied", false), () => io.NowMs);

        var r = await StepRunner.RunAsync(new MacroStep[] { new PasteStep(100, "Huge Doodle Cat") }, Ctx(clip), io, CancellationToken.None);

        Assert.Equal(PlaybackOutcome.Completed, r.Outcome);
        Assert.Equal(new[] { (Ctrl, true), (V, true), (V, false), (Ctrl, false) }, io.Keys);
        // The text is on the clipboard before Ctrl goes down, and the old text comes back only after
        // the settle that lets the game read it.
        Assert.Equal((100L, "Huge Doodle Cat"), clip.History[0]);
        Assert.Equal(100 + StepTiming.PasteKeyGapMs, io.Sent[1].TimestampMs);
        var lastKey = io.Sent[^1].TimestampMs;
        Assert.Equal((lastKey + StepTiming.PasteRestoreAfterMs, "what you copied"), clip.History[1]);
        Assert.Equal("what you copied", clip.Now.Text);
    }

    [Fact]
    public async Task With_no_text_it_pastes_the_clipboard_as_is_and_never_touches_it()
    {
        var io = new FakeIo();
        var clip = new FakeClipboard(new ClipboardContents("set by the agent", false), () => io.NowMs);

        var r = await StepRunner.RunAsync(new MacroStep[] { new PasteStep(0) }, Ctx(clip), io, CancellationToken.None);

        Assert.Equal(PlaybackOutcome.Completed, r.Outcome);
        Assert.Equal(4, io.Sent.Count);
        Assert.Empty(clip.Calls);
    }

    [Fact]
    public async Task An_empty_clipboard_is_cleared_again_and_a_non_text_one_is_left_and_logged()
    {
        var io = new FakeIo();
        var empty = new FakeClipboard(new ClipboardContents(null, false), () => io.NowMs);
        await StepRunner.RunAsync(new MacroStep[] { new PasteStep(0, "x") }, Ctx(empty), io, CancellationToken.None);
        Assert.Equal(new[] { "read", "set x", "clear" }, empty.Calls);

        var log = new List<string>();
        var image = new FakeClipboard(new ClipboardContents(null, true), () => io.NowMs);
        await StepRunner.RunAsync(new MacroStep[] { new PasteStep(0, "x") }, Ctx(image, log), io, CancellationToken.None);
        Assert.Equal(new[] { "read", "set x" }, image.Calls);
        Assert.Contains(log, l => l.Contains("something other than text"));
    }

    [Fact]
    public async Task A_clipboard_it_cannot_set_stops_the_run_before_any_key()
    {
        var io = new FakeIo();
        var clip = new FakeClipboard(new ClipboardContents("old", false), () => io.NowMs) { SetWorks = false };

        var r = await StepRunner.RunAsync(new MacroStep[] { new KeyStep(0, 0x45, true), new KeyStep(0, 0x45, false), new PasteStep(0, "x") },
            Ctx(clip), io, CancellationToken.None);

        Assert.Equal(PlaybackOutcome.Aborted, r.Outcome);
        Assert.Equal(2, r.StepIndex);
        Assert.Contains("clipboard at step 3", r.Reason);
        Assert.DoesNotContain(io.Keys, k => k.Vk == V || k.Vk == Ctrl);
    }

    [Fact]
    public async Task Losing_the_window_mid_paste_releases_ctrl_and_still_restores()
    {
        var io = new FakeIo();
        var clip = new FakeClipboard(new ClipboardContents("old", false), () => io.NowMs);
        // Ctrl is down and the gap before V runs: the user clicks away.
        io.OnDelay = () => { if (io.Sent.Count == 1) io.Foreground = false; };

        var r = await StepRunner.RunAsync(new MacroStep[] { new PasteStep(0, "x") }, Ctx(clip), io, CancellationToken.None);

        Assert.Equal(PlaybackOutcome.Aborted, r.Outcome);
        Assert.Equal(new[] { (Ctrl, true), (Ctrl, false) }, io.Keys);
        Assert.Equal("old", clip.Now.Text);
    }

    [Fact]
    public void Empty_text_is_refused_with_a_sentence_and_timing_counts_the_restore()
    {
        Assert.Equal("Step 1 is a paste with empty text; leave text out to paste what is on the clipboard.",
            StepValidator.Validate(new MacroStep[] { new PasteStep(0, "") }));
        Assert.Null(StepValidator.Validate(new MacroStep[] { new PasteStep(0), new PasteStep(0, "Huge") }));
        Assert.True(StepTiming.EstimateMs(new PasteStep(0, "x")) > StepTiming.EstimateMs(new PasteStep(0)));
    }

    // ---------- file and export ----------

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "urtask-paste-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void An_agent_written_paste_loads()
    {
        Directory.CreateDirectory(_dir);
        var id = Guid.NewGuid().ToString();
        File.WriteAllText(Path.Combine(_dir, id + ".json"), $$"""
        { "schemaVersion": 4, "id": "{{id}}", "recordedAtUnixMs": 1, "events": [],
          "steps": [ { "kind": "paste", "delayMs": 200, "text": "Huge" }, { "kind": "paste", "delayMs": 0 } ] }
        """);

        var m = Assert.Single(new MacroStore(_dir).LoadAll().Macros);
        Assert.Equal(new PasteStep(200, "Huge"), m.Steps![0]);
        Assert.Equal(new PasteStep(0), m.Steps[1]);
        Assert.Null(StepValidator.Validate(m.Steps));
    }

    private static Macro WithSteps(params MacroStep[] steps) => new(
        SchemaVersion: Macro.CurrentSchemaVersion, Id: "m1", Name: "paste", RecordMode: "PerWindow",
        RecordedAgainstUserId: null, RecordedAgainstDisplayName: null, InterAltDelayMs: null,
        RecordedAtUnixMs: 0, Events: new List<MacroEvent>()) { Steps = steps };

    [Fact]
    public void Exports_to_autohotkey_with_the_text_escaped_for_each_version()
    {
        var m = WithSteps(new PasteStep(0, "say \"hi\"; 50% `ok`"), new PasteStep(0));

        var v1 = AutoHotkeyExporter.Export(m, AhkVersion.V1);
        Assert.Contains("Clipboard := \"say \"\"hi\"\"`; 50% ``ok``\"", v1);
        Assert.Equal(2, v1.Split("Send, ^v").Length - 1);

        var v2 = AutoHotkeyExporter.Export(m, AhkVersion.V2);
        Assert.Contains("A_Clipboard := \"say `\"hi`\"; 50% ``ok``\"", v2);
        Assert.Equal(2, v2.Split("Send \"^v\"").Length - 1);
    }
}
