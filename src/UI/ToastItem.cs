namespace Labs626.UrTask.UI;

/// <summary>Toast severity. Error is all playback refusals need today; kept as
/// an enum (not a bool) so a future Info/Warning toast doesn't need a shape
/// change.</summary>
internal enum ToastSeverity { Error }

/// <summary>Transient, themed activity toast — surfaces a playback refusal
/// that would otherwise be buried in the (easy-to-miss) activity log.
/// Immutable: created once by <see cref="RecorderViewModel.ShowError"/>,
/// displayed, then removed from <see cref="RecorderViewModel.Toasts"/> after
/// its lifetime elapses.</summary>
internal sealed record ToastItem(string Message, ToastSeverity Severity, DateTimeOffset CreatedAt);

/// <summary>
/// Pure dedup/rate-limit decision for <see cref="RecorderViewModel.ShowError"/>,
/// split out so it's testable without a WPF Dispatcher or a live PluginRuntime.
/// </summary>
internal static class ToastDedup
{
    /// <summary>
    /// True when <paramref name="incomingMessage"/> should be suppressed rather
    /// than shown as a new toast. Rule: suppress only if it's identical to the
    /// single most-recently-shown message AND arrived within
    /// <paramref name="window"/> of that message. This is simpler than tracking
    /// every currently-visible toast (no per-message bookkeeping, no collection
    /// scan) and still kills the dominant repeat-refusal case — a loop that
    /// refuses on the same reason every cycle (e.g. round-robin hitting the
    /// same off-screen-resize refusal each pass) — without silently swallowing
    /// a genuinely different error that happens to follow it.
    /// </summary>
    public static bool ShouldSuppress(
        string? lastMessage,
        DateTimeOffset lastShownAt,
        string incomingMessage,
        DateTimeOffset now,
        TimeSpan window)
        => lastMessage == incomingMessage && (now - lastShownAt) < window;
}
