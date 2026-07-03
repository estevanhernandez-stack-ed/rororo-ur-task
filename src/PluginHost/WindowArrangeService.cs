namespace Labs626.UrTask.PluginHost;

/// <summary>
/// Applies WindowArranger layouts to the running alt windows.
/// STACK = minimize every alt (get them out of the way). GRID = tiled over the
/// anchor's monitor work area. RESET = restore each alt to the position it held
/// before the first STACK/GRID of this cycle (and un-minimize).
///
/// Before the first STACK/GRID since the last RESET, every alt's outer rect is
/// snapshotted once; RESET replays those and clears the snapshot, so a
/// STACK-then-GRID sequence still resets to the true originals. Grid's minimum
/// window size is discovered at apply time — windows enforce their own floor via
/// WM_GETMINMAXINFO — the pure layout uses a nominal floor and reports overlap.
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

    // pid -> outer rect held before this arrange cycle began. Captured once per
    // cycle (first STACK/GRID after a RESET), replayed and cleared by RESET.
    private readonly Dictionary<int, RectPx> _originals = new();

    public WindowArrangeService(AccountRegistry accounts, IWindowMetrics metrics, IForegroundWatcher foreground)
    {
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _foreground = foreground ?? throw new ArgumentNullException(nameof(foreground));
    }

    public (int moved, string? note) StackAll()
    {
        var alts = _accounts.Snapshot();
        if (alts.Count == 0) return (0, "No RoRoRo-managed alts running.");
        SnapshotOriginals();
        int moved = 0;
        foreach (var alt in alts)
        {
            var hwnd = _metrics.HwndForPid(alt.Pid);
            if (hwnd != IntPtr.Zero && _metrics.Minimize(hwnd)) moved++;
        }
        return moved == 0 ? (0, "No alt windows resolvable.") : (moved, null);
    }

    public (int moved, string? note) GridAll()
    {
        var (windows, anchorHwnd, note) = ResolveWindows();
        if (windows.Count == 0) return (0, note);
        SnapshotOriginals();
        var wa = _metrics.WorkAreaFor(anchorHwnd);
        var layout = WindowArranger.ComputeGrid(
            new RectPx(wa.X, wa.Y, wa.W, wa.H), windows.Count, NominalMinW, NominalMinH);
        var moved = Apply(windows, layout.Rects);
        return (moved, layout.Overlapping ? "Work area too small for a clean grid — windows overlap in cascade order." : null);
    }

    /// <summary>RESET: un-minimize and move each alt back to its pre-arrange rect,
    /// then clear the snapshot so the next STACK/GRID starts a fresh cycle.</summary>
    public (int restored, string? note) RestoreAll()
    {
        if (_originals.Count == 0) return (0, "No saved window layout to reset — STACK or GRID first.");
        int restored = 0;
        foreach (var (pid, rect) in _originals)
        {
            var hwnd = _metrics.HwndForPid(pid);
            if (hwnd == IntPtr.Zero) continue;
            _metrics.Restore(hwnd); // un-minimize before repositioning — SetWindowPos on a minimized window is unreliable
            if (_metrics.SetOuterRect(hwnd, rect.X, rect.Y, rect.W, rect.H)) restored++;
        }
        _originals.Clear();
        return (restored, null);
    }

    // Capture every alt's current outer rect — but only if we haven't already this
    // cycle. A STACK-then-GRID must still reset to the pre-STACK positions.
    private void SnapshotOriginals()
    {
        if (_originals.Count > 0) return;
        foreach (var alt in _accounts.Snapshot())
        {
            var hwnd = _metrics.HwndForPid(alt.Pid);
            if (hwnd == IntPtr.Zero) continue;
            var r = _metrics.OuterRect(hwnd);
            if (r is null) continue;
            _originals[alt.Pid] = new RectPx(r.Value.X, r.Value.Y, r.Value.W, r.Value.H);
        }
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
