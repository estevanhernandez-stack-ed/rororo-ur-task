using Labs626.UrTask.Macros;
using Labs626.UrTask.Macros.Steps;
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.Tests.Steps;

/// <summary>
/// Mouse input goes to the window under the pointer, not the one in front, and a screen capture
/// reads whatever is on top. RealStepIo asks which top-level window is at the point before a
/// press, a wheel notch or a capture. Both the Win32 lookup and SendInput sit behind seams here, so
/// nothing in this file touches a real window or sends real input.
/// </summary>
public class RealStepIoCoverTests
{
    private static readonly IntPtr Target = new(0x1234);
    private static readonly IntPtr Other = new(0x9999);

    /// <summary>Client (0,0) sits at screen (100, 50); the title bar is the 31 rows above it.</summary>
    private sealed class FakeMetrics : IWindowMetrics
    {
        public (int X, int Y)? Origin = (100, 50);
        public IntPtr HwndForPid(int pid) => Target;
        public (int X, int Y)? ClientOrigin(IntPtr hwnd) => Origin;
        public (int W, int H)? ClientSize(IntPtr hwnd) => (800, 599);
        public (int X, int Y, int W, int H)? OuterRect(IntPtr hwnd) => (92, 19, 816, 638);
        public bool SetOuterRect(IntPtr hwnd, int x, int y, int w, int h) => true;
        public bool Minimize(IntPtr hwnd) => true;
        public bool Restore(IntPtr hwnd) => true;
        public (int X, int Y, int W, int H) WorkAreaFor(IntPtr hwnd) => (0, 0, 2560, 1440);
        public void Maximize(IntPtr hwnd) { }
        public void RestoreDown(IntPtr hwnd) { }
        public bool IsMaximized(IntPtr hwnd) => false;
    }

    private sealed class CountingSampler : IScreenSampler
    {
        public int Calls;
        public PixelBlock? Capture(IntPtr hwnd, int clientX, int clientY, int w, int h)
        {
            Calls++;
            return new PixelBlock(clientX, clientY, w, h, new uint[w * h]);
        }
    }

    private sealed class NoForeground : IForegroundWatcher
    {
        public AccountRegistry.AccountInfo? ResolveForegroundAccount() => null;
    }

    private sealed class Rig
    {
        public IntPtr AtPoint = Target;
        public readonly List<(int X, int Y)> Asked = new();
        public readonly List<MacroEvent> Sent = new();
        public readonly CountingSampler Sampler = new();
        public readonly FakeMetrics Metrics = new();
        public readonly RealStepIo Io;

        public Rig()
        {
            Io = new RealStepIo(Target, Metrics, Sampler, new NoForeground(), targetUserId: 42,
                rootAt: (x, y) => { Asked.Add((x, y)); return AtPoint; },
                send: e => { Sent.Add(e); return true; });
        }
    }

    private static MacroEvent Mouse(MacroEventKind kind, int x = 10, int y = 20, int delta = 0)
        => new(0, kind, 0, x, y, kind == MacroEventKind.MouseWheel ? 0 : 1, delta);

    [Theory]
    [InlineData(MacroEventKind.MouseDown)]
    [InlineData(MacroEventKind.MouseWheel)]
    public void A_press_or_a_wheel_notch_is_refused_when_another_window_is_at_the_point(MacroEventKind kind)
    {
        var rig = new Rig { AtPoint = Other };

        Assert.False(rig.Io.Send(Mouse(kind, delta: 120)));

        Assert.Empty(rig.Sent);                          // nothing reached SendInput
        Assert.True(rig.Io.LastSendCovered);
        Assert.Equal((110, 70), Assert.Single(rig.Asked)); // asked at the screen point, not the client one
    }

    [Fact]
    public void Nothing_under_the_point_at_all_is_refused_too()
    {
        var rig = new Rig { AtPoint = IntPtr.Zero };

        Assert.False(rig.Io.Send(Mouse(MacroEventKind.MouseDown)));
        Assert.Empty(rig.Sent);
    }

    [Theory]
    [InlineData(MacroEventKind.MouseMove)]
    [InlineData(MacroEventKind.MouseUp)]
    public void A_move_or_a_release_is_never_refused(MacroEventKind kind)
    {
        // A button that is down must always be able to come up, whatever is over the point.
        var rig = new Rig { AtPoint = Other };

        Assert.True(rig.Io.Send(Mouse(kind)));

        var sent = Assert.Single(rig.Sent);
        Assert.Equal((kind, 110, 70), (sent.Kind, sent.X, sent.Y));
        Assert.Empty(rig.Asked);                         // not even looked up
        Assert.False(rig.Io.LastSendCovered);
    }

    [Fact]
    public void Keys_never_ask_what_is_under_the_pointer()
    {
        var rig = new Rig { AtPoint = Other };

        Assert.True(rig.Io.Send(new MacroEvent(0, MacroEventKind.KeyDown, 0x45, 0, 0, 0, 0)));

        Assert.Single(rig.Sent);
        Assert.Empty(rig.Asked);
    }

    [Theory]
    [InlineData(MacroEventKind.MouseDown)]
    [InlineData(MacroEventKind.MouseWheel)]
    [InlineData(MacroEventKind.MouseMove)]
    [InlineData(MacroEventKind.MouseUp)]
    public void Nothing_changes_when_the_target_itself_is_at_the_point(MacroEventKind kind)
    {
        var rig = new Rig();

        Assert.True(rig.Io.Send(Mouse(kind, delta: 120)));

        var sent = Assert.Single(rig.Sent);
        Assert.Equal((kind, 110, 70), (sent.Kind, sent.X, sent.Y));
        Assert.False(rig.Io.LastSendCovered);
    }

    [Fact]
    public void A_covered_send_is_forgotten_by_the_next_send()
    {
        var rig = new Rig { AtPoint = Other };
        Assert.False(rig.Io.Send(Mouse(MacroEventKind.MouseDown)));
        Assert.True(rig.Io.LastSendCovered);

        rig.AtPoint = Target;
        Assert.True(rig.Io.Send(Mouse(MacroEventKind.MouseDown)));
        Assert.False(rig.Io.LastSendCovered);
    }

    [Fact]
    public void A_window_that_has_gone_is_a_failed_send_not_a_covered_one()
    {
        var rig = new Rig();
        rig.Metrics.Origin = null;

        Assert.False(rig.Io.Send(Mouse(MacroEventKind.MouseDown)));
        Assert.False(rig.Io.LastSendCovered);
        Assert.Empty(rig.Asked);
    }

    [Fact]
    public void A_capture_returns_nothing_when_another_window_covers_the_middle_of_the_box()
    {
        var rig = new Rig { AtPoint = Other };

        Assert.Null(rig.Io.Capture(40, 60, 5, 5));

        Assert.Equal((142, 112), Assert.Single(rig.Asked)); // centre of the box, in screen pixels
        Assert.Equal(0, rig.Sampler.Calls);                // the covering window's pixels are never read
    }

    [Fact]
    public void A_capture_comes_back_when_the_target_is_what_is_on_screen_there()
    {
        var rig = new Rig();

        var block = rig.Io.Capture(40, 60, 5, 5);

        Assert.NotNull(block);
        Assert.Equal((40, 60, 5, 5), (block!.X, block.Y, block.W, block.H));
        Assert.Equal(1, rig.Sampler.Calls);
    }

    [Fact]
    public void Parking_on_the_title_bar_uses_the_same_test()
    {
        var covered = new Rig { AtPoint = Other };
        Assert.False(covered.Io.ParkPointer(50, -10));
        Assert.Empty(covered.Sent);
        Assert.Equal((150, 40), Assert.Single(covered.Asked));

        var clear = new Rig();
        Assert.True(clear.Io.ParkPointer(50, -10));
        var move = Assert.Single(clear.Sent);
        Assert.Equal((MacroEventKind.MouseMove, 150, 40), (move.Kind, move.X, move.Y));
    }

    [Theory]
    [InlineData(MacroEventKind.MouseDown, true)]
    [InlineData(MacroEventKind.MouseWheel, true)]
    [InlineData(MacroEventKind.MouseMove, false)]
    [InlineData(MacroEventKind.MouseUp, false)]
    [InlineData(MacroEventKind.KeyDown, false)]
    [InlineData(MacroEventKind.KeyUp, false)]
    public void Only_a_press_and_a_wheel_notch_are_held_to_the_window_under_the_point(MacroEventKind kind, bool held)
        => Assert.Equal(held, WindowUnderPoint.Guards(kind));
}
