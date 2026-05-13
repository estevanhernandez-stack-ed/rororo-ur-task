using System.Collections.Concurrent;

namespace Labs626.UrTask.PluginHost;

/// <summary>
/// In-memory (pid → user-id) map maintained from RoRoRo's account-launched +
/// account-exited event streams. The map is the structural reason this plugin
/// is a plugin — generic TinyTask can't bind a recording to a specific Roblox
/// user id because it has no way to map Roblox.exe pid → which Roblox user
/// the window belongs to. RoRoRo emits the binding because it launched the
/// alt and remembers.
/// </summary>
public sealed class AccountRegistry
{
    private readonly ConcurrentDictionary<int, AccountInfo> _byPid = new();

    public sealed record AccountInfo(int Pid, long RobloxUserId, string DisplayName, string AccountId);

    /// <summary>
    /// Snapshot of currently-known accounts. Safe to read while events flow in.
    /// </summary>
    public IReadOnlyCollection<AccountInfo> Snapshot() => _byPid.Values.ToArray();

    /// <summary>
    /// Resolve a foreground window's pid to the RoRoRo-managed account it belongs
    /// to. Returns null when the pid isn't a RoRoRo-launched Roblox process —
    /// either because RoRoRo didn't launch it, or because the launch event hasn't
    /// arrived yet (RoRoRo emits asynchronously after Process.Start completes).
    /// </summary>
    public AccountInfo? ResolveByPid(int pid)
        => _byPid.TryGetValue(pid, out var info) ? info : null;

    public void OnLaunched(int pid, long userId, string displayName, string accountId)
    {
        var info = new AccountInfo(pid, userId, displayName, accountId);
        _byPid[pid] = info;
        AccountAdded?.Invoke(this, info);
    }

    public void OnExited(int pid)
    {
        if (_byPid.TryRemove(pid, out var info))
        {
            AccountRemoved?.Invoke(this, info);
        }
    }

    /// <summary>Raised when an account-launched event populates a new pid.</summary>
    public event EventHandler<AccountInfo>? AccountAdded;

    /// <summary>Raised when an account-exited event removes a pid.</summary>
    public event EventHandler<AccountInfo>? AccountRemoved;
}
