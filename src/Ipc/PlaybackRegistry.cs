using System.Collections.Concurrent;

namespace Labs626.UrTask.Ipc;

internal enum PlaybackState { Running, Finished, Stopped, Failed }

/// <summary>How each bridge playback ended. Finished entries are kept for <see cref="Retention"/>
/// so a caller polling after the fact can still read them.</summary>
internal sealed class PlaybackRegistry
{
    public static readonly TimeSpan Retention = TimeSpan.FromMinutes(10);

    private sealed record Entry(PlaybackState State, string? Reason, string? Detail, int? StepIndex, DateTimeOffset? EndedAt);

    private readonly Func<DateTimeOffset> _now;
    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    public PlaybackRegistry(Func<DateTimeOffset>? now = null) => _now = now ?? (() => DateTimeOffset.UtcNow);

    /// <summary>Also prunes, so expired entries go even when nobody ever polls.</summary>
    public void Started(string id)
    {
        Prune();
        _entries[id] = new Entry(PlaybackState.Running, null, null, null, null);
    }

    public void Finished(string id, PlaybackState state, string? reason, string? detail, int? stepIndex)
        => _entries[id] = new Entry(state, reason, detail, stepIndex, _now());

    public GetPlaybackResponse Get(string? id)
    {
        Prune();
        if (string.IsNullOrWhiteSpace(id))
            return GetPlaybackResponse.Refused("refused", "Name the playback id that RunMacro returned.");
        if (!_entries.TryGetValue(id, out var e))
            return GetPlaybackResponse.Refused("unknown-playback", $"No playback with id '{id}'. Finished playbacks are kept for 10 minutes.");
        return new GetPlaybackResponse(true, e.State.ToString().ToLowerInvariant(), e.Reason, e.Detail, e.StepIndex);
    }

    private void Prune()
    {
        var cutoff = _now() - Retention;
        foreach (var kv in _entries)
            if (kv.Value.EndedAt is { } t && t < cutoff) _entries.TryRemove(kv.Key, out _);
    }
}
