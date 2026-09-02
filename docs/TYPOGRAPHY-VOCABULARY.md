# The type vocabulary (`remex.desktop`)

Every piece of text in the PC app picks its size and weight from Material's type scale, addressed by
the **`Theme`** the control carries — not by an inline `FontSize`. Guarded by
`remex.desktop.tests/Views/TypographyVocabularyTests.cs`.

Filed as **RemEx-9iz00.2**, under the Phase 3 design-system epic (RemEx-9iz00). Sibling of
[the button vocabulary](BUTTON-VOCABULARY.md), and deliberately the same shape: name the roles in one
bead, apply them in the per-screen beads.

## Why this exists

Measured 2026-08-31: **518 inline `FontSize` values** across `remex.desktop`, in **twenty** distinct
sizes.

| Size | Count | | Size | Count | | Size | Count |
|---:|---:|---|---:|---:|---|---:|---:|
| 8 | 1 | | 15 | 12 | | 24 | 1 |
| 9 | 14 | | 16 | 12 | | 26 | 2 |
| 10 | 34 | | 17 | 2 | | 28 | 1 |
| **11** | **108** | | 18 | 11 | | 30 | 9 |
| **12** | **128** | | 20 | 9 | | 48 | 18 |
| **13** | **104** | | 21 | 2 | | 280 | 1 |
| 14 | 28 | | 22 | 19 | | | |

**340 of the 518 — two thirds — sit inside the three-point band 11/12/13.** That is not a type scale.
Three sizes one point apart do not read as three different things; they read as three authors, on
three screens, each picking a number that looked right that afternoon. The same failure the button
vocabulary was filed for, in a different property.

It is also *growing*. The survey on this bead measured 435 sized `TextBlock`s on 2026-08-27. Four days
later it is 518. Every new view adds more, because there is no rule to follow.

## The scale

Material.Avalonia 3.19.0 ships the Material Design type scale as **`ControlTheme`s**, not as classes.
Verified against
[`Material.Styles/Resources/Themes/TextBlock.axaml`](https://github.com/AvaloniaCommunity/Material.Avalonia/blob/master/Material.Styles/Resources/Themes/TextBlock.axaml),
not assumed:

| `Theme` key | Size | Weight | Use it for |
|---|---:|---|---|
| `OverlineTextBlock` | 10 | Regular | All-caps section labels above a group |
| `CaptionTextBlock` | 12 | Regular | Secondary and supporting text, metadata, hints |
| `Body2TextBlock` | 14 | Regular | Default body text |
| `Body1TextBlock` | 16 | Regular | Emphasised body, first paragraph of a page |
| `Subtitle2TextBlock` | 14 | Medium | Row and list-item titles |
| `Subtitle1TextBlock` | 16 | Regular | Card and section headers |
| `Headline6TextBlock` | 20 | Medium | Sub-headings within a page |
| `Headline5TextBlock` | 24 | Regular | Page titles |
| `Headline4TextBlock` | 34 | Regular | Hero text |
| `Headline3TextBlock` | 48 | Regular | Splash / empty-state display |
| `Headline2TextBlock` | 60 | Light | — |
| `Headline1TextBlock` | 96 | Light | — |
| `LinkTextBlock` | — | — | Inline hyperlinks |

Applied as `<TextBlock Theme="{StaticResource CaptionTextBlock}" .../>`.

## The mapping

What today's inline sizes become. **The 11/12/13 band collapses to one step**, and that is the point:

| Today | Becomes | Note |
|---|---|---|
| 8, 9, 10 | `OverlineTextBlock` (10) | 9 and 10 are already this role |
| **11, 12, 13** | **`CaptionTextBlock`** (12) | 340 sites. 11 grows a point, 13 loses one |
| 14, 15 | `Body2TextBlock` (14) | |
| 16, 17 | `Body1TextBlock` / `Subtitle1TextBlock` (16) | Subtitle if it titles something, Body if it reads as prose |
| 18, 20, 21, 22 | `Headline6TextBlock` (20) | |
| 24, 26, 28, 30 | `Headline5TextBlock` (24) | |
| 48 | `Headline3TextBlock` (48) | Exact match already |
| 280 | *keep inline* | The splash glyph. Not type, artwork — see exceptions |

RemEx's three surviving classes keep their jobs for now and are swept per view:

| Class | Uses | Maps to | Keeps |
|---|---:|---|---|
| `page-title` | 8 | `Headline5TextBlock` | `PageTitleFontFamily`, accent foreground, `LetterSpacing=2` |
| `page-subtitle` | 5 | `Subtitle2TextBlock` | `PageTitleFontFamily`, muted foreground, `LetterSpacing=4` |
| `card-title` | 1 | `Subtitle1TextBlock` | Primary-text foreground |

They are **not** deleted by this bead. They carry font-family, colour and letter-spacing that the
Material theme does not, so retiring them is a per-screen judgement, not a find-and-replace.

`TextBlock.h1`, `.h2` and `.caption` **were** deleted here — all three had zero usages. `.caption`
additionally collided by name with Material's own `Compatibility/TextBlockClasses.axaml`, which
defines `:is(Control).caption`; see the comment block in `App.axaml`.

## What the theme does not own

`Theme` sets **size and weight**. It does not set colour, family, or spacing, and it must not be made
to:

- **Colour** stays on the brush tokens (`TextPrimaryBrush`, `TextSecondaryBrush`, `TextMutedBrush`),
  which the palette engine drives.
- **Family** is user-chosen and live. `CustomizationViewModel.SelectedPageTitleFont` /
  `SelectedBodyFont` feed `ThemeService`, which mirrors them into `PageTitleFontFamily`,
  `BodyFontFamily` **and** `MaterialDesignFonts` — the last is what reaches tooltips, flyouts and
  context menus (see the RemEx-prkot comment block in `App.axaml`). **A type-scale change must never
  take a hard-coded `FontFamily` with it**, or both pickers silently stop working on that element.
- **Letter spacing** is decorative and stays where it is.

## Documented exceptions — inline `FontSize` that stays

1. **The splash glyph** (`FontSize="280"`). Artwork sized to the canvas, not text on a scale.
2. **Badge and chip counters** already sized by their own control theme's setter.
3. **Anything inside a `ControlTheme`** in `Themes/`, which is defining a component, not consuming
   the vocabulary.
4. **Controls that are not a `TextBlock`** (`TextBox`, `SelectableTextBlock`). The Themes above
   target `TextBlock`; applied to a subclass they replace that control's own theme, and a
   `SelectableTextBlock` on `Headline6TextBlock` loses its `SelectionBrush` so drag-select paints
   nothing (RemEx-enbqf review). Those keep an inline size until they get a theme of their own.

Add to this list rather than quietly re-introducing a number.

## Still to do

This bead defines the vocabulary and stops. **The 518 call sites are swept in the Phase 4 view beads**
(RemEx-1ufoa), one screen at a time — deliberately, because collapsing 11/12/13 changes text density
on every screen and there is no headless render to check it against (RemEx-0e9eq). Per-view, each
sweep is judged on that screen with the `ui-verify` skill's hot-reload loop.

`TypographyVocabularyTests` is a **ratchet**, not a gate: it pins the current count and fails if it
grows. It cannot be satisfied by adding a number, only by removing one.
