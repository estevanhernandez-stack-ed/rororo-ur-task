# Theme

Ur Task does not have a theme of its own. It wears whatever theme RoRoRo is wearing, and falls
back to the 626 Labs brand colours when no RoRoRo is answering. This file says where the colours
come from, where they land, and what this project does differently from the 626 Labs design
system.

The design system itself (tokens, type stack, components) lives in the `626labs-design` skill.
Load it before UI work.

## Source of truth

There are two, in this order.

1. **RoRoRo's active theme.** The host sends its resolved palette over the plugin contract:
   `GetTheme` once on connect, then `SubscribeThemeChanged` for every switch. Ur Task reads no
   RoRoRo settings or theme files and carries no copy of RoRoRo's built-in themes. It did once;
   the copy went stale the first time RoRoRo added a theme, and it was deleted.
2. **The fallback, `HostThemeReader.Brand`.** A window has to paint before any host answers, and
   Ur Task also runs with no host at all. The fallback is the 626 Labs brand: navy `#0F1F31`,
   cyan `#17D4FA`, magenta `#F22F89`. It is a starting colour, not a mirror. Do not grow it into
   one.

## Where the colours land

Eight brushes in `src/App.xaml`. Every XAML file refers to them by key.

| Brush | Used for |
| --- | --- |
| `BgBrush` | Window background |
| `RowBgBrush` | Rows and panels |
| `RowHoverBrush` | A row under the pointer. Not sent by the host: worked out as `RowBgBrush` tinted 4% toward `WhiteBrush`. |
| `DividerBrush` | Borders and rules |
| `WhiteBrush` | Primary text |
| `MutedTextBrush` | Secondary text |
| `CyanBrush` | Primary accent: the thing to click, the thing that is on |
| `MagentaBrush` | Second accent: recording, warnings, and anything that deletes |

The names are colour names for history's sake. Under another theme `CyanBrush` may not be cyan.
Read each one as its job in the table, and pick a brush by job.

`HostThemeService` applies a palette. Three things about it will bite you:

- **Use `{DynamicResource}`, never `{StaticResource}`, for a brush.** A theme switch replaces the
  brush in the application's resources. `StaticResource` keeps the old one and that control stays
  the old colour.
- **Replace the brush, do not change its colour.** Setting `Color` on the existing brush was
  tried and does not repaint: brushes loaded from compiled XAML can be frozen.
- **The values in `src/App.xaml` and `HostThemeReader.Brand` say the same thing twice.** Change
  one and change the other with it. Nothing checks that they match.

## Deviations from 626labs-design

- **The host's palette wins over the brand's.** By design: a plugin that ignores the launcher's
  theme looks broken next to it. The brand shows only in the fallback.
- **Seven colour slots, not the full token set.** That is what the contract sends. There are no
  separate tokens for success, warning or danger; state is carried by the two accents plus text.
- **System fonts.** Segoe UI Variable for display and body, Cascadia Code then Consolas for
  mono, where the design system uses Space Grotesk, Inter and JetBrains Mono. This is a WPF app
  shipped as a zip; bundling three font families is not worth the size. Fonts are the three keys
  `DisplayFont`, `BodyFont` and `MonoFont` in `src/App.xaml`.
- **One hardcoded colour.** The black drop shadow in `src/UI/RecorderWindow.xaml`. A shadow is
  black under every theme.
- **No web tokens.** This is XAML. Nothing here reads `colors_and_type.css`.

## Baseline that doesn't bend

- No hardcoded colours in XAML or code. Brushes by key only, apart from the shadow above.
- State is never shown by colour alone. Pair it with text, an icon or a shape. Themes are
  user-made, so two accents can end up close together, and some people cannot tell them apart
  anyway.
- Text has to stay readable on the fallback palette: 4.5:1 for body text, 3:1 for large text.
  Check new text against `BgBrush` and `RowBgBrush` both. Under a user's own theme the contrast
  is theirs to get right.
- The window works in both of its shapes: the full window and the compact strip.
- UI copy follows the 626 Labs voice: second person, sentence case, no emoji. The readers are
  non-technical Windows users.

The design system's rules for touch targets and 320 px layouts are for phones. They do not apply
to a desktop window.
