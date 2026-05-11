using System.Runtime.InteropServices;

namespace Labs626.UrTask.PluginHost;

/// <summary>
/// Resolves the current foreground window to a RoRoRo-managed account by
/// asking Win32 for the foreground window's pid, then looking that pid up
/// in <see cref="AccountRegistry"/>. This is the runtime mechanism that
/// makes "playback refuses unless the foreground window matches the bound
/// user-id" possible.
///
/// Stateless on purpose — callers (record start, playback pre-flight,
/// playback continuous check) ask on demand. No polling loop lives here.
/// </summary>
internal sealed partial class ForegroundWatcher
{
    private readonly AccountRegistry _registry;

    public ForegroundWatcher(AccountRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <summary>
    /// Resolve the current foreground window to a RoRoRo-managed account, or
    /// null if the foreground window isn't a RoRoRo-launched Roblox process
    /// (either because RoRoRo didn't launch it, or because the launch event
    /// hasn't propagated through gRPC yet).
    /// </summary>
    public AccountRegistry.AccountInfo? ResolveForegroundAccount()
    {
        var pid = GetForegroundProcessId();
        return pid == 0 ? null : _registry.ResolveByPid((int)pid);
    }

    /// <summary>
    /// Win32 pid of the current foreground window, or 0 if no window has
    /// focus. Exposed for tests; callers normally want <see cref="ResolveForegroundAccount"/>.
    /// </summary>
    public static uint GetForegroundProcessId()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return 0;
        _ = GetWindowThreadProcessId(hwnd, out var pid);
        return pid;
    }

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
