namespace Labs626.UrTask.Macros;

/// <summary>
/// Kind discriminator for <see cref="MacroEvent"/>. Maps directly to Win32
/// window messages from the low-level hooks; playback (task 6) reverses the
/// mapping back into SendInput INPUT structures.
/// </summary>
public enum MacroEventKind
{
    KeyDown,
    KeyUp,
    MouseMove,
    MouseDown,
    MouseUp,
    MouseWheel,
}

/// <summary>
/// One captured user-input event. Fields irrelevant to the kind hold sentinel
/// zeros. Flat shape (not a discriminated union) keeps JSON serialization
/// trivial and reflects the actual data shape Win32 hooks deliver — there's
/// no real saving from splitting into multiple types for the volume we record.
/// </summary>
public sealed record MacroEvent(
    long TimestampMs,
    MacroEventKind Kind,
    int VirtualKeyCode,
    int X,
    int Y,
    int MouseButton,
    int WheelDelta);
