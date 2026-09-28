namespace Labs626.UrTask.Macros.Steps;

/// <summary>Turns a window-relative recording with clicks into a v4 point macro, keeping the
/// original events. Keyboard-only and screen-space recordings are returned unchanged: the
/// keyboard round-robin path is the dominant use and must not move.</summary>
public static class RecordingFinalizer
{
    public static Macro WithSteps(Macro recording, int? displayScale)
    {
        if (recording.HasSteps) return recording;
        if (!recording.IsClientSpace) return recording;
        if (!recording.Events.Any(e => e.Kind == MacroEventKind.MouseDown)) return recording;
        var steps = StepConverter.Convert(recording.Events);
        if (steps.Count == 0) return recording;
        return recording with
        {
            SchemaVersion = Macro.CurrentSchemaVersion,
            Steps = steps,
            RecordedDisplayScale = displayScale ?? recording.RecordedDisplayScale,
        };
    }
}
