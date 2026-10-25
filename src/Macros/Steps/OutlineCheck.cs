namespace Labs626.UrTask.Macros.Steps;

/// <summary>
/// The white outline the game draws on a block the pickaxe can break, while the pointer is on it
/// (ore-stop pulse spec, "The outline check"). The line is 1-2 px thick, which an averaged box
/// cannot see, so this counts instead: pixels in <see cref="Box"/> whose every channel is at least
/// <see cref="WhiteMin"/>, passing at <see cref="MinCount"/> or more. The box is relative to the
/// step point like a <see cref="CheckBox"/>, may be block-sized, and scales with the window like a
/// point (the count scales too, see <see cref="PointMath.ScaledCount"/>).
/// <para>Hand- or agent-written JSON: a missing <c>box</c> reads as null and a missing
/// <c>minCount</c> as 0; <c>StepValidator</c> refuses both with a sentence before playback.</para>
/// </summary>
public sealed record OutlineCheck(CheckBox Box, int MinCount, int WhiteMin = OutlineCheck.DefaultWhiteMin)
{
    /// <summary>"Each channel >= 225" (spec). A field, so a live measurement can move it.</summary>
    public const int DefaultWhiteMin = 225;

    /// <summary>Largest side at the recorded size. Block size follows how open the space is, not
    /// the zoom: the camera pops in to the first wall in its way, so a one-block shaft reads about
    /// 170 px, well past a Mine #8 block's ~56 px in the open. The box has to hold the whole frame
    /// even when the spot sits off the block's centre, so the cap is 240, not 120 (spec, "Block
    /// size is read every pass").</summary>
    public const int MaxSide = 240;
}
