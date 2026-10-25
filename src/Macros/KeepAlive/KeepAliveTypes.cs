namespace Labs626.UrTask.Macros;

/// <summary>
/// What a game allows before it idles an account out, and when we act.
/// <see cref="Limit"/> is how long the game tolerates no input; <see cref="Deadline"/>
/// is when we must have sent input by (limit minus the safety margin, or the user's
/// override). <see cref="Clamped"/> is true when an override was raised to the minimum.
/// </summary>
internal sealed record KeepAliveLimit(TimeSpan Limit, TimeSpan Deadline, bool Clamped);

/// <summary>Where a landed input came from.</summary>
internal enum InputSource { KeepAlive, Macro, Paste, Host }

/// <summary>Why a keep-alive attempt did not land.</summary>
internal enum MissCause { WindowGone, FocusRefused, OtherInFront, SendRejected, HeldOff }

/// <summary>The outcome of trying to bring an account's window to the front. Cause is set exactly when
/// Ok is false. The tuple conversion keeps the old (ok, error) rigs compiling: false means FocusRefused.</summary>
internal sealed record FocusAttempt(bool Ok, MissCause? Cause, string? Detail)
{
    public static implicit operator FocusAttempt((bool ok, string? error) t)
        => t.ok ? new FocusAttempt(true, null, t.error) : new FocusAttempt(false, MissCause.FocusRefused, t.error);
}

/// <summary>A failed attempt. Holder is "'&lt;title&gt;' (&lt;process&gt;)" or the hold reason.</summary>
internal sealed record Miss(MissCause Cause, string? Holder);

internal enum AccountKeepAliveState { Waiting, Serving, Off, Held, PausedForMacro }

/// <summary>Immutable per-account view of the ledger.</summary>
internal sealed record AccountLedgerEntry(
    long UserId,
    string DisplayName,
    string AccountId,
    KeepAliveLimit Limit,
    long? LastInputMs,
    InputSource? LastSource,
    AccountKeepAliveState State,
    bool Off,
    int ConsecutiveMisses,
    Miss? LastMiss);
