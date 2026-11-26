using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.Macros;

/// <summary>
/// Tells a per-window recording that its account's window has closed. Once it has, nothing typed can
/// belong to the recording any more, so the runtime stops and saves what it has rather than leaving
/// the hooks in until the user notices. The recording-side twin of <see cref="AutoStopCoordinator"/>.
/// </summary>
internal sealed class RecordingExitWatch
{
    private readonly Action<AccountRegistry.AccountInfo> _onBoundAccountExited;
    private int _pid;   // 0 = nothing watched

    public RecordingExitWatch(AccountRegistry accounts, Action<AccountRegistry.AccountInfo> onBoundAccountExited)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        _onBoundAccountExited = onBoundAccountExited ?? throw new ArgumentNullException(nameof(onBoundAccountExited));
        accounts.AccountRemoved += OnAccountRemoved;
    }

    /// <summary>A per-window recording bound to this process has started.</summary>
    public void Watch(int pid) => Volatile.Write(ref _pid, pid);

    /// <summary>The recording has ended some other way.</summary>
    public void Clear() => Volatile.Write(ref _pid, 0);

    private void OnAccountRemoved(object? sender, AccountRegistry.AccountInfo info)
    {
        // Claimed with one exchange, so the exit is reported once: Windows recycles pids, and a
        // stop that is already under way on another thread clears the watch first.
        if (info.Pid == 0 || Interlocked.CompareExchange(ref _pid, 0, info.Pid) != info.Pid) return;
        _onBoundAccountExited(info);
    }
}
