namespace Labs626.UrTask.Macros.Steps;

/// <summary>
/// A pixel that must keep its colour for a ClearAt pass to go on pressing (live safety bug
/// 2026-09-28 23:49: a clearing hold opened another player's profile, Friend and Trade buttons
/// included). X, Y, W and H are in the macro's recorded client pixels, placed and scaled like a
/// reach box. The runner averages the box before every point and every hold; a colour further
/// than <see cref="Tolerance"/> from <see cref="Expect"/> stops the playback before any more input.
/// Never saved: it lives on the in-memory ClearAt macro only.
/// </summary>
public sealed record ScreenGuard(int X, int Y, int W, int H, Rgb Expect, int Tolerance);
