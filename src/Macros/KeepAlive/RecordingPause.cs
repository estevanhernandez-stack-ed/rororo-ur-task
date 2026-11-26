namespace Labs626.UrTask.Macros;

/// <summary>
/// Keep-alive and recording do not run at the same time (owner ruling, 0.13.1). The recorder cannot
/// tell an injected Space from a typed one, so a running loop wrote its own Spaces into the macro, and
/// a turbo sweep's input guard swallowed the user's real keys mid-recording. Pressing record pauses the
/// loop; stopping brings it back, the way break-glass does.
/// <para>This is the decision only: was the loop running when the recording began, has someone started
/// a loop since, is break-glass on. The runtime does the stopping and starting.</para>
/// </summary>
internal sealed class RecordingPause
{
    public const string PausedLine = "Keep-alive is paused while you record. It starts again when you stop.";
    public const string ResumedLine = "Keep-alive is back on.";
    public const string RefusedLine = "Stop recording first. Keep-alive can't run while you record.";

    private readonly object _gate = new();
    private bool _recording;
    private bool _restore;

    /// <summary>True while a recording holds a loop it means to hand back.</summary>
    public bool IsPaused { get { lock (_gate) return _restore; } }

    /// <summary>A recording is starting. Returns true when the loop is running and must be stopped first.</summary>
    public bool Begin(bool loopRunning)
    {
        lock (_gate)
        {
            _recording = true;
            _restore = loopRunning;
            return loopRunning;
        }
    }

    /// <summary>A loop started while the recording was in progress. Ctrl+Shift+P is refused meanwhile, so
    /// this is the user running a routine: the loop is theirs now, and stop leaves it as it finds it.
    /// Ignored outside a recording, which is where the restart this class asks for lands.</summary>
    public void LoopStartedMeanwhile()
    {
        lock (_gate)
        {
            if (_recording) _restore = false;
        }
    }

    /// <summary>The recording ended, however it ended. Returns true when the loop must be started again:
    /// it was running when the recording began, nobody started one since, and break-glass is not on.
    /// Handed out once, so two stops racing each other restart it once.</summary>
    public bool End(bool breakglassOn)
    {
        lock (_gate)
        {
            var resume = _restore && !breakglassOn;
            _recording = false;
            _restore = false;
            return resume;
        }
    }

    /// <summary>Ctrl+Shift+P during a recording: starting the loop is refused, stopping one that is
    /// somehow running is not.</summary>
    public static bool RefusesLoopStart(bool recording, bool loopRunning) => recording && !loopRunning;
}
