using Labs626.UrTask.Macros;
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.UI;

/// <summary>What an assignment row needs to show and set keep-alive state: the shared ledger, the gate
/// (who a paused playback is paused for), a monotonic clock, and the limit an account is held to, and
/// whether the keep-alive loop is running (the ledger tracks accounts either way).</summary>
internal sealed record KeepAliveView(
    KeepAliveLedger Ledger,
    ForegroundGate Gate,
    Func<long> NowMs,
    Func<AccountRegistry.AccountInfo, KeepAliveLimit> LimitFor,
    Func<bool> IsLoopRunning);
