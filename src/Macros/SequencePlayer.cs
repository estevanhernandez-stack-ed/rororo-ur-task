using System.Diagnostics;
using System.Runtime.InteropServices;
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.Macros;

/// <summary>
/// Orchestrates <see cref="IMacroPlayer"/> across N alts in click order.
/// Owns the batch state; <see cref="MacroPlayer"/> stays single-alt and
/// stateless. For each target: SetForegroundWindow → wait inter-alt delay
/// → verify foreground flipped → call MacroPlayer.PlayAsync → log outcome →
/// continue (skip-on-failure semantics in subsequent tasks; happy path here).
/// </summary>
internal sealed class SequencePlayer
{
    private const int DefaultInterAltDelayMs = 500;

    private readonly IMacroPlayer _player;
    private readonly IForegroundWatcher _foreground;
    private readonly Func<int, (bool ok, string? error)> _focus;
    private CancellationTokenSource? _activeCts;

    public SequencePlayer(IMacroPlayer player, IForegroundWatcher foreground)
        : this(player, foreground, DefaultFocus) { }

    internal SequencePlayer(IMacroPlayer player, IForegroundWatcher foreground, Func<int, (bool, string?)> focus)
    {
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _foreground = foreground ?? throw new ArgumentNullException(nameof(foreground));
        _focus = focus ?? throw new ArgumentNullException(nameof(focus));
    }

    public event EventHandler<SequenceProgress>? Progress;

    public bool IsRunning => _activeCts is not null;

    public async Task<SequenceResult> PlayAsync(
        Macro macro,
        IReadOnlyList<AccountRegistry.AccountInfo> orderedTargets,
        int? interAltDelayMs = null,
        CancellationToken external = default)
    {
        if (macro is null) throw new ArgumentNullException(nameof(macro));
        if (orderedTargets is null) throw new ArgumentNullException(nameof(orderedTargets));
        if (orderedTargets.Count == 0) throw new ArgumentException("Empty target list.", nameof(orderedTargets));

        var delay = interAltDelayMs ?? macro.InterAltDelayMs ?? DefaultInterAltDelayMs;

        _activeCts = CancellationTokenSource.CreateLinkedTokenSource(external);
        var clock = Stopwatch.StartNew();
        var perAlt = new List<AltOutcome>(orderedTargets.Count);
        int completed = 0, failed = 0, skipped = 0;

        try
        {
            for (int i = 0; i < orderedTargets.Count; i++)
            {
                if (_activeCts.IsCancellationRequested)
                {
                    for (int j = i; j < orderedTargets.Count; j++)
                    {
                        perAlt.Add(new AltOutcome(orderedTargets[j], PlaybackOutcome.Skipped, "Sequence aborted."));
                        skipped++;
                    }
                    EmitProgress(new SequenceProgress(i, orderedTargets.Count, null, SequencePhase.Aborted, completed, failed, skipped));
                    return new SequenceResult(perAlt, completed, failed, skipped, clock.Elapsed);
                }

                var target = orderedTargets[i];

                // Phase: Focusing
                EmitProgress(new SequenceProgress(i, orderedTargets.Count, target, SequencePhase.Focusing, completed, failed, skipped));
                var (ok, focusErr) = _focus(target.Pid);
                if (!ok)
                {
                    perAlt.Add(new AltOutcome(target, PlaybackOutcome.Refused, focusErr));
                    failed++;
                    continue;
                }
                try { await Task.Delay(delay, _activeCts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    perAlt.Add(new AltOutcome(target, PlaybackOutcome.Skipped, "Sequence aborted."));
                    skipped++;
                    continue;
                }

                // Verify foreground actually flipped.
                var fg = _foreground.ResolveForegroundAccount();
                if (fg is null || fg.RobloxUserId != target.RobloxUserId)
                {
                    perAlt.Add(new AltOutcome(target, PlaybackOutcome.Refused,
                        $"Couldn't focus {target.DisplayName} — window may be minimized or frozen."));
                    failed++;
                    continue;
                }

                // Phase: Playing
                EmitProgress(new SequenceProgress(i, orderedTargets.Count, target, SequencePhase.Playing, completed, failed, skipped));
                PlaybackResult playResult;
                try
                {
                    playResult = await _player.PlayAsync(macro, target.RobloxUserId, _activeCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    perAlt.Add(new AltOutcome(target, PlaybackOutcome.Aborted, "Cancelled."));
                    skipped++;
                    continue;
                }

                perAlt.Add(new AltOutcome(target, playResult.Outcome, playResult.Reason));
                switch (playResult.Outcome)
                {
                    case PlaybackOutcome.Completed: completed++; break;
                    case PlaybackOutcome.Refused:
                    case PlaybackOutcome.Aborted: failed++; break;
                }
            }

            EmitProgress(new SequenceProgress(orderedTargets.Count, orderedTargets.Count, null, SequencePhase.Done, completed, failed, skipped));
            return new SequenceResult(perAlt, completed, failed, skipped, clock.Elapsed);
        }
        finally
        {
            _activeCts?.Dispose();
            _activeCts = null;
        }
    }

    public bool Abort()
    {
        var cts = _activeCts;
        if (cts is null) return false;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        return true;
    }

    private void EmitProgress(SequenceProgress p)
    {
        try { Progress?.Invoke(this, p); } catch { /* swallow — subscriber bugs don't kill the sequence */ }
    }

    private static (bool, string?) DefaultFocus(int pid)
    {
        try
        {
            var hwnd = Process.GetProcessById(pid).MainWindowHandle;
            if (hwnd == IntPtr.Zero) return (false, "MainWindowHandle is null.");
            SetForegroundWindow(hwnd);
            return (true, null);
        }
        catch (ArgumentException) { return (false, "Process not found (pid stale)."); }
        catch (Exception ex) { return (false, ex.Message); }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
