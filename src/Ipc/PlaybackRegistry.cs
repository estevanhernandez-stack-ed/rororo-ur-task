using System.Collections.Concurrent;

namespace Labs626.UrTask.Ipc;

internal enum PlaybackState { Running, Finished, Stopped, Failed }

/// <summary>How each bridge playback ended. Finished entries are kept for <see cref="Retention"/>
/// so a caller polling after the fact can still read them.</summary>
internal sealed class PlaybackRegistry
{
    public static readonly TimeSpan Retention = TimeSpan.FromMinutes(10);

    private sealed record Entry(PlaybackState State, string? Reason, string? Detail, int? StepIndex, DateTimeOffset? EndedAt, int[]? NoOutline = null, string? Actor = null, string? Advice = null, IReadOnlyList<long>? Targets = null);

    private readonly Func<DateTimeOffset> _now;
    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    public PlaybackRegistry(Func<DateTimeOffset>? now = null) => _now = now ?? (() => DateTimeOffset.UtcNow);

    /// <summary>Also prunes, so expired entries go even when nobody ever polls.</summary>
    /// <param name="targets">The accounts it plays on, so GetStatus can tell which one the gate is parked on.</param>
    public void Started(string id, IReadOnlyList<long>? targets = null)
    {
        Prune();
        _entries[id] = new Entry(PlaybackState.Running, null, null, null, null, Targets: targets);
    }

    /// <summary>Every playback that has not ended, with the accounts it plays on.</summary>
    public IReadOnlyList<(string Id, IReadOnlyList<long> Targets)> Running()
        => _entries.Where(kv => kv.Value.State == PlaybackState.Running)
                   .Select(kv => (kv.Key, kv.Value.Targets ?? (IReadOnlyList<long>)Array.Empty<long>()))
                   .ToArray();

    /// <param name="noOutline">A finished ClearAt's no-outline points (GetPlaybackResponse.NoOutline); null otherwise.</param>
    public void Finished(string id, PlaybackState state, string? reason, string? detail, int? stepIndex, int[]? noOutline = null,
        string? actor = null, string? advice = null)
        => _entries[id] = new Entry(state, reason, detail, stepIndex, _now(), noOutline, actor, advice);

    public GetPlaybackResponse Get(string? id)
    {
        Prune();
        if (string.IsNullOrWhiteSpace(id))
            return GetPlaybackResponse.Refused(Reasons.Refused, "Name the playback id that RunMacro returned.");
        if (!_entries.TryGetValue(id, out var e))
            return GetPlaybackResponse.Refused(Reasons.UnknownPlayback, $"No playback with id '{id}'. Finished playbacks are kept for 10 minutes.");
        return new GetPlaybackResponse(true, e.State.ToString().ToLowerInvariant(), e.Reason, e.Detail, e.StepIndex, e.NoOutline, e.Actor, e.Advice);
    }

    private void Prune()
    {
        var cutoff = _now() - Retention;
        foreach (var kv in _entries)
            if (kv.Value.EndedAt is { } t && t < cutoff) _entries.TryRemove(kv.Key, out _);
    }
}
