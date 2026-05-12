using System.Runtime.InteropServices;
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.Macros;

/// <summary>
/// Round-robin runner. Takes a list of (alt, macro?) tuples and cycles
/// through them forever until cancellation. For each alt: focus, settle,
/// play macro (or send Space keep-alive if macro is null), then move on.
/// </summary>
internal sealed class AssignmentRunner
{
    private const int DefaultPerAltDelayMs = 1000; // 1s settle before each alt's action
    private const int KeepAliveDelayMs = 200;       // after Space, small wait before moving on

    private readonly IMacroPlayer _player;
    private readonly IForegroundWatcher _foreground;
    private readonly Func<int, (bool ok, string? error)> _focus;
    private CancellationTokenSource? _activeCts;

    public AssignmentRunner(IMacroPlayer player, IForegroundWatcher foreground)
        : this(player, foreground, Win32Focus.AttachAndFocus) { }

    internal AssignmentRunner(IMacroPlayer player, IForegroundWatcher foreground, Func<int, (bool, string?)> focus)
    {
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _foreground = foreground ?? throw new ArgumentNullException(nameof(foreground));
        _focus = focus ?? throw new ArgumentNullException(nameof(focus));
    }

    public event EventHandler<AssignmentProgress>? Progress;

    public bool IsRunning => _activeCts is not null;

    /// <summary>
    /// Run the round-robin loop forever (until cancellation). Each iteration
    /// is one full pass through the assignments. Yields after each pass so
    /// the caller can observe progress.
    /// </summary>
    public async Task RunAsync(
        IReadOnlyList<Assignment> assignments,
        CancellationToken external = default)
    {
        if (assignments is null) throw new ArgumentNullException(nameof(assignments));
        if (assignments.Count == 0) return;

        _activeCts = CancellationTokenSource.CreateLinkedTokenSource(external);
        var ct = _activeCts.Token;
        var cycle = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                cycle++;
                for (int i = 0; i < assignments.Count; i++)
                {
                    if (ct.IsCancellationRequested) break;
                    var asn = assignments[i];

                    // Phase: Focusing
                    EmitProgress(new AssignmentProgress(cycle, i, assignments.Count, asn, AssignmentPhase.Focusing));
                    if (!_focus(asn.Alt.Pid).ok)
                    {
                        EmitProgress(new AssignmentProgress(cycle, i, assignments.Count, asn, AssignmentPhase.Skipped));
                        continue;
                    }
                    try { await Task.Delay(DefaultPerAltDelayMs, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }

                    // Verify foreground actually flipped
                    var fg = _foreground.ResolveForegroundAccount();
                    if (fg is null || fg.RobloxUserId != asn.Alt.RobloxUserId)
                    {
                        EmitProgress(new AssignmentProgress(cycle, i, assignments.Count, asn, AssignmentPhase.Skipped));
                        continue;
                    }

                    // Phase: Playing
                    EmitProgress(new AssignmentProgress(cycle, i, assignments.Count, asn, AssignmentPhase.Playing));
                    if (asn.Macro is not null)
                    {
                        try
                        {
                            await _player.PlayAsync(asn.Macro, asn.Alt.RobloxUserId, ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) { break; }
                    }
                    else
                    {
                        // Keep-alive: send a single Space key-press
                        SendSpaceKeepAlive();
                        try { await Task.Delay(KeepAliveDelayMs, ct).ConfigureAwait(false); }
                        catch (OperationCanceledException) { break; }
                    }
                }
            }
            EmitProgress(new AssignmentProgress(cycle, -1, assignments.Count, null, AssignmentPhase.Stopped));
        }
        finally
        {
            _activeCts?.Dispose();
            _activeCts = null;
        }
    }

    public bool Abort()
    {
        var cts = _activeCts;
        if (cts is null) return false;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        return true;
    }

    private void EmitProgress(AssignmentProgress p)
    {
        try { Progress?.Invoke(this, p); } catch { /* swallow */ }
    }

    private static void SendSpaceKeepAlive()
    {
        const ushort VK_SPACE = 0x20;
        SendKeyEvent(VK_SPACE, keyUp: false);
        Thread.Sleep(50); // briefly held
        SendKeyEvent(VK_SPACE, keyUp: true);
    }

    private static void SendKeyEvent(ushort vk, bool keyUp)
    {
        var scanCode = (ushort)MapVirtualKey(vk, 0);
        var flags = keyUp ? 0x0002u : 0u; // KEYEVENTF_KEYUP

        var input = new INPUT { type = 1 };
        input.union.keyboard = new KEYBDINPUT { wVk = vk, wScan = scanCode, dwFlags = flags };
        SendOne(ref input);
    }

    private static unsafe void SendOne(ref INPUT input)
    {
        fixed (INPUT* p = &input) { _ = SendInput(1, p, Marshal.SizeOf<INPUT>()); }
    }

    // ---------- Win32 (minimal — duplicates MacroPlayer's interop for self-containment) ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion union; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern unsafe uint SendInput(uint cInputs, INPUT* pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);
}

public sealed record Assignment(AccountRegistry.AccountInfo Alt, Macro? Macro);

public enum AssignmentPhase { Focusing, Playing, Skipped, Stopped }

public sealed record AssignmentProgress(
    int Cycle,
    int IndexInCycle,       // -1 = no current alt (Stopped state)
    int TotalInCycle,
    Assignment? Current,
    AssignmentPhase Phase);
