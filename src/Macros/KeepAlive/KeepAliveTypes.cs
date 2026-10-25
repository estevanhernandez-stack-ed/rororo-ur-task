namespace Labs626.UrTask.Macros;

/// <summary>
/// What a game allows before it idles an account out, and when we act.
/// <see cref="Limit"/> is how long the game tolerates no input; <see cref="Deadline"/>
/// is when we must have sent input by (limit minus the safety margin, or the user's
/// override). <see cref="Clamped"/> is true when an override was raised to the minimum.
/// </summary>
internal sealed record KeepAliveLimit(TimeSpan Limit, TimeSpan Deadline, bool Clamped);
