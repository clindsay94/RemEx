# The dashboard, as it is today

`HomeView.axaml` is the one surface with a stated no-regression requirement, and
`RemEx-oszfm` is about to rewrite it onto Material.Avalonia. This file is the definition of
"unchanged" that rewrite gets verified against, item by item — written **before** the rewrite, so it
describes what the dashboard does rather than what the rewrite left behind.

Filed for `RemEx-1qpjh`, against `remex.desktop/Views/HomeView.axaml` (471 lines) and
`remex.desktop/ViewModels/HomeViewModel.cs` (263 lines) at commit `abcc3f5`.

The mechanical half of this checklist is automated in
`remex.desktop.tests/Views/HomeViewCharacterisationTests.cs` — thirteen assertions covering the
command inventory, the row-to-card command routing, the button and indicator counts, the
quick-launch tiles, the stats strip, the SystemStatus states, colour literals, automation names and
the pinned footer. **Everything marked "by eye" below is the part a test cannot reach.** Read the
test as the enforceable subset of this document, not as a replacement for it.

---

## 0. The one fact that shapes everything else

**Every binding on this view is a reflection `{Binding}`. There is not a single `{CompiledBinding}`
in the file** — 66 bindings, zero compiled. Nothing here is checked at build time, and Avalonia
binding failures are silent, so a rewrite can drop a binding, mistype a path, or move a
`DataContext` and produce a screen that renders perfectly and does nothing.

This is not hypothetical on this view. Two buttons have already shipped dead:

- the SystemStatus **Fix** button, whose `$parent[ItemsControl]` cast was missing, so `Command` bound
  to null (`RemEx-tb0a`, `HomeView.axaml:185`)
- the same mistake again on **Explain** (`HomeView.axaml:197`)

Both looked completely normal. That is the failure mode this whole document exists to prevent.

If the rewrite converts these to `{CompiledBinding}`, that is a **strict improvement** — everything
here becomes build-time checked. The characterisation test already matches both spellings, so that
conversion needs no edit to it. Do not relax an assertion to make a conversion go green: two of the
command tests are satisfied by an empty set, which is why `CommandBindings()` asserts it still finds
twenty before anything else runs.

---

## 1. Structure

`Grid RowDefinitions="*,Auto"` — a scrolling body and a pinned footer.

| # | Section | Anchor |
|---|---|---|
| 1 | Welcome hero: watermark, BrandMark, title, intro, command-palette launcher, shortcut strip | `:76–121` |
| 2 | System status card (`RemEx-id37`) | `:124–207` |
| 3 | Always-on stats strip — CPU · memory · uptime | `:210–240` |
| 4 | Two-column `WrapPanel`: pinned sensors (left), quick launch (right) | `:242–359` |
| 5 | Recent activity feed | `:362–396` |
| 6 | External links: GitHub, Play Store, HWiNFO | `:399–433` |
| 7 | Footer status node — radar, presence text, link controls | `:439–469` (row 1, outside the ScrollViewer) |

Section 7 is **not** in the ScrollViewer. It is always visible. A rewrite that folds it into the
scrolling body is a behaviour change even though it looks like a layout change.

---

## 2. Commands — all 20 buttons

Eighteen distinct commands behind twenty buttons; `NavigateToCanvasCommand` is bound three times.

| Button | Command | Owner |
|---|---|---|
| Command palette launcher `:89` | `Shell.OpenCommandPaletteCommand` | `ShellViewModel` |
| SystemStatus recheck `:130` | `SystemStatus.RefreshCommand` | `SystemStatusViewModel` |
| Row **Fix** `:182` | `FixCommand` via `$parent[ItemsControl]` cast | `SystemStatusViewModel` |
| Row **Explain** `:195` | `ExplainCommand` via the same cast | `SystemStatusViewModel` |
| Open workspace `:248` | `NavigateToCanvasCommand` | `HomeViewModel` |
| Initialize sensors (empty state) `:300` | `NavigateToCanvasCommand` | `HomeViewModel` |
| Quick launch — Sensors `:309` | `NavigateToCanvasCommand` | `HomeViewModel` |
| Quick launch — Commands `:315` | `NavigateToRemoteCommand` | `HomeViewModel` |
| Quick launch — Apps `:321` | `NavigateToAppLauncherCommand` | `HomeViewModel` |
| Quick launch — Processes `:327` | `NavigateToTaskManagerCommand` | `HomeViewModel` |
| Quick launch — Files `:333` | `NavigateToFileTransferCommand` | `HomeViewModel` |
| Quick launch — Logs `:339` | `NavigateToDiagnosticLogsCommand` | `HomeViewModel` |
| Quick launch — Personalize `:345` | `NavigateToCustomizationCommand` | `HomeViewModel` |
| Quick launch — About `:351` | `NavigateToAboutCommand` | `HomeViewModel` |
| Clear activity `:365` | `ClearActivityCommand` | `HomeViewModel` |
| GitHub `:400` | `OpenGitHubCommand` | `HomeViewModel` |
| Play Store `:411` | `OpenPlayStoreCommand` | `HomeViewModel` |
| HWiNFO `:422` | `OpenHwInfoCommand` | `HomeViewModel` |
| Initialize link `:465` | `Connection.ConnectCommand` | `ConnectionViewModel` |
| Terminate link `:466` | `Connection.DisconnectCommand` | `ConnectionViewModel` |

**`HomeViewModel` also exposes `NavigateToHomeCommand`, `NavigateToSettingsCommand` and
`NavigateToRemoteDesktopCommand`, which this view never binds.** They are reachable from the shell,
not from here. Do not "restore" them into the rewrite on the strength of the view model.

Three buttons have conditional visibility, and losing the condition is worse than losing the button:

- Clear activity — `IsVisible="{Binding HasRecentActivity}"`
- Initialize link — `IsVisible="{Binding !Connection.IsConnected}"`
- Terminate link — `IsVisible="{Binding Connection.IsConnected}"`

Two have conditional enablement, both gated on the probe already running:
`IsEnabled="{Binding !SystemStatus.IsChecking}"` (`:132`) and the same via the cast on Fix (`:184`).

---

## 3. The live metric tiles and how they refresh

| Tile | Bound to | Source | Cadence |
|---|---|---|---|
| CPU text + bar | `CpuText`, `CpuPercent` | `Connection.TelemetryReceived` | ~1 Hz, push |
| Memory text + bar | `RamText`, `RamPercent` | same | ~1 Hz, push |
| Uptime | `UptimeText` | same | ~1 Hz, push |
| Pinned sensor tiles | `PinnedSensors` | `RefreshPinnedSensors()` | on navigation, not a timer |
| System status rows | `SystemStatus.Rows` | `SystemStatusViewModel.RefreshAsync()` | **once at construction, then on demand only** |
| Recent activity | `ActivityService.Instance.Recent` | shared singleton, `CollectionChanged` | event-driven |

Behaviour that has to survive:

- **There is no timer on this view.** The stats strip rides the existing telemetry stream, already
  marshalled to the UI thread by `ConnectionViewModel` (`HomeViewModel.cs:116–120`). A rewrite that
  introduces a `DispatcherTimer` to "refresh the dashboard" is adding a cost that does not exist
  today.
- **The strip blanks to `—` the instant the link drops** (`HomeViewModel.cs:104–105`). It must never
  show a stale number next to a dead link.
- **System status is deliberately not polled.** The probe shells out for the firewall check
  (`HomeViewModel.cs:49–53`). Putting it on a timer spends a process launch every few seconds to
  re-answer a question whose answer almost never changes.
- **`SystemStatus.RefreshAsync()` is fire-and-forget on purpose** (`HomeViewModel.cs:91–95`).
  Awaiting it would delay Home appearing; failure is already contained by the card's own Unavailable
  state.
- **Sensor selection is by `Name`, case-insensitively**, looking in placed cards first and then
  staged ones (`HomeViewModel.cs:179–194`). A pinned id with no matching sensor is silently skipped —
  that is intended, not a bug to fix during a restyle.

`HomeViewModel` unsubscribes all four handlers in `Dispose()` (`:255–262`). `ActivityService` and
`PhonePresenceMonitor` are process-wide singletons, so a leaked handler here outlives the view.

---

## 4. Tile layout, reorder, and what `DashboardLayoutService` actually persists

**HomeView has no drag and no reorder.** Worth stating plainly, because the bead asked and the answer
is "none": arranging cards is `CanvasView`'s job. Home is a read-only projection of the pin list.

- Pinned sensors render in a `ListBox` (`:256`) whose `ItemsPanel` is a
  `WrapPanel Orientation="Horizontal" ItemWidth="280"` (`:270`). The `ListBoxItem` template is
  stripped to a bare `ContentPresenter` (`:258–266`) so selection chrome never appears — this is a
  list used purely as a reflowing tile host.
- The outer two-column layout is itself a `WrapPanel` (`:242`): left column `MinWidth 400`/
  `MaxWidth 1400`, right column fixed `Width 340`. That is what makes quick launch drop below the
  sensors on a narrow window.
- Persistence: `DashboardLayoutService` writes `dashboard_layout.json` in the per-user RemEx
  directory, debounced 2000 ms. Home reads exactly one field —
  `_shell.LayoutService.CurrentProfile?.PinnedSensorIds` (`HomeViewModel.cs:173`) — and **never
  writes**. Every writer is in `CanvasDashboardViewModel`.
- `RefreshPinnedSensors()` is push-driven from eight call sites: `CanvasDashboardViewModel` (×4),
  `SettingsViewModel:804`, `ShellViewModel:518` (on navigate-to-home), and `TrayFlyoutViewModel:107`.
  A rewrite that makes Home observe the profile instead is a design change, not a restyle — and it
  would need all eight call sites revisited.

---

## 5. The four status indicators

| Ellipse | Where | Driven by |
|---|---|---|
| Row state dot | `:162`, inside the SystemStatus row template | local `Ellipse.state-dot` style + `Classes.attention="{Binding IsAttention}"` / `Classes.problem="{Binding IsProblem}"` |
| Radar ring | `:446` | `Classes.connected="{Binding Presence.IsPhoneAttached}"` |
| Radar halo | `:447` | same |
| Radar core | `:448` | same, plus `AutomationProperties.Name="{Binding Presence.PresenceAccessibleName}"` |

Two rules, both already load-bearing:

1. **All three radar ellipses follow phone presence, never `Connection.IsConnected`** (`RemEx-7zzw`).
   The loopback link is up essentially always, so a dot bound to it is green whether or not a phone
   is there. Binding only some of them makes the halo disagree with the dot inside it.
   `StatusDotPresenceBindingTests` enforces this repo-wide.
2. **The row dot uses class bindings, not a bound brush name** (`HomeView.axaml:10–12`). A
   `DynamicResource` cannot be resolved from a string a binding produced — it would silently render
   nothing. All three brushes exist in all four themes.

The `PresenceText` line beside the radar must move with the dot for the same reason: binding the dot
alone once put a red radar next to the word "Connected".

---

## 6. Item templates and their data sources

| Host | `x:DataType` | Source | Both states? |
|---|---|---|---|
| `ItemsControl` `:156` | `SystemStatusRowViewModel` | `Rows`, **with `DataContext="{Binding SystemStatus}"` set on the ItemsControl** | `IsVisible="{Binding !IsFullyReady}"` |
| `ListBox` `:256` | `SensorViewModel` | `PinnedSensors` | populated `PinnedSensors.Count` / empty `!PinnedSensors.Count` `:296` |
| `ItemsControl` `:373` | `services:ActivityEntry` | `RecentActivity` | populated `HasRecentActivity` / empty `!HasRecentActivity` `:390` |

**The `DataContext` shift on the SystemStatus ItemsControl is the single most fragile line in the
file.** It is set there so each row can reach the card's `FixCommand`; the rows then reach back out
through `$parent[ItemsControl].((vm:SystemStatusViewModel)DataContext)`. Move the `DataContext`, or
change the element type between the row and the ItemsControl, and both buttons go dead silently.

Fields consumed per template — a rewrite must still render all of them:

- **Row**: `IsAttention`, `IsProblem`, `Title`, `Sentence`, `ShowsFix`, `ShowsExplain`. Use
  `Sentence`, **never `ReadinessCheck.Detail`** — Detail is developer-facing English built for logs,
  sometimes straight from an exception message.
- **Sensor**: `History`, `ResolvedGraphType`, `Theme.AccentColor` (through `HexToColorConverter`),
  `Name`, `Value` (`StringFormat={}{0:F1}`), `Unit`. The `SparklineControl` sits behind the numbers
  at `Opacity 0.2` with negative margins so it bleeds to the card edge.
- **Activity**: `Glyph`, `Description`, `TimeLabel`. The feed scrolls inside `MaxHeight="300"`.

Every one of the three empty states is a real screen a user sees. Losing one leaves a blank region
where an invitation used to be.

---

## 7. Keyboard and accessibility

- **HomeView declares no `KeyBinding` and no `InputBindings` at all.** The `Ctrl + K`,
  `Ctrl + 1–7`, `Ctrl + ,` and `Esc` chips at `:99–119` are **decorative text**; the shortcuts
  themselves are owned by the shell. A rewrite cannot break them, and cannot fix them either — but
  if it deletes the chips, the app stops advertising them anywhere.
- 18 `AutomationProperties.Name` and 6 `ToolTip.Tip`. Every icon-or-glyph button — the 8 quick-launch
  tiles, the 3 link cards, the workspace button, the activity clear button — depends on the
  automation name, because its caption is a child `TextBlock` rather than `Content`.
- The radar core carries `Presence.PresenceAccessibleName`, so presence is announced rather than only
  coloured.
- The decorative `REMEX` watermark is correctly `IsHitTestVisible="False"` (`:78`).
- Focus-visible styling comes from `App.axaml`, not from here; `RemEx-kgs7g` owns keeping it.

---

## 8. Theming surface — what the rewrite is actually replacing

- **18 `glass-card` usages**, on `Border` and on `Button`. `RemEx-qbzl1` retires
  `Border.glass-card`; the `Button` usages (`:309`–`:351`, `:400`–`:422`) are the ones that will not
  be covered by a `Border`-only replacement.
- 15 distinct `DynamicResource` brushes and **exactly one hardcoded colour literal**: the footer
  status node's `BoxShadow="0 -8 32 0 #40000000"` (`:439`). It is a 25% black shade rather than a
  hue, which is presumably why it was written as a literal and why it has survived — but it is still
  a literal, and under a light theme a black drop shadow is what a smudge looks like. **Put the
  footer shadow on the SolarFlare by-eye pass.** The rewrite should either keep it deliberately or
  replace it with a theme resource; what it must not do is carry it forward without noticing.

  (An earlier draft of this section said the file had *zero* hex literals, and the assertion backing
  that claim could not have found this one — it only matched a literal filling an entire attribute
  value, so every `BoxShadow`, gradient stop and transition was invisible to it. Both are fixed; the
  test now allow-lists this single literal by value.)
- Three local `Button.action-pill` variants defined in `UserControl.Styles` (`:26–68`), including
  hover translate/scale transitions. `action-pill-danger` deliberately uses `SystemErrorBrush` and
  `SystemErrorBackgroundBrush` rather than a literal (`RemEx-fy0a`).
- 10 `StaticResource` icon geometries: `IconSearch`, `IconFlash`, `IconSensors`, `IconRemote`,
  `IconLauncher`, `IconTasks`, `IconFiles`, `IconLogs`, `IconPersonalize`, `IconAbout`.
  `RemEx-wyx2c` swaps these for `MaterialIcon`; that bead owns the mapping, and each one is a
  named target here so nothing is missed.
- `FontFamily="{StaticResource JetBrainsMono}"` appears throughout and is a large part of what the
  screen looks like today.
- Three emoji still sit in the markup — `🗒` (`:392`), `🐙` (`:403`), `🌡️` (`:425`) — plus `▶`
  (`:414`). `RemEx-me22` replaces the remaining emoji with `StreamGeometry` paths.

---

## 9. Localization

44 `local:Localize` keys, 37 of them `Home_*` plus `Shell_OpenCommandPaletteTooltip` and six
`SystemStatus_*`. All nine locales are green today.

Not repeated as a test assertion: `scripts/check-localization.ps1` already fails the build on a key
that is used but not declared, and a second copy of that check would drift out of sync with the
first.

---

## How to use this for RemEx-oszfm

1. Run `HomeViewCharacterisationTests` — 13 assertions, all mechanical. Any edit to
   `ExpectedCommands` or to the button/ellipse/automation-name counts must be a deliberate change
   somebody justifies in the commit message, not a number nudged to make CI green.
2. Walk sections 3, 4, 6 and 7 above by eye against the running app. Refresh cadence, the absence of
   a timer, and the three empty states are what a source-text test cannot confirm — it can see that
   the markup for an empty state is still present, not that it renders or that it renders the right
   thing.
3. Check all four PC themes. Section 8 is the inventory of what is theme-sensitive. The test pins
   "no hex literals"; it cannot pin that a theme brush was the *right* theme brush.
4. Anything in this document the rewrite *intends* to change: say so in the bead before changing it.
   The point of writing it down first was to make that an explicit decision.

### What the test still cannot catch

Worth knowing before trusting it further than it goes. It reads source text and reflects over types;
it does not render. So it cannot see a control that is present but zero-sized, a binding that
resolves to the right member of the wrong instance, a `DataContext` moved somewhere that still
happens to satisfy the string match, or a tab order. It also only catches a view-model-side rename
after `remex.desktop` has been rebuilt — the AXAML half is read from disk at run time, the
reflection half is not.

The routing assertion in `TheSystemStatusRowsStillReachTheCardsCommandsTheSameWay` exists because
that gap was measured rather than assumed: rewriting `$parent[ItemsControl]` to `$parent[Border]`
kills both the Fix and Explain buttons and left every other assertion in the file green.
