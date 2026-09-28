# Any-screen AutoHotkey macros for Roblox

Your click-and-pixel macro is great work, but I'm sure you've heard from users who say it doesn't
work, and they don't even know how to begin with their resolution. Maybe this can help. It covers
what we changed in the Automining disconnect monitor so it runs on any monitor and any Windows
display-scale setting, why each change was needed, and how to make the same changes to any other
macro.

Two files travel together:

- `AutominingDisconnectAHK_AnyScreen.ahk` is the converted script. The original is untouched.
- This document.

The approach is the one RoRoRo Ur Task uses for its shareable mouse macros, plus two things we only
learned by testing this script on a second PC.

## What changed in the script

| Area | Original | Any-screen version |
| --- | --- | --- |
| Where points are measured from | The window's outer corner, from `WinGetPos` | The corner of the game picture, from `WinGetClientPos` |
| What gets sized | The outer window, to 800x600 | The game picture, to 800x599 times the display scale |
| Display scale | Ignored | Size and every point are multiplied by the scale of the Roblox window's monitor |
| After resizing | Assumed it worked | Reads the size back, allows a few pixels, and refuses with a message beyond that |
| Window partly off-screen | Not handled | Slid back onto its monitor before any click |
| Monitors with different scales | Coordinates can come back scaled down | The script asks Windows for real pixels on every monitor |
| "Player loaded in" check | One exact pixel | The exact pixel at 100%, a small box at any other scale |
| Disconnect check | Numbers written inside the function | A `DisconnectBoxes` setting, in game-picture points |
| Click points | Fixed numbers in the file | Named, listed, draggable on screen, and restorable, per PC |
| Measuring a new point | Window Spy | F5 shows the point and colour under the mouse, the same number on any PC |
| Rejoining | VIP link only | VIP link, a friend, or the VIP link with a friend as backup |
| Roblox closes mid-sequence | Can raise an error dialog | The click is skipped and the next disconnect check recovers |
| Hotbar key after the automine toggle | Pressed 0.3 seconds later | Pressed after `HotbarAfterAutomineMs`, 3 seconds by default. The item does not take when it is used straight after the toggle |

**Settings.** `RobloxWindowWidth` and `RobloxWindowHeight` are gone. `GameAreaW`, `GameAreaH` and
`GameAreaSlop` replace them, and `UiScaleOverride` is there in case Windows ever reports the wrong
scale. Every X and Y is the original number minus the window frame, which is 8 for X and 31 for Y.

**Hotkeys.** F5 is new. F6 through F10 are unchanged. F5 only fires while the mouse is over a
Roblox window, so it is still a normal F5 in a browser.

**Status window.** A Friend box, a "Rejoin with" choice, and a Click points button are new.

**The ini.** `Reconnect/Friend` and `Reconnect/Mode` are new keys. Moved click points are saved in a
section named for the display scale, such as `[Points@125]`.

## Using it

1. Put the script in a folder of its own and run it. Paste the VIP link as before.
2. Take Roblox out of fullscreen. Press **F9**. The script sizes the game picture. If it cannot, it
   says what size it got and stops.
3. Press **F7** to start, or **F10** to test one reconnect.
4. If a click misses on this PC, open **Click points** and press **Drag on screen**. A named tag
   appears on every spot the script clicks. The red corner of a tag is the exact spot. Drag the tag
   that is off onto the right place. **Restore original** puts it back.
5. Do step 4 before pressing F7. Tags are refused while the script is working, because a tag
   sitting on a click point would swallow the click.

## Why the original missed on other PCs

The original does two sensible things. It asks for an 800x600 Roblox window, and it measures every
click from the window's top-left corner. Both quietly depend on the Windows display-scale setting,
in three separate ways.

**The frame changes size.** These are Windows' own frame sizes for a normal resizable window:

| Windows scale | Frame, left | Frame, top |
| --- | --- | --- |
| 100% | 8 | 31 |
| 125% | 9 | 38 |
| 150% | 11 | 45 |
| 175% | 12 | 52 |
| 200% | 13 | 58 |

On a 150% laptop, "53, 245 from the corner" lands 3 px left and 14 px higher in the game than it
did on your PC.

**Roblox never gave you 800x600.** Roblox refuses to shrink below a minimum window size, and that
minimum is bigger than 800x600. At 100% scale it is an 816x638 window holding an 800x599 game
picture. Your `WinMove` to 800x600 was silently bumped up to that, so 800x599 is the size your
points were really calibrated on. Roblox scales that minimum with the display scale:

| Windows scale | Smallest Roblox game picture | How we know |
| --- | --- | --- |
| 100% | 800 x 599 | Measured, 56 log entries on a 100% PC |
| 125% | 1002 x 750 | Measured on Dunder-MiffLan, 3840x2160 at 125% |
| 150% | 1202 x 901 | Predicted from the two above |
| 200% | 1606 x 1205 | Predicted from the two above |

**Roblox enlarges its UI.** Roblox draws its UI in 100%-scale units and enlarges it by the Windows
scale setting. Roblox staff confirmed this on the developer forum in March 2026. On a 125% PC every
button is 1.25x as big and 1.25x as far from the corner.

**What that adds up to on a real PC: Dunder-MiffLan, a 3840x2160 desktop.** This is the PC the converted script was
tested on. It runs at 3840x2160 with Windows set to 125%. Here is your original script on your PC,
next to the same script on Dunder-MiffLan. The window and frame sizes were measured there. Where the
original's points land is worked out from them.

| | Your PC, 100% | Dunder-MiffLan, 3840x2160 at 125% |
| --- | --- | --- |
| Window Roblox gives an 800x600 request | 816 x 638 | 1020 x 797 |
| Game picture inside it | 800 x 599 | 1002 x 750 |
| Frame, left and top | 8, 31 | 9, 38 |
| Where "54, 432 from the corner" lands | On the Mine world button | About 107 px above it, more than a whole button too high |
| What the loaded check "53, 245" reads | The orange of the gift icon | A spot about 43 px above the orange, so it never sees it |
| What the user sees | It works | Stuck on "Waiting for player to load in". The clicks never happen, and a reconnect relaunches Roblox over and over |

That matches what happened. On Dunder-MiffLan the original did not work with the screen at its full
3840x2160. It worked once the screen was dropped to 1920x1080. An experienced user reaches for that
fix without thinking. A lot of users will not get that far. They will assume the macro does not
work and message you about it. The fix most likely works because Windows changes the scale along
with the resolution.

The converted script on Dunder-MiffLan asks for a 1000 x 749 game picture, accepts the 1002 x 750
Roblox allows, and aims at 58, 501 for the Mine world button. Every point landed within a button's
width. The five points were nudged into place by dragging tags, and the load-in sequence ran through.

Your users will call this a resolution problem, and they are half right. A high-resolution screen
is the reason Windows runs above 100%, and that scale is what moves the buttons. A 4K screen set to
100% behaves exactly like a 1080p screen at 100%. The converted script covers both: the monitor's
resolution never enters its math, and the scale is multiplied in. Most laptops ship at 125% or 150%.

## The four rules

**1. Measure from the game picture, not the window corner.** Windows calls it the client area: the
picture inside the title bar and borders. `WinGetClientPos` gives you its position on screen, where
`WinGetPos` gives you the outer corner. A point measured from the game picture does not care how
thick the frame is.

**2. Size the game picture, not the window.** Roblox lays out its buttons from the size of the game
picture. Ask for the picture size you calibrated on and let the outer window be whatever it has to
be. The frame is just the outer size minus the picture size, so:

```text
new outer size = wanted game picture + (current outer size - current game picture)
```

**3. Multiply by the display scale.** To get the same layout on a 125% PC, the game picture has to
be 1.25x as big and every point 1.25x as far in. `GetDpiForWindow` divided by 96 gives the scale of
the monitor the Roblox window is on.

**4. Read the size back, allow a few pixels, refuse beyond that.** Never assume a resize worked.
Roblox's minimum comes out a hair bigger than the scaled target above 100%: at 125% you want
1000x749 and the smallest Roblox allows is 1002x750. Two pixels of picture size does not move a
button, so accept a small difference. A window that is fullscreen or far off the wanted size is a
different story. Clicking anyway is how a toggle button ends up switched off, so stop and say what
went wrong instead.

If you are starting a new macro, calibrate a little above Roblox's minimum, say a 900x650 game
picture. Every PC can then hit the target exactly and rule 4's slack never comes into play.

## The helpers

Drop these in and route every click and pixel read through them. Keep `CoordMode "Mouse", "Screen"`
and `CoordMode "Pixel", "Screen"`. Screen coordinates worked out from the game picture at click
time hit the right window wherever it sits, focused or not.

```autohotkey
GameAreaW := 800          ; the game-picture size you calibrated on, at 100% scale
GameAreaH := 599
GameAreaSlop := 6         ; how far off it may end up, in 100%-scale px, and still count

; Real screen pixels on every monitor, including one with a different scale.
UsePhysicalPixels() {
    try {
        if !DllCall("SetThreadDpiAwarenessContext", "ptr", -4, "ptr")
            DllCall("SetThreadDpiAwarenessContext", "ptr", -3, "ptr")
    }
}

; 1 at 100%, 1.25 at 125%, 1.5 at 150%.
GetUiScale(window) {
    dpi := 0
    try dpi := DllCall("GetDpiForWindow", "ptr", WinGetID(window), "uint")
    return (dpi > 0 ? dpi : A_ScreenDPI) / 96
}

; Where the game picture is right now. Read it fresh before every click.
GetGameArea(window) {
    UsePhysicalPixels()
    WinGetClientPos(&x, &y, &w, &h, window)
    return {x: x, y: y, w: w, h: h, s: GetUiScale(window)}
}

; Game-picture point -> screen pixel.
ToScreenX(area, px) => area.x + Round(px * area.s)
ToScreenY(area, py) => area.y + Round(py * area.s)

; True when the game picture is the calibrated size, give or take the slop.
GameAreaIsSized(window) {
    global GameAreaW, GameAreaH, GameAreaSlop
    UsePhysicalPixels()
    s := GetUiScale(window)
    slop := Max(2, Round(GameAreaSlop * s))
    WinGetClientPos(, , &cw, &ch, window)
    return Abs(cw - Round(GameAreaW * s)) <= slop && Abs(ch - Round(GameAreaH * s)) <= slop
}

; Size the GAME PICTURE. Returns false if it ended up further off than the slop.
SetGameAreaSize(window) {
    global GameAreaW, GameAreaH
    UsePhysicalPixels()
    s := GetUiScale(window)
    wantW := Round(GameAreaW * s), wantH := Round(GameAreaH * s)
    Loop 2 {
        WinGetPos(, , &ow, &oh, window)
        WinGetClientPos(, , &cw, &ch, window)
        if (Abs(cw - wantW) <= 1 && Abs(ch - wantH) <= 1)
            return true
        WinMove(, , wantW + (ow - cw), wantH + (oh - ch), window)
        Sleep(150)
    }
    return GameAreaIsSized(window)
}
```

A click then reads like this:

```autohotkey
area := GetGameArea(RobloxWindow)
Click(ToScreenX(area, AfterLoadX), ToScreenY(area, AfterLoadY))
```

## Converting the numbers you already have

If you calibrated on a Windows 10 or 11 PC at 100% scale, your frame was 8 px on the left and 31 px
on top. Subtract those once and you are done:

| Point | From the window corner | Game-picture point |
| --- | --- | --- |
| Loaded check | 53, 245 | 45, 214 |
| Mine world button | 54, 432 | 46, 401 |
| Automine toggle | 51, 359 | 43, 328 |
| 5th mine, first | 110, 245 | 102, 214 |
| 5th mine, second | 672, 410 | 664, 379 |
| Disconnect button | 543, 446 | 535, 415 |

The game picture to reproduce is 800 x 599, which is what Roblox really gave your 800x600 request.

This conversion has a useful property. On a 100% PC the converted script clicks exactly the same
screen pixels as the original, so nothing changes for anyone it already works for. That was checked
by running both methods against a window with Roblox's minimum size and comparing the pixels.

If you calibrated at a different scale, press F5 in the converted script over your Roblox window. It
shows your own frame sizes and scale. Subtract those frame numbers instead of 8 and 31, divide by
your scale, and use the game-picture size F5 reports, divided by your scale, for `GameAreaW` and
`GameAreaH`.

## Converting any other macro

The same eight steps work for any click-and-pixel macro that targets one Roblox window.

1. **Find what it really calibrated on.** Run the macro's own resize, then read the game-picture
   size and the frame's left and top. F5 in the converted script shows all three. Window Spy's
   Client row works too.
2. **Set `GameAreaW` and `GameAreaH`** to that picture size, divided by your display scale.
3. **Convert every X and Y.** Subtract the frame's left from X and its top from Y, then divide by
   your scale.
4. **Paste the helpers** from the section above.
5. **Replace every anchor.** Wherever the macro adds a number to `WinGetPos` output, call
   `GetGameArea` instead and wrap the number in `ToScreenX` or `ToScreenY`. Do it at click time,
   not once at the top, so a moved window never matters.
6. **Replace the resize** with `SetGameAreaSize`, and stop with a message when it returns false.
7. **Loosen one-pixel checks.** At any scale other than 100%, search a small box around the spot.
8. **Test twice.** At 100%, confirm it behaves exactly as before. At 125%, press F5 over each
   target and compare the numbers.

Put the points in a named table, as the converted script does with `PointDefs`, if you want your
users to be able to re-aim them.

### If you want it repeatable

This one is an idea, not something we built. The helpers are the same in every macro, so after your
first conversion they can live in one file of their own, say `AnyScreen.ahk`, loaded with one line
at the top of each macro. What stays in a macro is only what is different about it: the
game-picture size it was calibrated on, and its points.

```autohotkey
#Include AnyScreen.ahk        ; UsePhysicalPixels, GetUiScale, GetGameArea, ToScreenX, ToScreenY,
                              ; GameAreaIsSized, SetGameAreaSize

GameAreaW := 800              ; this macro's own calibration
GameAreaH := 599
GameAreaSlop := 6
```

From then on a new macro costs about what it did before. You measure points with F5 where you used
Window Spy, and you write `ToScreenX(area, x)` where you used to add a number to the window corner.
The Click points window could move into the same file. It needs the macro's `PointDefs` table and
has a few hooks into this particular script, so it would take some untangling first.

## Measuring new points

Press **F5** in the converted script with the mouse over the spot. It shows the game-picture point
and the colour under the cursor, and copies `x, y, colour` to the clipboard. It divides by the
display scale, so a point measured on a 150% PC comes out as the same number a 100% PC would give.

Window Spy works too. Read the **Client** row, never the Window or Screen row, and divide by your
scale if you are not at 100%.

## When the math is not enough: let users re-aim a point

The scale rules assume the game enlarges everything evenly. Roblox does, but a game can position one
of its own buttons by its own logic, and that button will sit somewhere else at 125% than the math
predicts. The first real test at 125% found exactly that: the window sized correctly, and the
left-hand buttons sat up to a button's width from where the math put them. One point, the loaded
check, had slipped off the orange it looks for.

No formula fixes that, so the converted script lets each user re-aim a point on their own PC without
opening the file. The **Click points** button opens a list of every in-game spot the script clicks:

| # | Click point | Now | Original | Status |
| --- | --- | --- | --- | --- |
| 1 | Loaded check - the orange that shows once the player is in | 45, 214 | 45, 214 | original |
| 2 | Mine world button - clicked after loading in | 56, 408 | 46, 401 | moved |

- **Drag on screen** puts a named, numbered tag on every spot, over the Roblox window. The red
  corner of a tag is the exact spot, and the tag sits beside it, so it never covers what it points
  at. Drag a tag and drop it. A grey square marks the original spot of any point that was moved.
- **Set with F5** is the same thing without tags: press it, hold the mouse over the spot, press F5.
- **Restore original** and **Restore all** put points back. The original numbers in the script are
  never changed, and the list always shows them.

Three design choices are worth copying:

- **Moved points are saved per display scale.** They go in the ini under a section such as
  `[Points@125]`. A point moved at 125% never applies at 100%, so a docked laptop cannot poison
  itself. It also means a user can send you that section and you can see exactly which button
  moved, and by how much, on their setup.
- **Points stay in 100%-scale units.** A captured spot is divided by the scale before it is saved,
  to one decimal, so it lands back on the very same pixel.
- **Tags are refused while the script is working.** A tag sitting on a click point would swallow
  the click, and one sitting on the loaded-check pixel would hide the colour. Starting the monitor
  or a test reconnect closes the tags.

The names come from your own variable names and comments. If a label is wrong, the `PointDefs`
table near the top of the script is the one place to fix it. The disconnect dialog is not in the
list because it only appears during a real disconnect, so there is nothing to aim at by hand.

## Rejoining through a friend instead of the VIP link

When an account is captcha-locked, the VIP link only opens a captcha, so the reconnect never
finishes. Joining a friend who is already in the server is a different request to Roblox:

```text
roblox://experiences/start?userId=123456789
```

That is the same as pressing Join on the friend's profile. It needs no place ID and no VIP code, and
it runs through the same `Run(link)` call the script already uses, with the login the Roblox app
already holds.

The converted script adds a **Friend** box, which takes a user ID or a profile link, and a **Rejoin
with** choice:

- **VIP link.** The original behaviour.
- **Friend.** Always joins the friend.
- **VIP link, friend as backup.** Odd attempts use the VIP link and even attempts join the friend.
  An account that gets captcha-locked overnight recovers on the next attempt without anyone
  touching the script.

Roblox decides whether the join is allowed. The two accounts must be friends. The friend's "who can
join me" privacy setting must allow it. If the friend is in a private server, this account must be
allowed into that server. If the friend is offline or in a different game, Roblox shows an error,
the load-in check times out, and the script tries again. Keeping the friend account in the server is
that account's job.

## What has been checked, and what has not

Measured on real Roblox windows:

- At 100% scale the game picture at Roblox's minimum is 800 x 599.
- At 125% scale on Dunder-MiffLan, 3840x2160, it is 1002 x 750. An early version of the script wanted
  980 x 701 there and correctly refused. That refusal is how the minimum-size rule was found.
- On Dunder-MiffLan the corrected script sized the window, and the in-game points sat within a
  button's width of their predicted spots. One of them, the loaded check, had slipped just off the
  orange it looks for, which would have looped the reconnect.
- All five points were then re-aimed by dragging tags over the real Roblox window, and the load-in
  sequence ran through at 125%, including the delayed hotbar press.

Checked by 66 automated tests against a stand-in window given Roblox's minimum sizes:

- Asking for an 800x600 window yields 816x638 with an 800 x 599 game picture, as Roblox does.
- The converted points hit the same screen pixels as the original points at 100%.
- The 125% Dunder-MiffLan case is accepted, and points scale by 1.25.
- A minimum far above the wanted size is refused rather than faked.
- A moved point is saved for its scale only, lands back on the dropped pixel, shows its original
  in the list, and restores. A drop outside the game picture is rejected.
- The friend box accepts IDs and profile links, and each rejoin mode launches the right link.

Not checked yet:

1. **Whether a friend join really avoids the captcha.** It is a different request, and it matches
   what was seen by hand, but it has not been proven on a locked account.
2. **Scales of 150% and above.** The minimum sizes there are predictions.
3. **The disconnect dialog at 125%.** It is Roblox's own UI, so it should scale evenly, but no real
   disconnect has been watched at that scale yet.

## Limits that no amount of math fixes

These numbers are tied to the game's current UI. If the game moves a button, every macro breaks the
same way, and Click points is the quick repair. Fullscreen is not handled: take Roblox out of
fullscreen first. And the script still drives one Roblox window, the first one it finds.
