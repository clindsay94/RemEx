# What actually breaks when Fluent is removed

`RemEx-3e65x`, for `RemEx-prkot` ("Wire MaterialTheme into App.axaml and decide Fluent's fate").

The bead asked which controls we currently get from `Avalonia.Themes.Fluent` that Material.Avalonia
may not template identically, and for each: does Material template it, does it fall back to the
built-in Simple theme, or does it lose its look entirely?

**The headline is that the question the bead asked turns out to be nearly a non-issue, and the real
risk is somewhere else.** Material.Avalonia templates every stock control Fluent does, bar one we do
not use — nothing loses its look. What bites instead is the set of *inherited* properties the two
themes set differently on `Window` and on popup roots: font, foreground and background, across
surfaces no per-control audit would have looked at. §3 is the section that matters.

Evidence: Material.Avalonia at tag `v3.14.2` (the version pinned by `RemEx-851li`) against Avalonia
at tag `11.3.11`, both read from source rather than inferred. Tags, not commit SHAs — the tags are
what was actually verified, and citing a SHA nobody checked is the same error this audit already
made once about a file nobody opened.

---

## 1. The answer to the bead's actual question

Fluent ships 69 files under `src/Avalonia.Themes.Fluent/Controls/`; two of those
(`DateTimePickerShared`, `FluentControls`) are shared resource files rather than control themes.
Material ships 70 under `Material.Styles/Resources/Themes/`.

**Templated by Fluent and NOT by Material — the entire set:**

| Control | Used in RemEx? | Verdict |
|---|---|---|
| `CalendarDatePicker` | **No** — zero occurrences in any `.axaml` | Nothing to lose |

That is the whole list. Every other stock control Fluent templates, Material templates too.

**Material adds five themes Fluent does not have:** `ContentControl`, `TextBlock`, `FontFamily`,
`NativeMenuBar`, and — outside `Resources/Themes/` and therefore easy to miss —
`Material.Styles/UserControl.axaml`, a `<Style Selector=":is(UserControl)">` whose only setter is
`Theme={StaticResource MaterialUserControl}`. Fluent has no `UserControl` entry at all.
**RemEx has 15 `UserControl`-rooted views and none of them set `Theme`**, so all 15 have their
template replaced. The replacement is a bare `PART_ContentPresenter` that template-binds `Background`,
`BorderBrush`, `BorderThickness`, `CornerRadius`, `Content`, `ContentTemplate`, `Padding` and both
content alignments — every property a view could set on its own root. So this is very likely benign,
and here is what would falsify that: **a view relying on a root-level property the bare presenter
does not bind.** None of the 15 does today. Check that before assuming, rather than re-deriving the
whole question. An audit enumerating what changes should not omit a style reaching every view in the
app, even a benign one.

`TextBlock` sounds alarming and is not: `MaterialTextBlock` is an **empty** `ControlTheme` and the
implicit `{x:Type TextBlock}` is `BasedOn` it, setting nothing. The named variants
(`Body1TextBlock`, `CaptionTextBlock`, …) are opt-in by key. So typography does not change through
that route — it changes through §3.

**There is no automatic fallback to the Simple theme.** `Avalonia.Themes.Simple` is referenced in
neither `Directory.Packages.props` nor `remex.desktop.csproj`. A control with no `ControlTheme` in
any included style renders as *nothing*; it does not degrade gracefully. That is what makes the
"loses its look entirely" column a real question rather than a cosmetic one — and why it matters
that the column turned out to be empty.

### The bead's specific worries, resolved

| Control | Where we use it | Material templates it? | Verdict |
|---|---|---|---|
| `NotificationCard` | styled directly in `App.axaml` | **Yes** — `Themes/NotificationCard.axaml` | Safe. Our style sets only `Background`, `BorderBrush`, `BorderThickness`, `Foreground`; all four survive a template swap because they are control properties, not template internals. |
| `NumericUpDown` | `SetAlertDialog.axaml` ×1 | **Yes** — plus `ButtonSpinner.axaml`, which it composes | Safe |
| `UniformGrid` | 5, incl. `RemoteView`, `HomeView`, `TrayFlyoutWindow` | **Neither theme templates it** | Safe, and the worry was misplaced. `UniformGrid : Panel : Control` never derives from `TemplatedControl`, so it has no `Template` property for a `ControlTheme` to fill — nothing for Fluent's removal to take away. The rule is *not* "panels are never themed": `AdornerLayer` is a `Panel` and **does** have a `ControlTheme` in both themes. The base type is what carries the argument, not the fact that it is a panel. |
| `SelectableTextBlock` | 4 — `FileTransferView`, `RemoteView`, `AboutView`, `PairingPinPanelView` | **Yes** — `Themes/SelectableTextBlock.axaml` | Safe |
| `LayoutTransformControl` | `MainWindow.axaml` ×1 | **Neither theme templates it** | Safe, same reason — `LayoutTransformControl : Decorator : Control`, also never a `TemplatedControl`. |
| `ToggleSwitch` | 9 — 8 in `SettingsView`, 1 in `PersonalizationPanelView` | **Yes** | Safe as a template; see §2 for its focus ring |
| `Separator` | 5 — `PersonalizationPanelView`, `ShellView`, `CanvasView` | **Yes** — plus `Compatibility/SeparatorClasses.axaml` | Safe |
| `MenuItem` | 30 — 23 in `CanvasView`, 4 in `TrayFlyoutWindow`, 3 elsewhere | **Yes** | Safe as a template. Note Material's template names its parts `PART_Icon` and `PART_HeaderPresenter`; any future style reaching into it must use those. |
| tray `Window` chrome | 11 window-rooted files | **Yes** — `Window.axaml`, `CaptionButtons.xaml`, `TitleBar.xaml` | Templated, but see §3 — this is where the font problem enters |
| the 10 `:focus-visible` selectors | `App.axaml` | Partly | **One breaks.** See §2. |

Nothing in this table lands in the "loses its look entirely" column, so no follow-up beads are owed
for it. The two findings below are owed instead.

---

## 2. FINDING — one focus ring breaks silently, and Material removes the fallback

Of our ten `:focus-visible` styles in `App.axaml`, four reach into a template and six set properties
on the control itself. All ten set the same thing: `BorderBrush={DynamicResource AccentPrimaryBrush}`
and `BorderThickness=2`.

**The six control-level ones survive.** All ten Material templates involved template-bind both
`BorderBrush` and `BorderThickness`, so a value set on the control still renders. Verified per file.

**Three of the four template-level ones survive.** `Button`, `ToggleButton` and `RepeatButton` all
have a `ContentPresenter` named `PART_ContentPresenter` in their Material template, and the selector
`/template/ ContentPresenter` matches on type, not name.

**`ListBoxItem:focus-visible /template/ ContentPresenter` matches nothing.** Material's `ListBoxItem`
template contains no `ContentPresenter` at all — it hosts content in a
`ripple:RippleEffect` instead, with parts named `PART_RootBorder`, `PART_RootPanel`,
`PART_BehaviourEffect`, `PART_Ripple`, `PART_HoverEffect`. The selector will compile, match nothing,
and the keyboard focus ring on every list item disappears with no error anywhere.

**And the default focus adorner is gone too.** Material sets `FocusAdorner="{x:Null}"` on 9 of the 13
controls checked — `Button`, `ToggleButton`, `ListBoxItem`, `ComboBox`, `CheckBox`, `RadioButton`,
`ToggleSwitch`, `TextBox`, `MenuItem`. So there is no built-in ring to fall back on when one of our
styles stops matching. `RepeatButton`, `TabItem` and `TreeViewItem` carry no `:focus` styling of
their own at all.

The fix for the one break is small: bind the focus ring to the control rather than the template part,
the way the other six already do, since `ListBoxItem`'s template does template-bind both border
properties. Appended to `RemEx-kgs7g`, which owns keeping these styles intact — no new bead, because
that one already exists to do exactly this and a second would split the work.

**One behaviour change, not a break:** Material's own `TextBox` (15 occurrences) and `ComboBox` (8)
themes style **`:focus-within`** — neither plain `:focus` nor `:focus-visible` appears in either file
— and set `BorderBrush`/`BorderThickness` themselves. Ours are `:focus-visible`, keyboard only.
`:focus-within` also activates when a *descendant* holds focus, which is a wider net still. After the
swap, clicking into a text box will show a Material focus border where today it shows none. Worth a
look during the visual pass rather than a bead.

One caveat on the six that survive: "still renders" is not "renders where it did". Material's
`TextBox` border is an underline (`PART_BackgroundTextField`), so a control-level `BorderThickness=2`
may draw a full rectangle where today there is a ring. Our styles carry an activator
(`:focus-visible`) so they apply at `StyleTrigger` priority and outrank Material's own `ControlTheme`
setters — they will win; the question is only what winning looks like.

---

## 3. FINDING — four inherited properties change on every window, and only one of them is the font

This is the one that matters, and the bead did not ask about it.

**Both themes set the same four properties on every `Window`.** An earlier draft of this section said
"Fluent sets neither" of the font properties — an assertion about a file I had not opened. Review
caught it. Both files, read at their tags:

| Property | Fluent `Controls/Window.xaml` @ 11.3.11 | Material `Resources/Themes/Window.axaml` @ 3.14.2 | Delta |
|---|---|---|---|
| `FontFamily` | `ContentControlThemeFontFamily` → `fonts:Inter#Inter, $Default` | `MaterialDesignFonts` → `Roboto, OpenMoji, $Default` | **Inter → Roboto** |
| `FontSize` | `ControlContentThemeFontSize` → `14` | `MaterialDesignFontSize` → `14` | **none — 14 → 14** |
| `Background` | `SystemRegionBrush` | `MaterialPaperBrush` | Fluent palette → Material palette |
| `Foreground` | `SystemControlForegroundBaseHighBrush` | `MaterialBodyBrush` | Fluent palette → Material palette |

RemEx overrides neither Fluent key (`ContentControlThemeFontFamily`, `ControlContentThemeFontSize`:
zero hits in `remex.desktop`). All four inherit down the entire visual tree.

**So the unpinned windows are not unstyled orphans today** — they render Inter at 14, and
`BodyFontFamily` is `avares://Avalonia.Fonts.Inter/Assets#Inter`, the same family `MainWindow` pins.
Everything matches right now. The regression is therefore not "these windows lose the app font"; it
is **`MainWindow` stays Inter while the rest become Roboto** — a new inconsistency *inside* the app.
That is slightly worse than a uniform change, which would at least look deliberate. And the
`FontSize` half of this finding is a no-op: 14 either way.

### What each window actually pins

Measured across the 11 window-rooted `.axaml` files:

| Window | `FontFamily` | `FontSize` | `Background` | `Foreground` |
|---|---|---|---|---|
| `MainWindow.axaml` | **pinned** | – | pinned | – |
| `ConfirmationDialog`, `RestorePromptWindow`, `AddProgramWindow`, `SecondMetricDialog`, `SetAlertDialog`, `TrayBalloonWindow`, `CommandPaletteWindow` | – | – | pinned | – |
| `FileConsentDialog`, `PairingDialog`, `TrayFlyoutWindow` | – | – | – | – |

A local value on an element outranks a `ControlTheme` setter in Avalonia's precedence — only
`Animation` outranks `LocalValue` — so a pinned property is genuinely safe. But **"MainWindow is
protected" holds only for `FontFamily` and `Background`.** No window pins `FontSize` (harmless, value
unchanged) and **no window pins `Foreground`**.

`Foreground` is the one to watch, and it is the half a font-only reading would miss. But the exposed
set is small, and naming it is better than gesturing at it: **about thirty Material control themes
set their own `Foreground` at `ControlTheme` level** — `Button`, `CheckBox`, `ComboBox`, `TextBox`,
`MenuItem`, `Slider`, `TreeView`, `ProgressBar` and the rest — and an explicit `ControlTheme` setter
beats inheritance, so none of those ever sees the inherited `MaterialBodyBrush`. Plain `TextBlock` is
covered by `App.axaml`'s `<Style Selector="TextBlock">` at `Style` priority, which outranks any
`ControlTheme` setter at `StyleTheme`.

What is actually exposed is **the four `SelectableTextBlock`s** — their Material theme sets
`SelectionBrush`, `Cursor` and `ContextFlyout` but no `Foreground` — **plus bare content-presenter
text**. That is the whole set. It takes `MaterialBodyBrush` from Material's palette rather than a
RemEx theme brush, across all four PC themes, and a foreground from one palette over a background
from another is how contrast failures happen — the SolarFlare case the guardrails call out.

`FileConsentDialog`, `PairingDialog` and `TrayFlyoutWindow` pin nothing at all and are the most
exposed; two of the three were redesigned recently.

### Windows are not the only surface — and this decides which fix is correct

Everything above is about `Window`. **Popups are a separate top-level with their own `ControlTheme`,
and a pin on a `Window` cannot reach them.** An explicit `ControlTheme` setter beats an inherited
value — inheritance is the floor, below every frame — so popup content takes Material's font
regardless of what the owning window says.

| Surface | Material sets | Fluent sets |
|---|---|---|
| `PopupRoot`, `OverlayPopupHost` | `FontFamily`, `FontSize`, `FontWeight=Normal`, `FontStyle=Normal` | the same four **plus `Foreground`** |
| `EmbeddableControlRoot` | `FontFamily`, `FontSize`, `Foreground=MaterialBodyBrush` | the same three, different values |
| `FlyoutPresenter` | `Foreground=MaterialPaperBrush` | *(nothing inherited)* |
| `ToolTip` | `Foreground=MaterialPaperBrush` | `Foreground=ToolTipForeground`, `FontSize=ToolTipContentThemeFontSize` |
| `TitleBar` | `Foreground=MaterialBodyBrush` | `Foreground=SystemControlForegroundBaseHighBrush` |

That reaches every `ContextMenu`, `MenuFlyout`, `ComboBox` dropdown, `ToolTip` and `Flyout` in the
app — including CanvasView's 23 context-menu `MenuItem`s and the tray flyout's 4.

**So the two candidate fixes are not equivalent, and only one of them works:**

- Pinning `FontFamily` on each `Window`, or an app-level `<Style Selector="Window">`, fixes
  **windows only**. Screenshot the ten dialogs afterwards and they look right, while every popup in
  the app is still Roboto. That is the same silent symptom this section exists to prevent, in a
  surface the fix appeared to cover.
- Overriding the **resource keys** — `MaterialDesignFonts` and `MaterialDesignFontSize` in
  `Application.Resources` — fixes both in one move, because both `ControlTheme`s resolve the same
  `DynamicResource`.

Take the second **for the font**. The first is the obvious one and it is a trap.

**But do not generalise (b) to the colour half — it inverts there.** The font keys are safe to
override precisely because they are single-role: across all of `Material.Styles`,
`MaterialDesignFonts` is only ever a `FontFamily` and `MaterialDesignFontSize` is only ever a
`FontSize`. The palette keys are not. Measured across the same tree, counting `ControlTheme`-level
`Background`/`Foreground` setters:

| Key | as `Background` | as `Foreground` |
|---|---|---|
| `MaterialPrimaryMidBrush` | 13 | 19 |
| `MaterialBodyBrush` | 7 | 21 |
| `MaterialSecondaryMidBrush` | 8 | 10 |
| `MaterialPrimaryLightBrush` | 9 | 7 |
| `MaterialPrimaryDarkBrush` | 8 | 8 |
| `MaterialPaperBrush` | 7 | 4 |

Six of them are dual-role. So overriding `MaterialPaperBrush` with a RemEx surface brush to fix the
`Window` background on `FileConsentDialog`, `PairingDialog` and `TrayFlyoutWindow` also lands on
`ToolTip` and `FlyoutPresenter` as a **foreground** — producing exactly the near-white-on-near-white
described below, this time *caused by the fix*. `MaterialBodyBrush` has the mirror problem.

The colour half therefore needs per-surface `Style` setters, or explicit `ToolTip`/`FlyoutPresenter`
foreground overrides — not a blanket key swap. One strategy for the font, a different one for the
palette.

One mechanical detail for (b): the override must match type — a `<FontFamily>` element and an
`<x:Double>`, not strings. `Application.TryGetResource` searches `Resources` before `Styles`, and a
`PopupRoot` is a `TopLevel` whose resource chain terminates at `Application`, which is why the same
override reaches both surfaces.

**`FlyoutPresenter` and `ToolTip` deserve their own look:** Material sets the *paper* brush as a
**foreground** on both, which is only correct against Material's own dark tooltip background. If
RemEx keeps its own flyout and tooltip backgrounds from theme resources while the foreground arrives
as `MaterialPaperBrush`, SolarFlare gives near-white text on a near-white surface. Same class as the
`Foreground` finding above, one level down.

None of this produces an error. It reads as "the redesign looks a bit off" rather than as a bug with
a cause. Filed as `RemEx-n0pb8` so `RemEx-prkot` has to decide it deliberately, covering the colour
half and the popup surface as well as the font: override the resource keys, or adopt Material's
palette on purpose and say so.

---

## 4. What `RemEx-prkot` should do with this

1. **Fluent can be dropped.** Not one control we use loses its template. The only gap,
   `CalendarDatePicker`, is unused. This matters more than it sounds, because §1 also establishes
   there is no Simple-theme safety net: anything that *had* fallen through would have rendered as
   nothing.
2. **Settle the four inherited `Window` properties before wiring `MaterialTheme` in** (`RemEx-n0pb8`).
   Font is only one of them and `FontSize` is a no-op; `Foreground` is unpinned on every window in
   the app and is the one with a contrast consequence.
3. **Expect the `ListBoxItem` focus ring to be dead the moment `MaterialTheme` goes in.** The fix
   belongs to `RemEx-kgs7g`, which sits downstream of `prkot` (via `mw0uh`) and cannot be pulled
   into the same commit without inverting that order. So `prkot` should not be called visually done
   until `kgs7g` has run — Material nulls `FocusAdorner` on nine controls, so there is nothing
   underneath in the meantime.
4. **Do not trust a green build.** Everything in §2 and §3 compiles perfectly and fails visually.
   The `ListBoxItem` selector in particular will match nothing and say nothing.

### What this audit did not check

Stated so nobody reads more into it than it earned. It is a source-level comparison of two theme
trees: it does not run the app, so it cannot see layout differences, spacing, control sizing, ripple
behaviour, animation, or how any of this looks under the four PC themes. Material's templates being
*present* is not the same as their looking right in CyberNOC or SolarFlare. It also covers
`Material.Styles` only — `Material.Avalonia.Dialogs` is a separate package with its own risk, tracked
on `RemEx-60x5f`.
