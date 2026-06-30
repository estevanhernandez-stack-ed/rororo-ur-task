// src/Ipc/MacroRunInvoker.cs
using System.Globalization;
using Labs626.UrTask.Macros;
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.Ipc;

/// <summary>
/// Resolves a <see cref="RunMacroRequest"/> against the macro library + running
/// alts and hands it to <see cref="SequencePlayer"/>. Resolution order matches
/// the contract refusal reasons: busy → unknown-macro → no-targets-resolved → play.
/// </summary>
internal sealed class MacroRunInvoker : IMacroRunInvoker
{
    internal const string ForegroundSentinel = "foreground";

    private readonly Func<IReadOnlyList<Macro>> _loadMacros;
    private readonly Func<IReadOnlyList<AccountRegistry.AccountInfo>> _snapshot;
    private readonly Func<long?> _resolveForegroundUserId;
    private readonly Func<bool> _isBusy;
    private readonly Func<Macro, IReadOnlyList<AccountRegistry.AccountInfo>, int?, CancellationToken, Task> _play;

    // Production ctor wires the real collaborators.
    public MacroRunInvoker(MacroStore store, AccountRegistry accounts, IForegroundWatcher foreground, SequencePlayer player)
        : this(
            loadMacros: () => store.LoadAll().Macros,
            snapshot: () => accounts.Snapshot().ToList(),
            resolveForegroundUserId: () => foreground.ResolveForegroundAccount()?.RobloxUserId,
            isBusy: () => player.IsRunning,
            play: (macro, targets, delay, ct) => player.PlayAsync(macro, targets, delay, ct))
    { }

    // Test ctor.
    internal MacroRunInvoker(
        Func<IReadOnlyList<Macro>> loadMacros,
        Func<IReadOnlyList<AccountRegistry.AccountInfo>> snapshot,
        Func<long?> resolveForegroundUserId,
        Func<bool> isBusy,
        Func<Macro, IReadOnlyList<AccountRegistry.AccountInfo>, int?, CancellationToken, Task> play)
    {
        _loadMacros = loadMacros;
        _snapshot = snapshot;
        _resolveForegroundUserId = resolveForegroundUserId;
        _isBusy = isBusy;
        _play = play;
    }

    public Task<RunMacroResponse> RunAsync(RunMacroRequest request, CancellationToken ct)
    {
        if (_isBusy())
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
        var playbackId = Guid.NewGuid().ToString("N");
        _ = ObservePlaybackAsync(macro, targets, request.InterAltDelayMs, ct);
        return Task.FromResult(RunMacroResponse.Accepted(playbackId));
    }

    private async Task ObservePlaybackAsync(Macro macro, IReadOnlyList<AccountRegistry.AccountInfo> targets, int? delay, CancellationToken ct)
    {
        try { await _play(macro, targets, delay, ct).ConfigureAwait(false); }
        catch { /* fire-and-forget; playback errors surface on the Ur Task side */ }
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
