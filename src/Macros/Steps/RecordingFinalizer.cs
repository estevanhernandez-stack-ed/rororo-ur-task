namespace Labs626.UrTask.Macros.Steps;

/// <summary>Turns a window-relative recording with clicks into a v4 point macro, keeping the
/// original events. Keyboard-only and screen-space recordings are returned unchanged: the
/// keyboard round-robin path is the dominant use and must not move.
/// <para>With <c>endMs</c> (when the recording was stopped), the point macro ends in a wait that
/// covers the time between the last input and the stop. Only step macros can carry it: an event
/// list has nowhere to put a time with no input attached, so those still end at their last event
/// until the schema gives them a field for it.</para></summary>
public static class RecordingFinalizer
{
    public static Macro WithSteps(Macro recording, int? displayScale, long? endMs = null)
    {
        if (recording.HasSteps) return recording;
        if (!recording.IsClientSpace) return recording;
        if (!recording.Events.Any(e => e.Kind == MacroEventKind.MouseDown)) return recording;
        var steps = StepConverter.Convert(recording.Events, endMs);
        if (steps.Count == 0) return recording;
        return recording with
        {
            SchemaVersion = Macro.CurrentSchemaVersion,
            Steps = steps,
            RecordedDisplayScale = displayScale ?? recording.RecordedDisplayScale,
        };
    }
}
