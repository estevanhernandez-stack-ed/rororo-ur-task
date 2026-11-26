namespace Labs626.UrTask.Macros;

/// <summary>
/// The keep-or-drop rule for keys in a per-window recording. The keyboard hook sees every key on the
/// machine, so without this a recording bound to one account also holds whatever was typed into a
/// browser or a chat window while it ran, and replays it into the game.
/// <para>A press is kept only while the bound account's window is in front. A release is kept exactly
/// when its press was, so the recording never holds a key down that it has no release for, and never
/// carries a release for a press it dropped. Pure, and touched only from the hook thread.</para>
/// </summary>
internal sealed class PerWindowKeyFilter
{
    /// <summary>Keys whose press is in the recording and whose release is still to come.</summary>
    private readonly HashSet<int> _recordedDown = new();

    /// <param name="boundInFront">Whether the bound account's window is the foreground window. Read
    /// only for a press, so the caller need not work it out for a release.</param>
    public bool Keep(MacroEventKind kind, int virtualKeyCode, bool boundInFront)
    {
        switch (kind)
        {
            case MacroEventKind.KeyDown:
                // A repeat of a held key that arrives over another window is dropped like any other
                // press there, but the key stays held in the recording until its release comes.
                if (!boundInFront) return false;
                _recordedDown.Add(virtualKeyCode);
                return true;
            case MacroEventKind.KeyUp:
                return _recordedDown.Remove(virtualKeyCode);
            default:
                return true;   // the mouse path has its own rules
        }
    }
}
