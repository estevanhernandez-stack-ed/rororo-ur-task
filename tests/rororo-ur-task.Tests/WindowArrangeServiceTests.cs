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
        public List<IntPtr> MinimizeCalls = new();
        public List<IntPtr> RestoreCalls = new();

        public IntPtr HwndForPid(int pid) => PidToHwnd.TryGetValue(pid, out var h) ? h : IntPtr.Zero;
        public (int X, int Y)? ClientOrigin(IntPtr hwnd) => (0, 0);
        public (int W, int H)? ClientSize(IntPtr hwnd)
            => Outers.TryGetValue(hwnd, out var o) ? (o.W, o.H) : null;
        public (int X, int Y, int W, int H)? OuterRect(IntPtr hwnd)
            => Outers.TryGetValue(hwnd, out var o) ? o : null;
        public bool SetOuterRect(IntPtr hwnd, int x, int y, int w, int h)
        { SetCalls.Add((hwnd, x, y, w, h)); Outers[hwnd] = (x, y, w, h); return true; }
        public bool Minimize(IntPtr hwnd) { MinimizeCalls.Add(hwnd); return true; }
        public bool Restore(IntPtr hwnd) { RestoreCalls.Add(hwnd); return true; }
        public (int X, int Y, int W, int H) WorkAreaFor(IntPtr hwnd) => (0, 0, 2000, 1200);
        public void Maximize(IntPtr hwnd) { }
        public void RestoreDown(IntPtr hwnd) { }
        public bool IsMaximized(IntPtr hwnd) => false;
    }

    private static AccountRegistry RegistryWith(params int[] pids)
    {
        var reg = new AccountRegistry();
        foreach (var pid in pids) reg.OnLaunched(pid, userId: pid * 10, displayName: $"alt{pid}", accountId: $"a{pid}");
        return reg;
    }

    [Fact]
    public void StackAll_MinimizesEveryAlt()
    {
        var reg = RegistryWith(1, 2, 3);
        var metrics = new FakeMetrics
        {
            PidToHwnd = { [1] = new(0x1), [2] = new(0x2), [3] = new(0x3) },
            Outers = { [new(0x1)] = (100, 50, 800, 600), [new(0x2)] = (0, 0, 500, 400), [new(0x3)] = (900, 300, 640, 480) },
        };
        var svc = new WindowArrangeService(reg, metrics, new FakeForeground());

        var (moved, note) = svc.StackAll();

        Assert.Equal(3, moved);
        Assert.Null(note);
        Assert.Equal(3, metrics.MinimizeCalls.Count);
        Assert.Contains(new IntPtr(0x1), metrics.MinimizeCalls);
        Assert.Contains(new IntPtr(0x2), metrics.MinimizeCalls);
        Assert.Contains(new IntPtr(0x3), metrics.MinimizeCalls);
        // Minimize only — no move/resize.
        Assert.Empty(metrics.SetCalls);
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

    [Fact]
    public void RestoreAll_RestoresOriginalPositions_AndUnminimizes()
    {
        var reg = RegistryWith(1, 2);
        var metrics = new FakeMetrics
        {
            PidToHwnd = { [1] = new(0x1), [2] = new(0x2) },
            Outers = { [new(0x1)] = (100, 50, 800, 600), [new(0x2)] = (200, 100, 640, 480) },
        };
        var svc = new WindowArrangeService(reg, metrics, new FakeForeground());

        svc.StackAll(); // snapshots the two originals, minimizes
        metrics.SetCalls.Clear();

        var (restored, note) = svc.RestoreAll();

        Assert.Equal(2, restored);
        Assert.Null(note);
        Assert.Contains(new IntPtr(0x1), metrics.RestoreCalls);
        Assert.Contains(new IntPtr(0x2), metrics.RestoreCalls);
        Assert.Contains((new IntPtr(0x1), 100, 50, 800, 600), metrics.SetCalls);
        Assert.Contains((new IntPtr(0x2), 200, 100, 640, 480), metrics.SetCalls);
    }

    [Fact]
    public void Snapshot_CapturedOnce_GridThenStack_RestoreUsesPreGridOriginals()
    {
        var reg = RegistryWith(1);
        var metrics = new FakeMetrics
        {
            PidToHwnd = { [1] = new(0x1) },
            Outers = { [new(0x1)] = (100, 50, 800, 600) },
        };
        var svc = new WindowArrangeService(reg, metrics, new FakeForeground());

        svc.GridAll();  // snapshots (100,50,800,600), then moves 0x1 to the full-work-area cell
        svc.StackAll(); // must NOT re-snapshot the moved rect
        metrics.SetCalls.Clear();

        svc.RestoreAll();

        // Restores to the pre-GRID original, not the grid cell — proves snapshot-once.
        Assert.Contains((new IntPtr(0x1), 100, 50, 800, 600), metrics.SetCalls);
    }

    [Fact]
    public void RestoreAll_NoSnapshot_ReturnsZeroAndNote()
    {
        var reg = RegistryWith(1);
        var metrics = new FakeMetrics { PidToHwnd = { [1] = new(0x1) }, Outers = { [new(0x1)] = (0, 0, 640, 480) } };
        var svc = new WindowArrangeService(reg, metrics, new FakeForeground());

        var (restored, note) = svc.RestoreAll();

        Assert.Equal(0, restored);
        Assert.NotNull(note);
    }

    [Fact]
    public void Restore_ThenStackAgain_SnapshotsFresh()
    {
        var reg = RegistryWith(1);
        var metrics = new FakeMetrics
        {
            PidToHwnd = { [1] = new(0x1) },
            Outers = { [new(0x1)] = (100, 50, 800, 600) },
        };
        var svc = new WindowArrangeService(reg, metrics, new FakeForeground());

        svc.StackAll();
        svc.RestoreAll();               // clears the snapshot
        metrics.Outers[new(0x1)] = (300, 200, 640, 480); // window is somewhere new now
        svc.StackAll();                 // fresh cycle -> snapshot the new position
        metrics.SetCalls.Clear();
        svc.RestoreAll();

        Assert.Contains((new IntPtr(0x1), 300, 200, 640, 480), metrics.SetCalls);
    }
}
