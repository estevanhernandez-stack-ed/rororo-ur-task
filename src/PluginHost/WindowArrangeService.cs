namespace Labs626.UrTask.PluginHost;

/// <summary>
/// Applies WindowArranger layouts to the running alt windows. Stack = every
/// alt at the anchor rect (foreground alt if any, else first in snapshot).
/// Grid = tiled over the anchor's monitor work area. The minimum window size
/// for grid clamping is discovered at apply time: apply, and windows enforce
/// their own floor via WM_GETMINMAXINFO — the pure layout uses a nominal
/// floor and the note reports overlap.
/// </summary>
internal sealed class WindowArrangeService
{
    // Nominal floor for grid cells. The real floor is whatever the window
    // enforces when SetWindowPos lands; this just keeps cells from computing
    // absurdly small before that.
    private const int NominalMinW = 640;
    private const int NominalMinH = 480;

    private readonly AccountRegistry _accounts;
    private readonly IWindowMetrics _metrics;
    private readonly IForegroundWatcher _foreground;

    public WindowArrangeService(AccountRegistry accounts, IWindowMetrics metrics, IForegroundWatcher foreground)
    {
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _foreground = foreground ?? throw new ArgumentNullException(nameof(foreground));
    }

    public (int moved, string? note) StackAll()
    {
        var (windows, anchorHwnd, note) = ResolveWindows();
        if (windows.Count == 0) return (0, note);
        var anchor = _metrics.OuterRect(anchorHwnd);
        if (anchor is null) return (0, "Couldn't read the anchor window's rect.");
        var rects = WindowArranger.ComputeStack(
            new RectPx(anchor.Value.X, anchor.Value.Y, anchor.Value.W, anchor.Value.H), windows.Count);
        return (Apply(windows, rects), null);
    }

    public (int moved, string? note) GridAll()
    {
        var (windows, anchorHwnd, note) = ResolveWindows();
        if (windows.Count == 0) return (0, note);
        var wa = _metrics.WorkAreaFor(anchorHwnd);
        var layout = WindowArranger.ComputeGrid(
            new RectPx(wa.X, wa.Y, wa.W, wa.H), windows.Count, NominalMinW, NominalMinH);
        var moved = Apply(windows, layout.Rects);
        return (moved, layout.Overlapping ? "Work area too small for a clean grid — windows overlap in cascade order." : null);
    }

    private (List<IntPtr> windows, IntPtr anchor, string? note) ResolveWindows()
    {
        var alts = _accounts.Snapshot().OrderBy(a => a.DisplayName).ToList();
        if (alts.Count == 0) return (new List<IntPtr>(), IntPtr.Zero, "No RoRoRo-managed alts running.");

        var windows = new List<IntPtr>(alts.Count);
        foreach (var alt in alts)
        {
            var hwnd = _metrics.HwndForPid(alt.Pid);
            if (hwnd != IntPtr.Zero) windows.Add(hwnd);
        }
        if (windows.Count == 0) return (windows, IntPtr.Zero, "No alt windows resolvable.");

        // Anchor: the foreground alt's window when it's one of ours, else the first.
        var fg = _foreground.ResolveForegroundAccount();
        var anchor = fg is not null ? _metrics.HwndForPid(fg.Pid) : IntPtr.Zero;
        if (anchor == IntPtr.Zero || !windows.Contains(anchor)) anchor = windows[0];
        return (windows, anchor, null);
    }

    private int Apply(List<IntPtr> windows, IReadOnlyList<RectPx> rects)
    {
        int moved = 0;
        for (int i = 0; i < windows.Count && i < rects.Count; i++)
        {
            var r = rects[i];
            if (_metrics.SetOuterRect(windows[i], r.X, r.Y, r.W, r.H)) moved++;
        }
        return moved;
    }
}
