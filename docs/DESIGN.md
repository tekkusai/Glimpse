# Glimpse design

This is the rulebook for how Glimpse looks, moves and speaks. Code that draws
anything the user sees follows it. When a rule here and the code disagree, the
code is wrong or this file needs a deliberate edit, not a quiet exception.

## Principles

1. **Content first.** The window exists to show a file. Chrome stays quiet,
   the canvas stays neutral, and the file's own colors are the only loud thing
   on screen.
2. **The first frame is already the file.** The window opens in the content's
   shape. If the content is not ready, the best image of it we have stands in:
   a poster, or a ghost in its exact shape. Never an empty void with small text
   in the middle.
3. **One motion.** Every preview arrives with the same dissolve and leaves
   with the same fade, whatever renders it.
4. **One system.** WPF surfaces and web pages share the same tokens, type,
   scrollbars and selection color. A `.txt` next to a `.cs` must not look like
   two apps.
5. **Honest words.** Copy says what is true right now, in plain sentence case.
   No promises the next file will break.

## Color

Neutral grays (R = G = B) with one hue, the accent. The content area is the
deepest surface in dark theme and the brightest in light theme, so content
always sits on the calmest tone.

| Token          | Dark        | Light       | Used for |
|----------------|-------------|-------------|----------|
| `ChromeBg`     | `#202020`   | `#F3F3F3`   | Header, menus, tooltips, dialogs |
| `CanvasBg`     | `#191919`   | `#FAFAFA`   | Everything under the header: content, letterbox, scrim, loading, cards, text pages |
| `RaisedBg`     | `#2C2C2C`   | `#FFFFFF`   | Pills, controls, tiles, code blocks, ghost pages |
| `RaisedBorder` | `#3A3A3A`   | `#DCDCDC`   | 1 px border of raised things |
| `Border`       | `#2E2E2E`   | `#E3E3E3`   | Hairline dividers, the window edge |
| `HoverBg`      | white 6 %   | black 4 %   | Hover on any surface |
| `PressedBg`    | white 4 %   | black 6 %   | Pressed on any surface |
| `TextPrimary`  | `#F2F2F2`   | `#1A1A1A`   | Names, titles, body |
| `TextSecondary`| `#B4B4B4`   | `#5C5C5C`   | Details, labels |
| `TextTertiary` | `#8C8C8C`   | `#707070`   | Captions, counts, hints |
| `Accent`       | `#4E80F5`   | `#3B72E8`   | Selection, focus, on-state, progress |
| `Warning`      | `#D9A66A`   | `#9A6B24`   | Warnings only |
| `ScrollThumb`  | `#5A5A5A`   | `#B8B8B8`   | Scrollbar thumbs, WPF and web |

Floating HUDs over content (zoom readout, mesh overlay) use the fixed dark
glass in `Theme.Overlay*` in both themes.

Video always plays on black. Everything else follows the theme, including the
audio player, spreadsheets and the PDF viewer's toolbar (WebView2's
`PreferredColorScheme` follows the app theme).

## Type

Segoe UI Variable for words, Cascadia Mono for code and plain text.

| Size   | Weight   | Role |
|--------|----------|------|
| 14     | SemiBold | Header file name |
| 11.5   | Regular  | Header details, captions, tooltips |
| 12.5   | SemiBold | Pill and button labels |
| 13     | Regular  | Card body, info lines, menus |
| 20     | SemiBold | Card titles |
| 34     | SemiBold | Welcome title only |
| 13 mono| Regular  | Code and plain text, line height 1.6 |

Nothing else. A size not in this table is a bug.

Two things are not chrome type and keep their own sizes: documents Glimpse
sets for reading (markdown and EPUB use a reading scale, 15 px body with
28, 21 and 17 px headings), and icon glyphs, which are sized to the button
or tile they sit in.

## Shape

| Radius | Used for |
|--------|----------|
| 6      | Icon buttons, menu items, list rows |
| 8      | Menus, flyouts, thumbnails, HUDs, sheets in the rail |
| 12     | Card tiles, specimens, raised panels |
| capsule| Pills (height 30, radius 15) |

The window uses the system's rounded corners and shadow (DWM). Dialogs use the
same window chrome as the preview, not a drawn card with a fake shadow.

Spacing follows a 4 px grid. The header is 52 px with 12 px side padding.
Header controls are 30 px tall: icon buttons 30 x 30, pills 30 high.

## Header

Left to right, in priority order when space runs out:

1. The file name always keeps at least 160 px.
2. Details (`PDF · 5.7 MB · 81 pages`) trim first.
3. Tools appear only for kinds that have tools (rotate for images, Sign for
   PDFs, zoom for spreadsheets). No reserved empty slot.
4. **Open** is one pill: the default app's 16 px icon, the word Open and a
   chevron. It never truncates and never changes width between files. The
   tooltip names the app. The chevron menu holds "Open with…", Show in
   Explorer, Share and the kind's extra actions.
5. Pin, fill screen and close are icon buttons.

Browse arrows sit before the identity when browsing is possible, with the
position (`2 of 14`) beside them when a multi-selection is open.

Tools and the Open pill change only when the file kind changes, so arrowing
through a folder never makes the header blink.

## Motion

All motion goes through one switch that honors the Windows "Show animations"
setting. With animations off, every transition snaps to its end state and
loading states are still.

| Moment | Motion |
|--------|--------|
| Open | The window waits up to 120 ms for its content, then fades in over 140 ms with whatever is ready. No scale. |
| Reveal | Content dissolves in over 160 ms, cubic ease-out, from the canvas or from its stand-in. |
| Browse, same shape | The next item dissolves over the current one in 140 ms. No blank beat. |
| Browse, new shape | The current item fades out in 90 ms, the window takes the new shape in one step, the next item dissolves in. |
| Close | 90 ms fade, ease-in. |

Web content dissolves inside the page (its background is transparent, so the
canvas or stand-in shows through). The PDF viewer is opaque and appears in one
cut once it has painted at its final size.

## Waiting

| Elapsed | What the user sees |
|---------|--------------------|
| 0 to 120 ms | Nothing new. Fast files arrive here in one motion with the window. |
| 120 ms to 1 s | The stand-in: a poster (blurred-up thumbnail of page one or the image) or a ghost page in the content's exact shape, still. |
| after 1 s | A slow sheen moves across the stand-in. |
| after 1.2 s, when there is a reason | One line under the stand-in, e.g. "Converting with LibreOffice". |

Posters come from, in order: the PDF rail cache's first page, the Office file's
embedded thumbnail, Explorer's cached thumbnail. A ghost page is `RaisedBg`
with a `RaisedBorder` hairline at the page's aspect, placed where the real page
will land.

## Cards

One card layout for every file that has no richer view: the visual centered at
38 % of the canvas height, a 20 px title under it, then at most two lines of
facts the header does not already show, then an optional footer. Nothing moves
between kinds. Errors use the same card with a warning glyph instead of the
file visual, one sentence and at most one action.

## Words

- Sentence case. Straight quotes. No em dashes.
- Say what happened and what to do: "Can't preview this file. Open it in
  Photos instead."
- Use `Branding.Name`, never a hard-coded app name.
- Never show an exception message, a byte offset or an engine name the user
  did not choose.

## Web pages

Pages Glimpse writes (code, markdown, text, media player, image viewer,
spreadsheets, EPUB) take their colors from one CSS block generated from the
current palette. They style scrollbars to match the WPF ones (no arrow buttons,
rounded thumb in `ScrollThumb`), use the accent for `::selection`, and start
transparent so they dissolve in over the canvas.

## Zoom and pan

One gesture set for every zoomable view:

- Fit never upscales: small images show at 1:1 device pixels, centered.
- Wheel zooms around the cursor by 1.15 per notch.
- Double-click toggles fit and 1:1 device pixels (or 2x fit when fit is
  already 1:1 or larger).
- Ctrl+Plus, Ctrl+Minus and Ctrl+0 work whenever the view is zoomable.
- Pan is clamped to the content's edges.
- Images with transparency sit on a subtle two-tone checkerboard.
