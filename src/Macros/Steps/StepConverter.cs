namespace Labs626.UrTask.Macros.Steps;

/// <summary>
/// Collapses a recording into steps. The path between clicks is dropped; a click's delay is
/// the time the mouse sat still before it (time spent moving is hand travel, replaced at
/// playback by a fixed jump and wiggle). Drags, the wheel and keys keep their meaning.
/// </summary>
public static class StepConverter
{
    public const int SameSpotPx = 8;   // spec §2, amended from 4: the real egg recording has a 6 px click
    public const int TravelGapMs = 150;
    public const double CameraDragMargin = 1.5;

    private static readonly HashSet<int> Modifiers = new() { 0x10, 0x11, 0x12, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5 };

    public static IReadOnlyList<MacroStep> Convert(IReadOnlyList<MacroEvent> events)
    {
        var steps = new List<MacroStep>();
        var heldKeys = new HashSet<int>();
        long anchorMs = 0;        // delays are measured from here
        long travelMs = 0;        // motion since the anchor
        (int X, int Y)? lastMove = null;
        long lastMoveMs = 0;
        int nextId = 1;

        MacroEvent? down = null;  // the pending press
        int downIndex = -1;
        long downDelay = 0;

        List<MacroEvent>? raw = null; // collecting a raw stretch
        HashSet<int>? rawButtons = null;
        long rawDelay = 0;
        string rawNote = "";

        // Travel is dropped only from the gap before a mouse press or wheel (the hand moving to
        // the spot), and never while a key is held: a key's up is measured from the last step,
        // so travel cut from a click inside the hold would be cut from the key's held time too.
        // Keys keep their real timing. Playback's jump and press add about 230 ms per click
        // made during a hold, the same as any click; the recorded time itself is never lost.
        int Delay(long at, bool dropTravel = true) => (int)Math.Clamp(at - anchorMs - (dropTravel ? travelMs : 0), 0, int.MaxValue);
        void Anchor(long at) { anchorMs = at; travelMs = 0; }

        for (int i = 0; i < events.Count; i++)
        {
            var e = events[i];

            if (raw is not null)
            {
                raw.Add(e);
                if (e.Kind == MacroEventKind.MouseDown) rawButtons!.Add(e.MouseButton);
                if (e.Kind == MacroEventKind.MouseUp) rawButtons!.Remove(e.MouseButton);
                if (rawButtons!.Count == 0)
                {
                    steps.Add(Raw(rawDelay, raw, rawNote));
                    Anchor(e.TimestampMs);
                    raw = null; rawButtons = null;
                }
                continue;
            }

            switch (e.Kind)
            {
                case MacroEventKind.MouseMove:
                    if (down is null && lastMove is { } lm && (lm.X != e.X || lm.Y != e.Y)
                        && e.TimestampMs - lastMoveMs <= TravelGapMs && lastMoveMs >= anchorMs)
                        travelMs += e.TimestampMs - lastMoveMs;
                    lastMove = (e.X, e.Y);
                    lastMoveMs = e.TimestampMs;
                    break;

                case MacroEventKind.KeyDown:
                    if (!heldKeys.Add(e.VirtualKeyCode)) break; // auto-repeat
                    steps.Add(new KeyStep(Delay(e.TimestampMs, dropTravel: false), e.VirtualKeyCode, true));
                    Anchor(e.TimestampMs);
                    break;

                case MacroEventKind.KeyUp:
                    if (!heldKeys.Remove(e.VirtualKeyCode)) break; // orphan (e.g. the record hotkey's release)
                    steps.Add(new KeyStep(Delay(e.TimestampMs, dropTravel: false), e.VirtualKeyCode, false));
                    Anchor(e.TimestampMs);
                    break;

                case MacroEventKind.MouseWheel:
                    steps.Add(new WheelStep(Delay(e.TimestampMs, dropTravel: heldKeys.Count == 0), e.X, e.Y, e.WheelDelta));
                    Anchor(e.TimestampMs);
                    break;

                case MacroEventKind.MouseDown:
                    if (down is not null)
                    {
                        raw = new List<MacroEvent>();
                        for (int j = downIndex; j <= i; j++) raw.Add(events[j]);
                        rawButtons = new HashSet<int> { down.MouseButton, e.MouseButton };
                        rawDelay = downDelay;
                        rawNote = "Two mouse buttons were held at once.";
                        down = null;
                        break;
                    }
                    down = e;
                    downIndex = i;
                    downDelay = Delay(e.TimestampMs, dropTravel: heldKeys.Count == 0);
                    break;

                case MacroEventKind.MouseUp:
                    if (down is null || down.MouseButton != e.MouseButton) break; // orphan release
                    var dx = e.X - down.X;
                    var dy = e.Y - down.Y;
                    if (Math.Abs(dx) <= SameSpotPx && Math.Abs(dy) <= SameSpotPx)
                    {
                        steps.Add(new PointStep((int)downDelay, $"p{nextId++}", null, down.X, down.Y, down.MouseButton));
                    }
                    else
                    {
                        // Right drag = camera turn. The margin drives pitch (vertical) to its limit;
                        // yaw has no limit, so overshooting it would change the heading.
                        var m = down.MouseButton == 2 ? CameraDragMargin : 1.0;
                        steps.Add(new DragStep((int)downDelay, down.MouseButton, down.X, down.Y,
                            dx, (int)Math.Round(dy * m), (int)(e.TimestampMs - down.TimestampMs)));
                    }
                    Anchor(e.TimestampMs);
                    down = null;
                    break;
            }
        }

        if (raw is not null) steps.Add(Raw(rawDelay, raw, rawNote));
        else if (down is not null)
        {
            var rest = new List<MacroEvent>();
            for (int j = downIndex; j < events.Count; j++) rest.Add(events[j]);
            steps.Add(Raw(downDelay, rest, "A mouse button was pressed and never released."));
        }

        // The stop hotkey leaves modifier presses at the very end (the egg ends LCtrl down,
        // LShift down, LShift up, LShift down). In the trailing run of modifier key steps, drop
        // every down that has no later up, so Ctrl can never be left held. A complete down/up
        // pair in the run is a real press and stays. Walk backwards so indices stay valid.
        int runStart = steps.Count;
        while (runStart > 0 && steps[runStart - 1] is KeyStep rk && Modifiers.Contains(rk.VirtualKeyCode))
            runStart--;
        for (int j = steps.Count - 1; j >= runStart; j--)
        {
            if (steps[j] is KeyStep { Down: true } d
                && !steps.Skip(j + 1).Any(s => s is KeyStep { Down: false } u && u.VirtualKeyCode == d.VirtualKeyCode))
                steps.RemoveAt(j);
        }

        return steps;
    }

    private static RawStep Raw(long delay, List<MacroEvent> evs, string note)
    {
        var t0 = evs[0].TimestampMs;
        return new RawStep((int)delay, evs.Select(x => x with { TimestampMs = x.TimestampMs - t0 }).ToList(), note);
    }
}
