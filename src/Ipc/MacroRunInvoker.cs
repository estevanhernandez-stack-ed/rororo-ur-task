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
    private readonly Func<Macro, IReadOnlyList<AccountRegistry.AccountInfo>, int?, CancellationToken, Task<string>> _play;

    // Production ctor wires the real collaborators.
    public MacroRunInvoker(MacroStore store, AccountRegistry accounts, IForegroundWatcher foreground, SequencePlayer player)
        : this(
            loadMacros: () => store.LoadAll().Macros,
            snapshot: () => accounts.Snapshot().ToList(),
            resolveForegroundUserId: () => foreground.ResolveForegroundAccount()?.RobloxUserId,
            isBusy: () => player.IsRunning,
            play: async (macro, targets, delay, ct) =>
            {
                await player.PlayAsync(macro, targets, delay, ct).ConfigureAwait(false);
                return Guid.NewGuid().ToString("N"); // playback id for the ack
            })
    { }

    // Test ctor.
    internal MacroRunInvoker(
        Func<IReadOnlyList<Macro>> loadMacros,
        Func<IReadOnlyList<AccountRegistry.AccountInfo>> snapshot,
        Func<long?> resolveForegroundUserId,
        Func<bool> isBusy,
        Func<Macro, IReadOnlyList<AccountRegistry.AccountInfo>, int?, CancellationToken, Task<string>> play)
    {
        _loadMacros = loadMacros;
        _snapshot = snapshot;
        _resolveForegroundUserId = resolveForegroundUserId;
        _isBusy = isBusy;
        _play = play;
    }

    public async Task<RunMacroResponse> RunAsync(RunMacroRequest request, CancellationToken ct)
    {
        if (_isBusy())
            return RunMacroResponse.Refused("busy", "A sequence is already running.");

        var macro = _loadMacros().FirstOrDefault(m => string.Equals(m.Id, request.MacroId, StringComparison.OrdinalIgnoreCase));
        if (macro is null)
            return RunMacroResponse.Refused("unknown-macro", $"No macro with id '{request.MacroId}'.");

        var targets = ResolveTargets(request.Targets);
        if (targets.Count == 0)
            return RunMacroResponse.Refused("no-targets-resolved", "None of the requested targets are running.");

        var playbackId = await _play(macro, targets, request.InterAltDelayMs, ct).ConfigureAwait(false);
        return RunMacroResponse.Accepted(playbackId);
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
