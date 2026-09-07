# Sensor alerts center — design

Bead: RemEx-8wpvr (epic). Date: 2026-09-07. Status: approved by Connor (option A, "latched until acknowledged").

## Problem

Sensor alerts exist on the desktop app but are invisible as a feature. A card with an alert looks identical to one without. When a threshold is crossed the card flashes red for two seconds and a number ticks up on the Sensors nav item, but nothing says which sensor did it, nothing reaches the tray, and the only way to inspect or remove an alert is to right-click every card. The "little dots" Connor sees are the minimap's card rectangles turning red during those two-second flashes.

## Goals

1. A card with an alert configured shows it.
2. A sensor that crosses its threshold is marked on its card until someone acknowledges it, even if the value has since recovered.
3. One place lists every configured alert with edit, remove, copy-to-other-sensors, acknowledge-all and a confirmed reset-all.
4. A crossing produces a notification by the clock when the window is hidden.
5. Alerts continue to ride along in the profile export.

## Non-goals

- Native Windows toasts into Action Center. The desktop targets plain `net10.0`; this would need a Windows-specific target or package. Filed as a follow-up bead.
- Persisting the tripped state across restarts. The app is tray-resident; session-only is enough.
- Alert history UI. The existing 20-entry `AlertNotifications` ring stays untouched and unbound.
- Android. Nothing there consumes sensor alerts.
- Hysteresis on the threshold itself. Edge-trigger and re-arm stay exactly as `SensorViewModel.CheckAlert` does them today.

## Current state (what exists, by file)

| Concern | Where | Notes |
|---|---|---|
| Alert record | `remex.core/Models/SensorAlert.cs:21` | `SensorName`, `Threshold`, `Direction` (Above/Below), `Severity` (Warning/Critical). Record, `init`-only. |
| Persistence | `remex.core/Models/DashboardProfile.cs:151` `SensorAlerts` | Loaded at `CanvasDashboardViewModel.cs:477`, saved at `:1391` from the private `_sensorAlerts` dictionary. |
| Set/remove | `CanvasDashboardViewModel.SetAlert` `:268`, `ApplySensorAlert` `:281`, `ApplyAlertToSensors` `:296` | Context menu at `CanvasView.axaml:257`; dialog opened by `CanvasView.axaml.cs:173-183` via `ShowSetAlertRequested` (`CanvasDashboardViewModel.cs:86`). `SetAlertDialog(string sensorName, SensorAlert? existing, Action<SensorAlert?> onResult)`; a null result removes. |
| Evaluation | `SensorViewModel.CheckAlert` `:386-406` | Edge-triggered: fires `AlertTriggered` once when the value crosses, `IsAlertActive` is the live latch, re-arms when the value returns. `Alert` property `:81-88` is a plain setter with no change notification. |
| Flash | `CanvasDashboardViewModel.OnSensorAlertTriggered` `:307-325` | Sets `CanvasCardViewModel.IsAlertActive` (`CanvasCardViewModel.cs:67`) on every matching card, raises `SensorAlertFired`, resets after `Task.Delay(2000)`. `DraggableCard.cs:98,120-126` toggles the `alert-active` class; `CanvasView.axaml:29-36` styles it (error border, drop shadow 20/0.85). `CanvasMinimap.cs:157` paints `IsAlertActive` cards with the alert brush. |
| Badge | `ShellViewModel.cs:150-161, 634, 873-881, 947-950` | `AlertBadgeCount++` per firing; `HasAlerts`; `DismissAlerts` clears count and ring; `NavigateToCanvas` zeroes the count. Bound in `ShellView.axaml` via `material:Badged` on the Sensors nav item. Guarded by `remex.desktop.tests/Views/AlertBadgeTests.cs`. |
| Notifications | `remex.desktop/Services/NotificationService.cs:97` `Notify(importance, title, message)`; `NotificationRouter.cs:17` `NotificationImportance` | Router rule, guarded: visible window gets the in-app toast; hidden window gets the tray balloon for Outcome and Problem, nothing for Informational. Sensor alerts do not call it today. |
| Export | `remex.desktop/Services/Backup/RemexSavefileService.cs:120` `ExportAsync`, `:237` `ImportDashboardLayoutAsync` | `RemexSavefileSections.DashboardLayout` is the whole `DashboardProfile`, so alerts already export. Import raises `ProfileReplaced`. |
| Settings | `remex.desktop/Views/SettingsView.axaml` (flat stack of `material:Card Padding="24"` sections from `:89`), `SettingsViewModel.cs` | `IsReducedMotion` proxied from `ShellViewModel` at `SettingsViewModel.cs:214`. |
| Confirm dialog | `remex.desktop/Views/ConfirmationDialogHost.cs:23` | Static helper used for destructive confirmations. |
| DI | `remex.desktop/App.axaml.cs:59-87` | Singletons registered on the service collection. |
| Localization | `remex.desktop/Localization/Strings.resx` plus 8 locales (es, fr, hi, id, pl, pt-BR, tr, uk) | Every new key lands in all nine. |

## State model

Per sensor name (case-insensitive, matching `ApplyAlertToSensors`), three flags with one owner each:

| Flag | Meaning | Owner | Persisted |
|---|---|---|---|
| **Configured** | an alert exists for this sensor | `SensorAlertStore` | yes, via `DashboardProfile.SensorAlerts`, unchanged shape, no schema bump |
| **Live** | value is over the threshold right now | `SensorViewModel.IsAlertActive` (exists) | no |
| **Tripped** | crossed the threshold and not yet acknowledged; carries the time and the value at the crossing | `SensorAlertTracker` | no (session only) |

Transitions:

- Configured set/changed/removed → store `Changed`; canvas re-applies to `SensorViewModel.Alert`; profile save is triggered as today. Removing an alert also acknowledges it.
- `SensorViewModel.AlertTriggered` → tracker `Trip(name, at, value, alert)`. Tripped is idempotent; a second crossing before acknowledgement updates the timestamp and value, nothing else.
- Acknowledge(name) or AcknowledgeAll() → tracker removes; `TrippedChanged` fires.
- Sidebar number = tracker's tripped count. `NavigateToCanvas` no longer zeroes anything.
- Live has no interaction with Tripped. A card acknowledged while still hot keeps pulsing until the value recovers and does not count in the badge.

## Components

### `SensorAlertStore` (new, `remex.desktop/Services/SensorAlertStore.cs`)

What it does: the single in-memory source of configured alerts.

- `IReadOnlyCollection<SensorAlert> All`
- `bool TryGet(string sensorName, out SensorAlert alert)`
- `void Set(SensorAlert alert)` — upsert by `SensorName`
- `void Remove(string sensorName)`
- `void Clear()`
- `void ReplaceAll(IEnumerable<SensorAlert> alerts)` — used on profile load and after import; fires `Changed` once
- `event Action? Changed`

Depends on nothing. Registered as a singleton in `App.axaml.cs`. Persistence stays where it is: `CanvasDashboardViewModel` calls `ReplaceAll` from the profile at load (`:477` path) and writes `store.All.ToList()` into `SensorAlerts` at save (`:1391`). The private `_sensorAlerts` dictionary is deleted. `ApplySensorAlert` becomes a thin call into the store; the canvas subscribes to `Changed` to re-run `ApplyAlertToSensors` for the affected name and `TriggerSave`.

### `SensorAlertTracker` (new, `remex.desktop/Services/SensorAlertTracker.cs`)

What it does: runtime trip state and the notification cooldown.

- `IReadOnlyCollection<TrippedAlert> Tripped` where `TrippedAlert(string SensorName, DateTimeOffset At, double Value, SensorAlert Alert)`
- `int TrippedCount`
- `bool IsTripped(string sensorName)`
- `bool Trip(string sensorName, double value, SensorAlert alert, DateTimeOffset now)` — returns `true` when a notification should be sent, i.e. the sensor has not notified within the last 60 seconds. The trip is recorded regardless of the return value.
- `void Acknowledge(string sensorName)`, `void AcknowledgeAll()`
- `event Action? TrippedChanged`

Takes a clock (`Func<DateTimeOffset>` or `TimeProvider`) so the cooldown is testable. Registered as a singleton. Removing an alert from the store acknowledges it; the canvas does that in its `Changed` handler when the name is gone.

### `SensorViewModel` (edit)

- `Alert` becomes observable and exposes `HasAlert` (`Alert is not null`).
- `AlertTriggered` gains the current value: `Action<SensorAlert, double>`. Two subscribe sites (`CanvasDashboardViewModel.cs:1190-1191, 1695`).
- `CheckAlert` logic unchanged.

### `CanvasCardViewModel` (edit)

- `IsAlertActive` is re-pointed at the sensor's live flag (mirror `Sensor.IsAlertActive`; the 2-second timer and its `ContinueWith` are deleted).
- New `IsAlertTripped` (bool) and `HasAlert` (bool, mirrors `Sensor.HasAlert`), set by the canvas from the tracker and store.
- New `AcknowledgeAlertCommand` → tracker `Acknowledge(Sensor.Name)`.

### `CanvasDashboardViewModel` (edit)

- Takes `SensorAlertStore` and `SensorAlertTracker` in its constructor.
- `OnSensorAlertTriggered(alert, value)`: `tracker.Trip(...)`; if it returns `true`, raise `SensorAlertFired(alert, value)` (the shell notifies). Refresh `IsAlertTripped` on matching cards. No flash timer.
- Store `Changed` → re-apply alerts to sensors, acknowledge removed names, refresh `HasAlert` on cards, `TriggerSave`.
- Tracker `TrippedChanged` → refresh `IsAlertTripped` on cards.
- Card pointer press acknowledges: `DraggableCard` already handles pointer for drag; on press, if the card's `IsAlertTripped`, invoke `AcknowledgeAlertCommand`. Selecting or dragging the card is otherwise unchanged.

### `ShellViewModel` (edit)

- `AlertBadgeCount` becomes a derived read of `tracker.TrippedCount`, refreshed on `TrippedChanged`. `HasAlerts` unchanged in meaning.
- `OnSensorAlertFired(alert, value)`: keep the ring insert; add the notification (below).
- `NavigateToCanvas` no longer touches the count. `DismissAlerts` calls `tracker.AcknowledgeAll()` and still clears the ring.

### Canvas visuals (`CanvasView.axaml`, `DraggableCard.cs`, `CanvasMinimap.cs`)

- **Bell**: in the sensor card header grid (`CanvasView.axaml:336-340`), right of the name: `mi:MaterialIcon` 14 px. `Kind="BellOutline"` with the sensor theme's unit colour when `HasAlert` and not tripped; `Kind="BellRing"` in the error colour when `IsAlertTripped`. Hidden when no alert. It is a button: clicking it runs `AcknowledgeAlertCommand`. Tooltip "Alert: above 90 °C · Critical" / "Tripped at 00:42 (91.2 °C). Click to acknowledge."
- **Pulse**: the `ctrl|DraggableCard.alert-active` style (`:29-36`) keeps the error border and gains a keyframe `Animation` on the glow, `IterationCount="Infinite"`, `Duration="0:0:1.6"`, ease in and out, opacity between roughly 0.35 and 0.85. A second style `ctrl|DraggableCard.alert-active.reduced-motion` (or a class toggled from `IsReducedMotion`) has no animation and holds the glow steady. Only cards with `IsAlertActive` carry the class, so the cost is bounded by the number of hot sensors, not the sensor count. Implementation may animate `BoxShadow` or the `DropShadowEffect.Opacity`; the plan decides which one Avalonia animates cleanly.
- **Minimap** (`CanvasMinimap.cs:157`): paint with the alert brush when `IsAlertActive || IsAlertTripped`.

### Alerts card in Settings (`SettingsView.axaml`, `SettingsViewModel.cs`)

A new `material:Card Padding="24"` titled **Sensor alerts**, placed after the existing canvas/grid card. Bound to a new `SensorAlertsSectionViewModel` (own file, owned by `SettingsViewModel`) that takes the store, the tracker and an `ISensorCatalog`:

```
interface ISensorCatalog
{
    IReadOnlyList<SensorInfo> Known { get; }          // every sensor the canvas has seen this session
    bool TryResolve(string name, out SensorInfo info);
}
record SensorInfo(string Name, string DisplayName, string? Unit, bool IsConnected);
```

`CanvasDashboardViewModel` implements it over `Cards` plus `StagedCards`, and the shell hands the instance to the settings section. The section and the picker never reference the canvas view model directly.

- Rows: a `ListBox` (virtualising) with `MaxHeight`, one row per alert sorted by display name. Row shows display name (stored name when the sensor is not connected, in the muted colour), "above 90 °C · Critical" built from the record, and when tripped a second line "Tripped at 00:42 · 91.2 °C" in the error colour.
- Row buttons: **Edit** (raises `EditRequested(sensorName, existing)`; `SettingsView.axaml.cs` opens `SetAlertDialog` the same way `CanvasView.axaml.cs:173-183` does and writes the result to the store), **Copy to…** (opens the picker below), **Remove** (store `Remove`).
- Footer buttons: **Acknowledge all** (tracker `AcknowledgeAll`, enabled when tripped count > 0) and **Reset all alerts** (goes through `ConfirmationDialogHost` with title "Reset all sensor alerts?", body "This removes N alerts. Nothing else in your profile changes.", then store `Clear()`). Reset is the error-coloured tonal button.
- Empty state: "No alerts configured. Right-click a sensor card on the canvas to set one."

### Copy-to picker (`remex.desktop/Views/CopyAlertDialog.axaml(.cs)` + view model)

- Header: "Copy alert from CPU Package: above 90 °C · Critical".
- Filter `TextBox` at the top.
- `ListBox SelectionMode="Multiple"` of candidate sensors (display name, unit, and "has alert" marker when one already exists). Candidates are `ISensorCatalog.Known` minus the source.
- `ToggleSwitch` "Show all sensors", off by default: the list shows only sensors whose unit matches the source sensor's unit. When the source sensor's unit is unknown (not connected), the toggle is on and disabled.
- **Apply** writes one `SensorAlert` per selected sensor with the source's threshold, direction and severity, through `store.Set`. Existing alerts on those sensors are overwritten; the button label says "Apply to N" and the tooltip warns about overwrites when any selected sensor already has one. **Cancel** does nothing.

### Notification (`ShellViewModel.OnSensorAlertFired`)

```
importance = alert.Severity == Critical ? NotificationImportance.Problem : NotificationImportance.Outcome
title      = Alert_Notify_Title  → "{DisplayName} hit {Value}{Unit}"
message    = Alert_Notify_Body   → "Alert: {above|below} {Threshold}{Unit} · {Severity}"
NotificationService.Instance.Notify(importance, title, message)
```

Routing is the existing guarded rule: hidden window → tray balloon; visible window → in-app toast. Clicking the balloon opens the window as today. The 60-second per-sensor cooldown lives in the tracker's `Trip` return value, so a hovering value cannot spam.

## Persistence and export

No new fields. `DashboardProfile.SensorAlerts` keeps its shape. After `ImportDashboardLayoutAsync` raises `ProfileReplaced`, the canvas's existing reload path calls `store.ReplaceAll` so the settings card and bells refresh without a restart. A round-trip test proves an exported savefile with alerts imports them back into the store.

## Localization

New keys (all nine resx files): `Canvas_AlertBellConfigured`, `Canvas_AlertBellTripped`, `Settings_Alerts_Title`, `Settings_Alerts_Empty`, `Settings_Alerts_Edit`, `Settings_Alerts_CopyTo`, `Settings_Alerts_Remove`, `Settings_Alerts_AcknowledgeAll`, `Settings_Alerts_ResetAll`, `Settings_Alerts_ResetConfirmTitle`, `Settings_Alerts_ResetConfirmBody`, `Settings_Alerts_Above`, `Settings_Alerts_Below`, `Settings_Alerts_TrippedAt`, `CopyAlert_Title`, `CopyAlert_Filter`, `CopyAlert_ShowAll`, `CopyAlert_Apply`, `CopyAlert_OverwriteHint`, `Alert_Notify_Title`, `Alert_Notify_Body`. Severity names reuse whatever `SetAlertDialog` already localises.

## Testing

Unit (no Avalonia):
- Store: set/upsert/remove/clear/replace-all, `Changed` fires once per mutation and once for `ReplaceAll`, case-insensitive names.
- Tracker: trip is idempotent, updates time and value, acknowledge/all, count, `TrippedChanged`; cooldown returns `true` on first trip, `false` within 60 s, `true` after; acknowledging does not reset the cooldown.
- Shell: badge equals tracked count; Critical → Problem, Warning → Outcome; `NavigateToCanvas` leaves the count alone; `DismissAlerts` acknowledges all.
- Canvas: trip marks only matching cards; removing an alert acknowledges and clears `HasAlert`; `IsAlertActive` mirrors the sensor's live flag with no timer.
- Copy picker: unit filter default, show-all toggle, source excluded, apply overwrites.
- Export: savefile with alerts round-trips into the store.

Source-scrape guards (the repo's pattern, since there is no headless render):
- `CanvasView.axaml` binds a bell to `HasAlert` and `IsAlertTripped`, and the `alert-active` style carries `IterationCount="Infinite"` plus a reduced-motion branch.
- `SettingsView.axaml` contains the alerts card bound to the section view model, and reset goes through `ConfirmationDialogHost`.
- `AlertBadgeTests.SomethingClearsTheCount` is rewritten to assert the acknowledge path clears the count and `NavigateToCanvas` does not.
- All nine resx files carry every new key.

Eyes (`ui-verify`): bell in both states, the pulse on a live card and its steady form with reduced motion on, the settings card with rows, empty state, the copy picker, and one tray balloon with the window hidden. Queued as notes on the epic for Connor.

## Follow-ups (separate beads)

- Native Windows toast (Action Center) sink behind `ITrayBalloonSink`, Windows-only, no-op on Linux.
- Persist tripped state across restarts if tray-resident use turns out not to be the norm.

## Acceptance

1. A sensor card with an alert shows a bell; one without does not.
2. Crossing the threshold turns the bell red and starts a smooth pulse; the pulse stops when the value recovers; the red bell stays until the card or bell is clicked or Acknowledge all is used.
3. The Sensors nav badge equals the number of unacknowledged sensors and does not change when the canvas is opened.
4. Settings → Sensor alerts lists every alert with Edit, Copy to…, Remove; Acknowledge all and a confirmed Reset all work; the empty state reads correctly.
5. With the window hidden, a Critical or Warning crossing shows a balloon by the clock with the sensor, value and threshold; the same sensor re-crossing within 60 s shows nothing new.
6. Export → import on a fresh profile restores the alerts and the settings card lists them.
7. `scripts/verify.ps1 -Check` passes; nine locales carry every new key.
