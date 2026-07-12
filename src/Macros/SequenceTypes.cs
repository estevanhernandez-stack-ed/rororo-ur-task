using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.Macros;

public enum SequencePhase
{
    Focusing,    // SetForegroundWindow called, waiting for inter-alt delay
    Playing,     // MacroPlayer.PlayAsync running on this alt
    Settling,    // delay between alts (post-play)
    Refused,     // this alt's macro was refused/aborted (see SequenceProgress.Reason) — sequence continues to the next alt
    Aborted,     // sequence was aborted by user
    Done,        // all alts processed
}

public sealed record SequenceProgress(
    int Index,                                   // 0-based index of the alt currently being processed
    int Total,                                   // total alts in the sequence
    AccountRegistry.AccountInfo? CurrentAlt,     // null on Done/Aborted-final
    SequencePhase Phase,
    int Completed,
    int Failed,
    int Skipped,
    string? Reason = null);                      // set on Phase == Refused; carries PlaybackResult.Reason / focus-failure text

public sealed record AltOutcome(
    AccountRegistry.AccountInfo Alt,
    PlaybackOutcome Outcome,                     // Completed | Refused | Aborted | Skipped (sequence aborted)
    string? Reason);

public sealed record SequenceResult(
    IReadOnlyList<AltOutcome> PerAlt,
    int Completed,
    int Failed,
    int Skipped,
    TimeSpan TotalDuration);
