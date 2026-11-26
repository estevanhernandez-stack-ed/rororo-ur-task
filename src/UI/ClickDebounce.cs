namespace Labs626.UrTask.UI;

/// <summary>Swallows a repeat invocation that lands within a short window of the last accepted one.
/// Monotonic clock, so a wall-clock change cannot reopen or jam it.</summary>
internal sealed class ClickDebounce
{
    private readonly long _windowMs;
    private readonly Func<long> _nowMs;
    private long _lastAcceptedMs;
    private bool _any;

    public ClickDebounce(TimeSpan window, Func<long>? nowMs = null)
    {
        _windowMs = (long)window.TotalMilliseconds;
        _nowMs = nowMs ?? (() => Environment.TickCount64);
    }

    /// <summary>True when the call should proceed. A refused call does not extend the window.</summary>
    public bool TryEnter()
    {
        var now = _nowMs();
        if (_any && now - _lastAcceptedMs < _windowMs) return false;
        _any = true;
        _lastAcceptedMs = now;
        return true;
    }
}
