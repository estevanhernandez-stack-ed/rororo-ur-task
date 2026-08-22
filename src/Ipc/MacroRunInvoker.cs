// src/Ipc/MacroRunInvoker.cs
using System.Collections.Concurrent;
using System.Globalization;
using Labs626.UrTask.Macros;
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.Ipc;

/// <summary>
/// Resolves a <see cref="RunMacroRequest"/> against the macro library + running
/// alts and hands it to <see cref="SequencePlayer"/>. Resolution order matches
/// the contract refusal reasons: busy → unknown-macro → no-targets-resolved → play.
/// <para>
/// Bridge 1.x extensions (MCP connector): playbacks register under their id in
/// <see cref="_playbacks"/> so <see cref="StopMacro"/> can cancel them, and
/// <c>Repeat</c> loops the play delegate until that cancellation lands. The busy
/// guard also refuses while any registered playback is active — a repeat BETWEEN
/// passes holds no <see cref="SequencePlayer"/> claim, and without this a second
/// run could slip into that gap.
/// </para>
/// </summary>
internal sealed class MacroRunInvoker : IMacroRunInvoker
{
    internal const string ForegroundSentinel = "foreground";

    private readonly Func<IReadOnlyList<Macro>> _loadMacros;
    private readonly Func<IReadOnlyList<AccountRegistry.AccountInfo>> _snapshot;
    private readonly Func<long?> _resolveForegroundUserId;
    private readonly Func<bool> _isBusy;
    private readonly Func<Macro, IReadOnlyList<AccountRegistry.AccountInfo>, int?, CancellationToken, Task> _play;
    private readonly Func<bool> _abort;

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _playbacks = new();

    internal int ActivePlaybackCount => _playbacks.Count;

    // Production ctor wires the real collaborators.
    public MacroRunInvoker(MacroStore store, AccountRegistry accounts, IForegroundWatcher foreground, SequencePlayer player)
        : this(
            loadMacros: () => store.LoadAll().Macros,
            snapshot: () => accounts.Snapshot().ToList(),
            resolveForegroundUserId: () => foreground.ResolveForegroundAccount()?.RobloxUserId,
            isBusy: () => player.IsRunning,
            play: (macro, targets, delay, ct) => player.PlayAsync(macro, targets, delay, ct),
            abort: () => player.Abort())
    { }

    // Test ctor.
    internal MacroRunInvoker(
        Func<IReadOnlyList<Macro>> loadMacros,
        Func<IReadOnlyList<AccountRegistry.AccountInfo>> snapshot,
        Func<long?> resolveForegroundUserId,
        Func<bool> isBusy,
        Func<Macro, IReadOnlyList<AccountRegistry.AccountInfo>, int?, CancellationToken, Task> play,
        Func<bool>? abort = null)
    {
        _loadMacros = loadMacros;
        _snapshot = snapshot;
        _resolveForegroundUserId = resolveForegroundUserId;
        _isBusy = isBusy;
        _play = play;
        _abort = abort ?? (() => false);
    }

    public Task<RunMacroResponse> RunAsync(RunMacroRequest request, CancellationToken ct)
    {
        if (_isBusy() || !_playbacks.IsEmpty)
            return Task.FromResult(RunMacroResponse.Refused("busy", "A sequence is already running."));

        var macro = _loadMacros().FirstOrDefault(m => string.Equals(m.Id, request.MacroId, StringComparison.OrdinalIgnoreCase));
        if (macro is null)
            return Task.FromResult(RunMacroResponse.Refused("unknown-macro", $"No macro with id '{request.MacroId}'."));

        var targets = ResolveTargets(request.Targets);
        if (targets.Count == 0)
            return Task.FromResult(RunMacroResponse.Refused("no-targets-resolved", "None of the requested targets are running."));

        // Ack-on-accept: start playback fire-and-forget and ack now. The bridge must not
        // block the caller (Ur-OCR's 5Hz tick) for the macro's full runtime. Exceptions in
        // the detached playback are swallowed here — they surface on the Ur Task playback side.
        // The playback registers under its id with a CTS linked to the bridge token, so
        // StopMacro can end it and bridge shutdown still tears it down.
        var playbackId = Guid.NewGuid().ToString("N");
        var playbackCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _playbacks[playbackId] = playbackCts;
        _ = ObservePlaybackAsync(playbackId, macro, targets, request.InterAltDelayMs, request.Repeat, playbackCts);
        return Task.FromResult(RunMacroResponse.Accepted(playbackId));
    }

    public IReadOnlyList<MacroSummary> ListMacros()
        => _loadMacros()
            .Select(m => new MacroSummary(m.Id, string.IsNullOrWhiteSpace(m.Name) ? "(unnamed)" : m.Name!))
            .ToList();

    public StopMacroResponse StopMacro(StopMacroRequest request)
    {
        int stopped = 0;

        if (!string.IsNullOrEmpty(request.PlaybackId))
        {
            if (_playbacks.TryGetValue(request.PlaybackId, out var cts))
            {
                try { cts.Cancel(); } catch (ObjectDisposedException) { }
                stopped = 1;
            }
        }
        else
        {
            foreach (var kvp in _playbacks)
            {
                try { kvp.Value.Cancel(); } catch (ObjectDisposedException) { }
                stopped++;
            }
        }

        // Abort the in-flight SequencePlayer pass so cancellation takes effect immediately,
        // not just at the next loop boundary. Single-flight today, so this stops the active
        // pass; Abort() is idempotent-safe and returns false when nothing is running.
        _abort();

        return StopMacroResponse.Done(stopped);
    }

    private async Task ObservePlaybackAsync(
        string playbackId, Macro macro, IReadOnlyList<AccountRegistry.AccountInfo> targets,
        int? interAltDelayMs, bool repeat, CancellationTokenSource playbackCts)
    {
        // Detach from the caller before the first pass — same reasoning as the accept loop's
        // Task.Yield. A play delegate that completes synchronously (an empty macro, a test fake)
        // would otherwise run the ENTIRE repeat loop inside RunAsync's fire-and-forget statement,
        // which is a hang wearing an ack's clothes.
        await Task.Yield();

        try
        {
            do
            {
                await _play(macro, targets, interAltDelayMs, playbackCts.Token).ConfigureAwait(false);
            }
            while (repeat && !playbackCts.IsCancellationRequested);
        }
        catch (OperationCanceledException) { /* stopped */ }
        catch { /* fire-and-forget; playback errors surface on the Ur Task side */ }
        finally
        {
            _playbacks.TryRemove(playbackId, out _);
            playbackCts.Dispose();
        }
    }

    private IReadOnlyList<AccountRegistry.AccountInfo> ResolveTargets(IReadOnlyList<string>? requested)
    {
        requested ??= Array.Empty<string>();
        var running = _snapshot();

        // Null/omitted or explicit ["foreground"] ⇒ the current foreground alt.
        bool isForeground = requested.Count == 0
            || (requested.Count == 1 && string.Equals(requested[0], ForegroundSentinel, StringComparison.OrdinalIgnoreCase));
        if (isForeground)
        {
            var fgUserId = _resolveForegroundUserId();
            if (fgUserId is not { } uid)
                return Array.Empty<AccountRegistry.AccountInfo>();
            var fg = running.FirstOrDefault(a => a.RobloxUserId == uid);
            return fg is null ? Array.Empty<AccountRegistry.AccountInfo>() : new[] { fg };
        }

        // Explicit user-ids — preserve requested order, drop unresolved.
        var resolved = new List<AccountRegistry.AccountInfo>(requested.Count);
        foreach (var t in requested)
        {
            if (long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId))
            {
                var hit = running.FirstOrDefault(a => a.RobloxUserId == userId);
                if (hit is not null) resolved.Add(hit);
            }
        }
        return resolved;
    }
}
