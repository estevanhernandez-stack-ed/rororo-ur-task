using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.Tests;

public class WindowArrangeServiceTests
{
    private sealed class FakeForeground : IForegroundWatcher
    {
        public AccountRegistry.AccountInfo? Current { get; set; }
        public AccountRegistry.AccountInfo? ResolveForegroundAccount() => Current;
    }

    private sealed class FakeMetrics : IWindowMetrics
    {
        public Dictionary<int, IntPtr> PidToHwnd = new();
        public Dictionary<IntPtr, (int X, int Y, int W, int H)> Outers = new();
        public List<(IntPtr hwnd, int x, int y, int w, int h)> SetCalls = new();

        public IntPtr HwndForPid(int pid) => PidToHwnd.TryGetValue(pid, out var h) ? h : IntPtr.Zero;
        public (int X, int Y)? ClientOrigin(IntPtr hwnd) => (0, 0);
        public (int W, int H)? ClientSize(IntPtr hwnd)
            => Outers.TryGetValue(hwnd, out var o) ? (o.W, o.H) : null;
        public (int X, int Y, int W, int H)? OuterRect(IntPtr hwnd)
            => Outers.TryGetValue(hwnd, out var o) ? o : null;
        public bool SetOuterRect(IntPtr hwnd, int x, int y, int w, int h)
        { SetCalls.Add((hwnd, x, y, w, h)); Outers[hwnd] = (x, y, w, h); return true; }
        public (int X, int Y, int W, int H) WorkAreaFor(IntPtr hwnd) => (0, 0, 2000, 1200);
    }

    private static AccountRegistry RegistryWith(params int[] pids)
    {
        var reg = new AccountRegistry();
        foreach (var pid in pids) reg.OnLaunched(pid, userId: pid * 10, displayName: $"alt{pid}", accountId: $"a{pid}");
        return reg;
    }

    [Fact]
    public void StackAll_MovesEveryAltToAnchorRect()
    {
        var reg = RegistryWith(1, 2, 3);
        var metrics = new FakeMetrics
        {
            PidToHwnd = { [1] = new(0x1), [2] = new(0x2), [3] = new(0x3) },
            Outers = { [new(0x1)] = (100, 50, 800, 600), [new(0x2)] = (0, 0, 500, 400), [new(0x3)] = (900, 300, 640, 480) },
        };
        // Foreground = pid 1 → its rect is the anchor.
        var fg = new FakeForeground { Current = new AccountRegistry.AccountInfo(1, 10, "alt1", "a1") };
        var svc = new WindowArrangeService(reg, metrics, fg);

        var (moved, note) = svc.StackAll();
        Assert.Equal(3, moved);
        Assert.Null(note);
        Assert.All(metrics.SetCalls, c => Assert.Equal((100, 50, 800, 600), (c.x, c.y, c.w, c.h)));
    }

    [Fact]
    public void StackAll_NoForeground_AnchorsOnFirstAlt()
    {
        var reg = RegistryWith(7);
        var metrics = new FakeMetrics
        {
            PidToHwnd = { [7] = new(0x7) },
            Outers = { [new(0x7)] = (10, 10, 640, 480) },
        };
        var svc = new WindowArrangeService(reg, metrics, new FakeForeground());
        var (moved, _) = svc.StackAll();
        Assert.Equal(1, moved);
    }

    [Fact]
    public void GridAll_TilesAcrossWorkArea()
    {
        var reg = RegistryWith(1, 2, 3, 4);
        var metrics = new FakeMetrics
        {
            PidToHwnd = { [1] = new(0x1), [2] = new(0x2), [3] = new(0x3), [4] = new(0x4) },
            Outers = { [new(0x1)] = (0, 0, 800, 600), [new(0x2)] = (0, 0, 800, 600), [new(0x3)] = (0, 0, 800, 600), [new(0x4)] = (0, 0, 800, 600) },
        };
        var svc = new WindowArrangeService(reg, metrics, new FakeForeground());
        var (moved, note) = svc.GridAll();
        Assert.Equal(4, moved);
        Assert.Null(note);
        Assert.Equal(4, metrics.SetCalls.Select(c => (c.x, c.y)).Distinct().Count()); // 4 distinct cells
    }

    [Fact]
    public void NoAltsRunning_ReturnsZeroAndNote()
    {
        var svc = new WindowArrangeService(new AccountRegistry(), new FakeMetrics(), new FakeForeground());
        var (moved, note) = svc.StackAll();
        Assert.Equal(0, moved);
        Assert.NotNull(note);
    }
}
