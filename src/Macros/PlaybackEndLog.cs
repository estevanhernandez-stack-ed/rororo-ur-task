using System.Globalization;

namespace Labs626.UrTask.Macros;

/// <summary>
/// The ur-task.log lines for how a playback ended. Until 0.10.0 the stop sentence reached only
/// GetPlayback, and the log showed a playback's start and nothing after it (0.9.0 live pass,
/// docs/BACKLOG.md). Pure strings, so the wording is tested without a log file.
/// </summary>
internal static class PlaybackEndLog
{
    /// <summary>One account's playback. <paramref name="result"/> is null only when the play path
    /// threw; the exception itself surfaces wherever it is caught.</summary>
    public static string Line(string? macroName, string account, PlaybackResult? result, TimeSpan elapsed)
    {
        var name = string.IsNullOrWhiteSpace(macroName) ? "(unnamed)" : macroName;
        var secs = elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture);
        if (result is null) return $"playback ended with an error: '{name}' on {account} after {secs} s.";
        if (result.Outcome == PlaybackOutcome.Completed) return $"playback finished: '{name}' on {account} in {secs} s.";
        var verb = result.Outcome switch
        {
            PlaybackOutcome.Refused => "refused",
            PlaybackOutcome.Skipped => "skipped",
            _ => "stopped",
        };
        // StepIndex is 0-based and set only by a failed check; "step N" matches the sentence.
        var step = result.StepIndex is int i ? string.Create(CultureInfo.InvariantCulture, $" at step {i + 1}") : "";
        return $"playback {verb}{step}: '{name}' on {account} after {secs} s. {result.Reason ?? "No reason given."}";
    }

    /// <summary>One bridge playback: the id GetPlayback answers to, with the same state, reason and
    /// detail it returns, so an agent can match a GetPlayback reply to the log.</summary>
    public static string BridgeLine(string playbackId, string? macroName, string state, string? reason, string? detail)
    {
        var name = string.IsNullOrWhiteSpace(macroName) ? "(unnamed)" : macroName;
        var line = $"bridge playback {playbackId} '{name}': {state}";
        if (reason is not null) line += $" ({reason})";
        if (detail is not null) line += $". {detail}";
        return line;
    }
}
