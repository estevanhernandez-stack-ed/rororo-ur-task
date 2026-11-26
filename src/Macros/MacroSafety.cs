using Labs626.UrTask.Macros.Steps;

namespace Labs626.UrTask.Macros;

/// <summary>
/// What a macro may not contain, whoever wrote it. A macro can come from a shared bundle or a
/// file another tool wrote, and some keys are acted on by Windows itself whatever window is in
/// front: the Windows key opens Start and starts Win chords, the browser and launch keys start
/// programs, Sleep suspends the PC. Playback only checks that Roblox is in front; it cannot keep
/// those keys inside the game. So a macro that presses one is refused, with the same sentence, at
/// import (<see cref="MacroBundle.Parse"/>) and at playback (<see cref="MacroPlayer"/>).
/// <para>Not at load: a macro already in the library stays listed, so the user can see it and
/// delete it.</para>
/// <para>There is no "click outside the game window" rule here, on purpose. The recorder writes
/// every mouse press on the screen relative to the game window with no bounds, so a recording
/// stopped with the STOP button (not the hotkey) ends with a press on Ur Task's own window,
/// outside the recorded size. Refusing those would refuse recordings that play today.</para>
/// </summary>
public static class MacroSafety
{
    /// <summary>
    /// Null when the macro may be imported and played; otherwise one plain sentence about the
    /// first problem. Pure. Looks at the event list (key presses and releases), every key step,
    /// and the events inside raw steps. The event list is checked even when steps are what play:
    /// it is the kept original, and what an export or a hand edit falls back to.
    /// </summary>
    public static string? FirstProblem(Macro macro)
    {
        if (macro is null) throw new ArgumentNullException(nameof(macro));

        if (FirstRefusedKey(macro.Events) is { } inEvents) return KeyProblem(inEvents, step: null);

        if (macro.Steps is { } steps)
        {
            for (int i = 0; i < steps.Count; i++)
            {
                var name = steps[i] switch
                {
                    KeyStep k => RefusedKeyName(k.VirtualKeyCode),
                    RawStep r => FirstRefusedKey(r.Events),
                    _ => null,
                };
                if (name is not null) return KeyProblem(name, step: i + 1);
            }
        }

        return null;
    }

    /// <summary>
    /// The name of a key no macro may press, or null for every other key. Only keys the shell
    /// acts on outside the focused window are here. Alt, F4, Tab, Esc and the rest go to the
    /// window in front, which playback already makes sure is the game.
    /// </summary>
    public static string? RefusedKeyName(int virtualKeyCode) => virtualKeyCode switch
    {
        0x5B or 0x5C => "the Windows key",          // VK_LWIN, VK_RWIN
        0x5F => "the Sleep key",                    // VK_SLEEP
        >= 0xA6 and <= 0xAC => "a browser key",     // VK_BROWSER_BACK .. VK_BROWSER_HOME
        0xB4 => "the Mail key",                     // VK_LAUNCH_MAIL
        0xB5 => "the Media key",                    // VK_LAUNCH_MEDIA_SELECT
        0xB6 or 0xB7 => "an app launch key",        // VK_LAUNCH_APP1, VK_LAUNCH_APP2
        _ => null,
    };

    // A release counts as much as a press: a macro has no business with these keys at all, so
    // neither half of one gets through.
    private static string? FirstRefusedKey(IReadOnlyList<MacroEvent>? events)
    {
        if (events is null) return null;
        foreach (var e in events)
        {
            if (e is null || e.Kind is not (MacroEventKind.KeyDown or MacroEventKind.KeyUp)) continue;
            if (RefusedKeyName(e.VirtualKeyCode) is { } name) return name;
        }
        return null;
    }

    private static string KeyProblem(string keyName, int? step)
        => $"This macro presses {keyName}{(step is int n ? $" at step {n}" : "")}. "
         + "Windows acts on that key even while Roblox is in front, so Ur Task will not import or play it. "
         + "Record it again without that key, or delete it.";
}
