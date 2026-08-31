# The button vocabulary (`remex.desktop`)

Every button in the PC app picks its look from this list. The classes are declared once in
[`remex.desktop/App.axaml`](../remex.desktop/App.axaml) and guarded by
`remex.desktop.tests/Views/ButtonVocabularyTests.cs`.

Filed as **RemEx-z7pnx**, under the Phase 3 design-system epic (RemEx-9iz00).

## Why this exists

Before it, 37 button classes were spread across 16 files. They were named after the **screen** they
happened to live on — `rd-btn`, `log-btn`, `about-back`, `crumb-btn`, `stage-mini-btn` — so a name
told you where a button was, never what it meant. The consequences were the ordinary ones:

- Nine corner radii (2, 6, 8, 10, 12, 14, 16, 24, and "the card radius") for buttons doing the
  same job on different screens.
- Five spellings of "this action is destructive". Three of them used `SystemErrorBrush` as the
  foreground over a 15% red wash, which
  [`AccentForegroundContrastTests`](../remex.desktop.tests/Services/AccentForegroundContrastTests.cs)
  measures at **1.24:1** — the other two used the white token, which measures 15.96:1.
- Three pressed-feedback scales — `0.96`, `0.97`, `0.98` — which is not a design decision, it is
  three authors not knowing about each other.
- The accent colour swatch declared twice, in two files, at two sizes, with the same hover bug
  (RemEx-fy0a) fixed in each of them separately, months apart.

A new button had no rule to follow, so every one of them was a fresh judgement call.

## The system

Pick **one emphasis**. Add a **tint** if the action carries a consequence. Add **modifiers** if the
layout needs them. That is all of it.

### Emphasis — exactly one per button

| Class | Means | Looks like |
|---|---|---|
| `primary` | The one thing this surface exists to do | Accent fill, accent foreground, elevation 1 |
| `secondary` | An action of equal standing, or the normal emphasis where there is no single primary | Card surface, card border, body text, flat |
| `tertiary` | Inline, toolbar and dismiss actions: present, not competing | No fill, no border until hovered |

At most **one** `primary` per surface. It is the only emphasis that carries elevation, and that is
what makes it read as the primary at a glance rather than by being bigger.

### Tint — optional, combines with any emphasis

| Class | Means |
|---|---|
| `danger` | Destructive or irreversible |
| `success` | Confirms or starts something |
| `warning` | Proceed knowingly |

A tint says the action has a **consequence**. It never changes how loud the button is, only what it
means. How it paints depends on the emphasis it is combined with, because the emphasis decides what
surface there is to tint:

- `secondary` + tint → the 15% wash, white/coloured label. This is the common case.
- `primary` + tint → a **solid** fill in the tint colour, dark label. This is the
  destructive-confirm shape: one loud button whose colour is the warning.
- `tertiary` + tint → stays transparent; only the label is recoloured.

`primary danger` and `secondary danger` are genuinely different buttons, not two intensities of one.
The first asks "are you sure?", the second is a delete button sitting in a row of other buttons.

### Modifiers

| Class | Means |
|---|---|
| `compact` | Denser padding and a smaller label. Toolbars, breadcrumbs, chips. |
| `pill` | Fully rounded. Filter and quick-action rows. |
| `icon-button` | A square hit target with a glyph and no label. Write it **alongside** `tertiary`, not instead of it — the class owns the size, not the paint. |

### Standalone roles

These replace an emphasis rather than modifying one, because they are surfaces that happen to be
clickable rather than buttons that happen to be big.

| Class | Means |
|---|---|
| `tile` | A large, stretch-content target in a grid of its peers — theme presets, the tray's device tiles, Remote's command grid. Brightens on hover; does **not** lift. Add `selected` for the chosen one in a single-choice grid. |
| `card` | A card that is a click target. Lifts off the page on hover (see RemEx-qbzl1). Pair with `interactive`. |
| `swatch` | A round colour chip whose **fill is the value it represents**, so the class sets everything except `Background`. |

`tile` and `card` differ on purpose: a tile sits flat among its peers, a card rises out of the page.
Using the lift for a grid of tiles makes the whole grid twitch as the pointer crosses it.

## What the class does not own

**Height, width, margin and grid placement stay at the call site.** A tile's height is a property of
the grid it lives in, not of what a tile is — pinning it in the class is exactly what forced three
near-identical copies of the tile style to exist. If two screens need the same size, that is a
coincidence until it is a token.

## Where the look actually comes from

These classes sit **on top of Material's `Button` control template**, they do not replace it. The
ripple, the hover overlay (`PART_HoverEffect`), the focus overlay and the disabled treatment all
come from `MaterialButtonBase` in Material.Avalonia 3.19.0. What RemEx supplies is the palette and
the geometry.

`ShadowAssist.ShadowDepth` **is** used for buttons, unlike for `Card`. A button's shadow is a plain
drop shadow with no accent glow riding on it, so Material's fixed ramp is exactly right, and the
local-value problem that ruled `ShadowAssist` out for cards (RemEx-qbzl1) never arises.

Note that Material.Avalonia 3.19 exposes its variants as **ControlThemes**
(`MaterialOutlineButton`, `MaterialFlatButton`, `MaterialIconButton`), not as the
Flat/Outline/Raised/Rounded/Light **classes** that the 2.x-era docs describe. RemEx-z7pnx's original
description assumed the older API; it also assumed `ButtonAssist` carries a corner radius, which in
3.19 it does not — `ButtonAssist` has exactly `HoverColor` and `ClickFeedbackColor`.

## `:is(Button)`, never bare `Button`

Every selector here is written `:is(Button).tertiary`, not `Button.tertiary`. A bare type selector
in Avalonia matches the **exact** type, so `Button.tertiary` reaches a `Button` and silently skips
`DropDownButton`, `SplitButton`, `RepeatButton` and `ToggleButton`. RemEx-z7pnx shipped exactly
that miss for one release of the epic: AppLauncherView's sort control is a `DropDownButton`, it
was given `Classes="tertiary"`, the build was green, the class was spelled correctly — and the
control rendered completely unstyled. There is no warning for a class that matches nothing.

`SelectionControlTests.TheVocabularySelectorsUseIsButtonSoTheyReachButtonsSubclasses` fails the
build if a bare `Button.x` comes back.

### Toggles

A `ToggleButton` wearing a vocabulary class is a button that remembers: `:checked` paints the
"this one is on" state rather than being a fourth emphasis.

**Keep the `:checked` on the selector.** Avalonia's frame order puts an app style *with* an
activator above one without, and a control theme's own activated style in between — so a plain
`Background` setter in the `.tertiary` block loses to the theme's accent-derived checked fill,
while one carrying `:checked` wins. Moving those setters "up" into the base block is the tidy that
reintroduces RemEx-xpfls: the tray's pin button rendering as a blank accent square in its pinned
state, with no way to tell which mode the window was in.

## Selection controls

`ToggleSwitch`, `CheckBox` and `RadioButton` keep Material's templates — ripple, knob transition,
check glyph — and take RemEx's palette through the assist properties those templates read
(RemEx-x3vom). Set them in `App.axaml`, not per view.

One of those defaults was a real defect rather than a mismatch: `MaterialToggleSwitch` sets
`SwitchTrackOffBackground` to the **literal** `Black`, drawn at 0.26 opacity. Over RemEx's dark
glass that is very nearly the surface itself, so an OFF switch read as an empty gap where a control
should be — on three of the four themes, with nothing thrown and nothing logged.

`ToggleButtonAssist`, which the bead's description named, is not read by any shipped theme in
Material.Avalonia 3.19.0; `MaterialFlatToggleButton` does not reference it. The `:checked` rule
above does that job instead.

The keyboard focus ring for all three is `App.axaml`'s `:focus-visible` styles setting
`BorderBrush`/`BorderThickness` on the control. That works because each Material template roots on
a Border that template-binds both — and it is **load-bearing**, because Material nulls
`FocusAdorner`. Delete those styles and keyboard traversal has no visible indication at all.

## Documented exceptions

Two groups of buttons keep bespoke styles, each because another bead owns them:

| Style | Where | Owned by |
|---|---|---|
| `nav-item` | `ShellView.axaml` | **RemEx-zi3ua** — retained for the drawer-toggle brand button; the nine nav destinations moved to `ListBoxItem` |
| Window chrome buttons | `Themes/Chrome/WindowChrome.axaml` | Template parts of the window chrome, not app buttons |

`gear-fab` (`ShellView.axaml`) was on this list until **RemEx-bado6**: the gear is a Material
`FloatingButton` now, not a `Button`, so the guard tests it used to except from can no longer even
see it.

The tray flyout's chips were on this list until RemEx-x3vom; they are
`tertiary icon-button compact` now, with their `:checked` state part of the toggle rule below.

Adding to this table without a bead that owns the exception is how the old sprawl came back last
time. The guard test asserts the list, so widening it is a deliberate edit rather than a drift.

## Still to do

**66 buttons carry no class at all** and therefore render as Material's default raised primary.
Most of them are not primary actions. They need a per-screen judgement about which role they play,
which is what the Phase 4 view-migration beads (RemEx-1ufoa) are for — a sweep that guesses would
be worse than the state it replaced. Tracked as **RemEx-z7pnx.1**.
