using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.Macros;

/// <summary>
/// Bridges <see cref="AccountRegistry.AccountRemoved"/> to
/// <see cref="MacroPlayer.Abort"/>: when an alt's Roblox window closes
/// while a macro bound to that user-id is mid-playback, kill the playback
/// immediately. This is the "auto-stop on account-exited" behavior that
/// keeps a farming macro from keying air after the target alt has died.
///
/// Stateless except for the active-playback user-id pin set on PlaybackStarted
/// and cleared on PlaybackEnded. Subscription lifetime is the plugin's
/// lifetime — the coordinator is created once at startup and lives until exit.
/// </summary>
internal sealed class AutoStopCoordinator
{
    private readonly MacroPlayer _player;
    private long? _activeTargetUserId;

    public AutoStopCoordinator(MacroPlayer player, AccountRegistry accounts)
    {
        _player = player ?? throw new ArgumentNullException(nameof(player));
        if (accounts is null) throw new ArgumentNullException(nameof(accounts));

        _player.Started += (_, args) => _activeTargetUserId = args.TargetUserId == 0 ? null : args.TargetUserId;
        _player.Ended += (_, _) => _activeTargetUserId = null;
        accounts.AccountRemoved += OnAccountRemoved;
    }

    private void OnAccountRemoved(object? sender, AccountRegistry.AccountInfo info)
    {
        if (_activeTargetUserId == info.RobloxUserId)
        {
            // Active playback was bound to the user-id whose window just closed.
            // Abort. The PlayAsync loop catches OperationCanceledException and
            // returns PlaybackResult.Aborted("Playback cancelled.") naturally.
            _player.Abort();
        }
    }
}
