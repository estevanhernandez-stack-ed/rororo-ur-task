using System.Diagnostics;
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

        // Atomic single-flight claim. Only one round-robin loop may run at a time. A
        // second concurrent entry must NOT clobber the in-flight loop's token — it
        // refuses (returns immediately) and runs nothing.
        var cts = CancellationTokenSource.CreateLinkedTokenSource(external);
        if (Interlocked.CompareExchange(ref _activeCts, cts, null) is not null)
        {
            cts.Dispose();
            return;
        }
        var ct = cts.Token;
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
                            var playResult = await _player.PlayAsync(asn.Macro, asn.Alt.RobloxUserId, ct).ConfigureAwait(false);
                            // Preflight refusals (e.g. client-space resize failed) and
                            // mid-playback aborts (e.g. foreground shifted) return
                            // before the player's Started/Ended events fire — without
                            // this, they vanish silently on the round-robin path.
                            // Skip emit if this is a user-initiated cancellation, not a genuine refusal/abort.
                            if (playResult.Outcome is PlaybackOutcome.Refused or PlaybackOutcome.Aborted)
                            {
                                if (!ct.IsCancellationRequested)
                                {
                                    EmitProgress(new AssignmentProgress(
                                        cycle, i, assignments.Count, asn, AssignmentPhase.Refused, playResult.Reason));
                                }
                            }
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

    private static bool SendSpaceKeepAlive()
    {
        const ushort VK_SPACE = 0x20;
        var down = SendKeyEvent(VK_SPACE, keyUp: false);
        Thread.Sleep(50); // briefly held
        var up = SendKeyEvent(VK_SPACE, keyUp: true);

        // SendInput returns the number of events inserted; 0 means Windows
        // rejected the call (e.g. cbSize mismatch). Surface it instead of
        // swallowing — a silent 0 here is exactly the bug that made keep-alive
        // a no-op for every release through v0.2.2.
        var ok = down == 1 && up == 1;
        if (!ok)
            Debug.WriteLine($"[AssignmentRunner] keep-alive Space rejected by SendInput (down={down}, up={up}).");
        return ok;
    }

    private static uint SendKeyEvent(ushort vk, bool keyUp)
    {
        var scanCode = (ushort)MapVirtualKey(vk, 0);
        var flags = keyUp ? 0x0002u : 0u; // KEYEVENTF_KEYUP

        var input = new INPUT { type = 1 };
        input.union.keyboard = new KEYBDINPUT { wVk = vk, wScan = scanCode, dwFlags = flags };
        return SendOne(ref input);
    }

    private static unsafe uint SendOne(ref INPUT input)
    {
        fixed (INPUT* p = &input) { return SendInput(1, p, Marshal.SizeOf<INPUT>()); }
    }

    /// <summary>
    /// Test seam: the cbSize this runner passes to SendInput. MUST equal the
    /// canonical Win32 INPUT size, or SendInput rejects every keep-alive event.
    /// </summary>
    internal static int KeepAliveInputStructSize => Marshal.SizeOf<INPUT>();

    // ---------- Win32 (minimal — mirrors MacroPlayer's interop for self-containment) ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion union; }

    // The union MUST be sized to its largest member (MOUSEINPUT). The keep-alive
    // only ever writes the keyboard field, but Win32 SendInput validates cbSize
    // against the full INPUT size — drop MOUSEINPUT and cbSize comes up 8 bytes
    // short (32 vs 40 on x64), SendInput fails, and the Space is never injected.
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mouse;
        [FieldOffset(0)] public KEYBDINPUT keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
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

// Refused covers both PlaybackOutcome.Refused (preflight declined, e.g. client-space
// resize failed) and PlaybackOutcome.Aborted (mid-playback foreground shift) from the
// player — from the round-robin's perspective both mean "this alt's macro didn't play
// to completion," surfaced with Reason so it isn't silently swallowed.
public enum AssignmentPhase { Focusing, Playing, Skipped, Refused, Stopped }

public sealed record AssignmentProgress(
    int Cycle,
    int IndexInCycle,       // -1 = no current alt (Stopped state)
    int TotalInCycle,
    Assignment? Current,
    AssignmentPhase Phase,
    string? Reason = null); // set on Phase == Refused; carries PlaybackResult.Reason
