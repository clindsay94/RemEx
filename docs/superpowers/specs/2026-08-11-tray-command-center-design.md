# Tray Command Center — design

**Date:** 2026-08-11
**Branch:** v2.5-board-drain
**Supersedes:** the "Live Glance" tray flyout (`remex.desktop/Views/TrayFlyoutWindow.axaml`), unchanged in
composition since ~1.10.

## Problem

The tray flyout is a fixed 360×auto popup pinned to the bottom-right of the primary screen. It cannot be
resized or moved, it borrows `HomeViewModel` wholesale, and its entire action set is two navigation buttons
and a close button. Its right-click menu — Show / Live Glance / Theme / Exit — has not changed in years.
Visually it is a stack of sensor cards with a hard grey rectangle around its rounded corners.

Three separate defects are tangled together here, and they need to be named separately because they have
different fixes:

1. **Chrome.** The window requests `Mica, Blur, Transparent` (`TrayFlyoutWindow.axaml.cs:14`) while the
   visible rounded card is an inner `Border` with `Margin="12"`. When DWM grants Mica it composites the
   backdrop across the **whole window rect**, including that margin. The result is an opaque square behind
   a rounded card — the sharp grey outer rectangle.
2. **Interaction.** `CanResize="False"` and `Focusable="False"` are both set. `Focusable="False"` is also
   why the window cannot dismiss itself on click-away, and would block `BeginMoveDrag`.
3. **Capability.** The flyout does nothing. Not because the UI is thin, but because it was given a
   telemetry view model and no commands.

## Architectural finding that bounds the scope

**The PC cannot control the phone, and no amount of UI work changes that.**

`ConnectionViewModel` opens a WebSocket to the embedded host **in its own process**, over loopback.
`PhonePresence.IsPhone` (`remex.desktop/Services/PhonePresence.cs:97`) exists precisely because "connected"
on the desktop means loopback, not phone. Every RemEx feature is phone-initiated: `desktop_start`,
`clipboard_push`, `clipboard_request`, file browse and screenshot capture all originate on the phone
(`remex.agent/Handlers/PingPongHandler.cs:474, :507, :902`).

The only PC-initiated actions that reach a phone are unpair/revoke (`SettingsViewModel.cs:346` →
`PairedDeviceRevoker`) and file-trust grant changes. There is no PC→phone message type for clipboard push,
ring/locate, notification, or wake — not unimplemented handlers, **no message types at all** in
`remex.core/Messages`.

Note also that "Send to phone" on `FileTransferView.axaml:174` is a mislabel: `SendFileAsync`
(`FileTransferViewModel.cs:581`) is byte-identical to `UploadAsync` and uploads into the host's shared root
over loopback. It differs only in queue label and activity kind. **This spec does not fix that**, but no new
surface here may reuse that name for a phone-directed action.

**Decision: this spec covers PC-side controls only.** A PC→phone command channel is a separate epic spanning
`remex.core`, `remex.agent` and `remex.android`. The grid designed below has room for those tiles when it
lands; nothing here pretends to control the phone.

## Design

### 1. `TrayFlyoutViewModel`

New view model in `remex.desktop/ViewModels/`. The flyout stops using `HomeViewModel` as its `DataContext`.

It **composes**, it does not reimplement:

| Concern | Source |
|---|---|
| Presence dot, device name, presence text | `PhonePresenceMonitor.Instance` (existing singleton) |
| Power commands | `ConnectionViewModel` — `LockAsync`, `SleepAsync`, `HibernateAsync`, `SignOutAsync`, `RestartAsync`, `ShutdownAsync` (`ConnectionViewModel.cs:686-717`) |
| Pinned sensors | `HomeViewModel.PinnedSensors` |
| Navigation | `ShellViewModel` navigation commands, as the current flyout already does |
| Pairing state | `SettingsViewModel.CanListPairedDevices` / paired-device source |

Owned state, in full:

- `IsPinned` (bool) — transient vs pinned mode.
- `Tiles` — an ordered `IReadOnlyList<TrayTile>`, so the grid is data-driven rather than eight hand-placed
  buttons in XAML.

`TrayTile` is a small record exposing `Label`, `IconKey`, `Command`, `IsEnabled` and `HasSubmenu`. It exists
so tile enablement is a testable property rather than a XAML binding expression.

**Why a new VM rather than growing `HomeViewModel`:** `HomeViewModel` is the sensor-pinning view model and
is already consumed by `HomeView`. Adding power commands to it would couple the home page to
`ConnectionViewModel` for the benefit of a different surface, and would make the flyout's tile set
untestable in isolation.

### 2. Chrome — the grey rectangle

- Remove `Mica` and `Blur` from `TransparencyLevelHint`; request `Transparent` only, in **both**
  `TrayFlyoutWindow.axaml` and the code-behind (they currently disagree — the axaml says
  `"AcrylicBlur, Mica, None"`, the constructor overwrites it with `Mica, Blur, Transparent`). One
  declaration, in the code-behind, with the axaml attribute removed.
- Replace the backdrop with a **themed translucent `SolidColorBrush`** on the inner rounded `Border` — one
  new alpha-bearing token per theme (see §7). `ExperimentalAcrylicBorder` is deliberately **not** used: it is
  marked experimental in Avalonia, and a per-theme translucent brush is deterministic, clips to
  `CornerRadius` without argument, and behaves identically on Linux.
- On Windows, additionally set the DWM `DWMWA_WINDOW_CORNER_PREFERENCE` attribute to `DWMWCP_ROUND` on the
  window handle. This is belt-and-braces against the OS-drawn edge and is a no-op on Windows 10.
- Non-Windows: the P/Invoke is guarded by `OperatingSystem.IsWindows()`. Linux keeps the transparent-window
  path, which already works.

### 3. Layout

```
┌────────────────────────────────────────┐
│ ● Galaxy S26 Ultra          ⚙  ▣  ✕   │   status strip
│   Connected · 1 device                 │
├────────────────────────────────────────┤
│ ┌──────┐ ┌──────┐ ┌──────┐             │
│ │ Lock │ │Sleep │ │Remote│             │   action grid
│ └──────┘ └──────┘ └──────┘             │   (reflowing)
│ ┌──────┐ ┌──────┐ ┌──────┐             │
│ │ Send │ │ Pair │ │Power▾│             │
│ └──────┘ └──────┘ └──────┘             │
├────────────────────────────────────────┤
│ CPU 42% ▁▃▅▂  GPU 61% ▂▅▇▃  RAM 18/32  │   sensor strip
└────────────────────────────────────────┘
```

- **Status strip** — presence dot (existing `status-dot` style + `connected` class), device name,
  connection line. Right-aligned: settings, pin toggle, close. Doubles as the drag handle in pinned mode.
- **Action grid** — a `UniformGrid` whose `Columns` binds to window width through a converter:
  `< 380px → 2`, `< 520px → 3`, `>= 520px → 4`. This is what makes resizing worth doing; without it,
  resizing only stretches cards.
- **Sensor strip** — pinned sensors demoted to one dense horizontal row, each a name, a value and a small
  sparkline. Reuses `SparklineControl`. Scrolls horizontally when the pin count exceeds the width. When
  nothing is pinned the strip collapses entirely (`IsVisible` bound to count) rather than showing the
  current empty-state paragraph — a command center with no telemetry should just be shorter.

### 4. Tiles

| Tile | Command | Enabled when |
|---|---|---|
| Lock | `ConnectionViewModel.LockAsync` | always |
| Sleep | `ConnectionViewModel.SleepAsync` | always |
| Remote desktop | navigate to `RemoteDesktopView` | a phone is attached |
| Send file | navigate to `FileTransferView` | always |
| Pair | navigate to pairing / reveal PIN | no phone attached |
| Power ▾ | opens submenu | always |

Power submenu: Restart, Shutdown, Sign out, Hibernate. Restart, Shutdown and Sign out each route through the
existing `ConfirmationDialogHost` before executing. `ForceShutdownAsync`, `ForceRestartAsync` and
`RestartToUefiAsync` are **not** exposed in the flyout — main window only.

Disabled tiles render dimmed with an explanatory tooltip; they do not disappear. A grid whose contents move
depending on state is harder to build muscle memory against than one with a greyed cell.

### 5. Two modes

| | Transient (default) | Pinned |
|---|---|---|
| Position | tray corner, via existing `TrayPlacement.BottomRight` | last saved position |
| `Focusable` | `false` | `true` |
| `CanResize` | `false` | `true` |
| `Topmost` | `true` | `true` |
| Dismiss | on `Deactivated` | never; explicit close only |
| Drag | no | header `BeginMoveDrag` |
| Geometry saved | no | on move/resize, debounced |

The pin button toggles between them. `SystemDecorations.None` is retained in both — the window keeps its
custom chrome; resize grips come from `CanResize` on an undecorated window, which Avalonia supports.

**Deactivate-dismiss caveat.** The current code-behind comment warns that hiding on `Deactivated` "might
conflict with the Tray menu showing". This is real: on Windows, opening the tray context menu deactivates
the flyout. Mitigation — the handler ignores deactivation within 250ms of the window being shown, and
`App.OnTrayIconClicked` sets a suppression flag while a native menu is open. This must be verified on the
installed exe, not just in a dev run.

### 6. `TrayFlyoutLayoutStore`

New service in `remex.desktop/Services/`, following the `FileTransferRootSettingsService` pattern: a small
JSON file under `RemexDataPaths` (**not** `SpecialFolder` directly — see the RemEx-mz9f note in
`RemexSavefileService.cs:79`).

Schema:

```json
{ "isPinned": true, "x": 2140, "y": 620, "width": 460, "height": 380 }
```

Load-time validation, in order:
1. Missing or unparseable file → defaults (transient, no geometry). Never throws to the caller.
2. Width/height clamped to `[320, 900] × [240, 800]`.
3. The saved rect must intersect the working area of **some** currently-connected screen by at least
   100×100 logical pixels. If it does not — monitor disconnected, resolution changed — the geometry is
   discarded and the window falls back to tray placement. This is the failure that strands a window
   offscreen with no way to recover it, and it is the primary reason this store gets tests.

Saves are debounced (500ms) off move/resize so a drag does not write the file every frame.

### 7. Theming

All four PC themes — CyberNOC, Monolith, SolarFlare, BaseDarkGlass — must be verified visually. Rules:

- Tokens only. No hardcoded colors, no hardcoded `#RRGGBB` in the new XAML.
- Tile surfaces use `CardBackgroundBrush` / `CardBackgroundHoverBrush` / `CardBorderBrush`; accents use
  `AccentPrimaryBrush`. If a needed token does not exist, it is added to all four theme dictionaries in the
  same commit — never defaulted in one and omitted in another.
- The translucent inner border (§2) must be checked against the light theme specifically: the current
  `GlassBaseDarkBrush` is a dark-glass token and will read wrong if reused unconditionally.

### 8. Localization

Every new string lands in all 9 `.resx` files (`Strings.resx` plus es, fr, hi, id, pl, pt-BR, tr, uk).
`scripts/check-localization.ps1` gates this. Expected new strings: 6 tile labels, 4 power submenu items,
~5 tray menu items, pin/unpin tooltips, disabled-tile tooltips, and accessible names for the status strip
controls. Reuse existing strings where the wording is genuinely identical — do not invent a second variant
of a phrase that already exists (the current flyout's empty state reuses `Home_NoPinnedSensorsHint` for
exactly this reason).

### 9. Tray context menu

Rebuilt in code rather than declared in `App.axaml`, so items can enable and disable with state:

```
  Galaxy S26 Ultra — Connected      (disabled header)
  ─────────────────────────────
  Lock PC
  Remote desktop                    (disabled with no phone)
  Open transfers
  Pair a device                     (disabled when paired)
  ─────────────────────────────
  Show RemEx
  Settings
  Exit
```

The status header refreshes from `PhonePresenceMonitor`, which already polls at 3s
(`PhonePresenceMonitor.cs:188`) and already drives the tray tooltip. `UpdateTrayMenuHeaders`
(`App.axaml.cs:533`) extends to own the header line as well as the locale refresh.

"Live Glance" and the theme toggle leave the menu. Theme switching stays in Settings, where it belongs; the
`Tray_LiveGlance` and `Tray_SwitchLightMode` strings are removed from all 9 files.

## Testing

| Unit | Test |
|---|---|
| `TrayFlyoutLayoutStore` | roundtrip; missing file → defaults; corrupt JSON → defaults, no throw; oversize clamped; offscreen rect rejected; rect on a secondary screen preserved |
| Column-count converter | breakpoints at 379/380/519/520; degenerate widths (0, NaN) → 2 |
| `TrayFlyoutViewModel` | tile order stable; Remote-desktop tile disabled with no phone and enabled when attached; Pair tile inverse; destructive power commands route through confirmation, not straight to `ConnectionViewModel` |
| Tray menu | header text tracks presence; items enable/disable with state; all headers resolve through `LocalizationService` |

Mutation-verify the enablement predicates and the offscreen check — those are the assertions most likely to
be vacuously true.

**Manual verification, on the installed exe** (`C:\Program Files\RemEx\Remex.Agent.exe` via
`update-local-install.ps1` — a `dotnet <dll>` run trips the .NET Host firewall prompt):

1. No grey rectangle at 100%, 125%, 150% and 200% display scaling.
2. All four themes.
3. Pin, drag to a second monitor, resize, restart the app — geometry restored.
4. Disconnect that monitor, restart — window returns to the tray corner rather than vanishing.
5. Right-click the tray icon with the flyout open — the flyout does not vanish behind the menu.

## Out of scope

- Any PC→phone command channel. Separate epic across `remex.core`, `remex.agent`, `remex.android`.
- Fixing the "Send to phone" mislabel on `FileTransferView`.
- Android-side changes of any kind. The four PC themes do not exist on Android; this is desktop-only.
- Replacing `PhonePresenceMonitor`'s 3s polling with a push signal (noted as the better end state at
  `PhonePresenceMonitor.cs:108-114`, but independent of this work).
