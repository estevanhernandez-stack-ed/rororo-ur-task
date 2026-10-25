using Labs626.UrTask.Macros;
using Labs626.UrTask.Macros.Steps;

namespace Labs626.UrTask.Tests.Steps;

/// <summary>A macro's key-downs reset the account's idle clock (OnKeyLanded). Mouse input never does:
/// nobody has measured whether Roblox counts it.</summary>
public class KeyCreditTests
{
    private sealed class FakeIo : IStepIo
    {
        public long NowMs => 0;
        public bool SendWorks = true;
        public List<MacroEvent> Sent = new();
        public bool Send(MacroEvent e) { Sent.Add(e); return SendWorks; }
        public bool ReleaseButton(int button) => true;
        public bool MoveRelative(int dx, int dy) => true;
        public bool ParkPointer(int clientX, int clientY) => true;
        public (int X, int Y)? CursorClient() => (0, 0);
        public (int W, int H)? ClientSize() => (800, 599);
        public PixelBlock? Capture(int clientX, int clientY, int w, int h) => null;
        public bool TargetInForeground() => true;
        public Task Delay(int ms, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NoClipboard : IStepClipboard
    {
        public ClipboardContents Read() => new(null, false);
        public bool SetText(string text) => true;
        public bool Clear() => true;
    }

    private static async Task<(PlaybackResult R, List<InputSource> Credits)> Run(FakeIo io, params MacroStep[] steps)
    {
        var credits = new List<InputSource>();
        var ctx = new StepContext("CElCPapa", 5400534998, "m1", (800, 599), (800, 599), 100, _ => null, _ => { },
            Clipboard: new NoClipboard(), OnKeyLanded: credits.Add);
        var r = await StepRunner.RunAsync(steps, ctx, io, CancellationToken.None);
        return (r, credits);
    }

    [Fact]
    public async Task A_key_down_credits_once_as_macro_and_the_key_up_does_not()
    {
        var (_, credits) = await Run(new FakeIo(), new KeyStep(0, 0x41, true), new KeyStep(0, 0x41, false));
        Assert.Equal(new[] { InputSource.Macro }, credits);
    }

    [Fact]
    public async Task A_paste_credits_once_as_paste_not_per_key()
    {
        var (_, credits) = await Run(new FakeIo(), new PasteStep(0, "hi"));
        Assert.Equal(new[] { InputSource.Paste }, credits);
    }

    [Fact]
    public async Task Raw_key_downs_credit_and_mouse_events_do_not()
    {
        var events = new MacroEvent[]
        {
            new(0, MacroEventKind.MouseMove, 0, 10, 10, 0, 0),
            new(1, MacroEventKind.MouseDown, 0, 10, 10, 1, 0),
            new(2, MacroEventKind.MouseUp, 0, 10, 10, 1, 0),
            new(3, MacroEventKind.KeyDown, 0x20, 0, 0, 0, 0),
            new(4, MacroEventKind.KeyUp, 0x20, 0, 0, 0, 0),
        };
        var (_, credits) = await Run(new FakeIo(), new RawStep(0, events, ""));
        Assert.Equal(new[] { InputSource.Macro }, credits);
    }

    [Fact]
    public async Task Mouse_steps_never_credit()
    {
        var (_, credits) = await Run(new FakeIo(),
            new PointStep(0, "p", null, 100, 100),
            new DragStep(0, 1, 100, 100, 20, 20, 0),
            new WheelStep(0, 100, 100, 120),
            new SweepStep(0, new[] { new SweepPoint(100, 100), new SweepPoint(120, 120) }, 0));
        Assert.Empty(credits);
    }

    [Fact]
    public async Task A_key_send_that_fails_does_not_credit()
    {
        var (_, credits) = await Run(new FakeIo { SendWorks = false }, new KeyStep(0, 0x41, true));
        Assert.Empty(credits);
    }

    [Fact]
    public async Task A_paste_whose_ctrl_v_fails_does_not_credit()
    {
        var (_, credits) = await Run(new FakeIo { SendWorks = false }, new PasteStep(0, "hi"));
        Assert.Empty(credits);
    }
}
