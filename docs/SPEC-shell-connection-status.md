# SPEC — Shell connection status, and the trust card's opaque device names

**Status:** approved by Connor 2026-08-11, not yet implemented
**Beads:** [`RemEx-44gc6`](#1-the-nav-drawer-status-control) (status rework), [`RemEx-9me77`](#8-the-trust-cards-opaque-device-names) (trust names)
**Out of scope, filed separately:** `RemEx-xt0af` (PC→phone browsing), `RemEx-drhfx` (clipboard manager)

Two unrelated defects reported in the same session, both in `remex.desktop`, both fixed
by wiring up information the app already has and throws away. They share a spec because
they ship together, not because they share code.

---

## 1. The nav-drawer status control

### What is there today

`remex.desktop/Views/ShellView.axaml:238-254`. Pinned to the bottom of the left nav
drawer:

```
Border (rounded, CardBackground, CardBorder)
└── Grid ColumnDefinitions="64,*"
    ├── [0] Ellipse .status-dot, .connected ← Presence.IsPhoneAttached
    └── [1] StackPanel  IsVisible ← IsDrawerOpen
        ├── Presence.PresenceText      (phone presence — the headline)
        ├── Connection.StatusText      (host link — the detail)
        └── Connection.LatencyText     (visible only while connected)
```

### Why it reads as a toggle switch

**The silhouette is structural, not cosmetic.** The entire text column is
`IsVisible`-bound to `IsDrawerOpen`. Collapse the drawer and what survives is a 64px
rounded `Border` containing one 12px `Ellipse` — which is, to the pixel, the shape of a
`ToggleSwitch`. There is no `ToolTip.Tip` anywhere on it, so in that state the control
conveys exactly one bit: the dot is green or it is red.

### Why one bit is not enough

That single bit is overloaded four ways. `PhonePresenceMonitor` collapses
`PhonePresenceState` plus a host-down branch into one `bool`:

| Situation | `IsPhoneAttached` | Dot |
|---|---|---|
| `IClientSessionSource` missing — the embedded host failed to start | `false` | red |
| Host healthy, no phone attached | `false` | red |
| One phone attached | `true` | green |
| Several phones attached | `true` | green |

Rows 1 and 2 are the ones that matter, and they are indistinguishable. **The codebase
already knows this.** `remex.desktop/ViewModels/PhonePresenceMonitor.cs:124-131`, written
during RemEx-t6s1:

> A MISSING SOURCE IS NOT AN ABSENT PHONE (RemEx-t6s1). […] With the drawer COLLAPSED the
> two text lines are hidden and the dot is the entire indicator, so the user goes and
> checks their phone, their Wi-Fi and their pairing while the fault is on this PC.

That comment describes the reported defect precisely. It was written as a caveat on
another fix; this spec is the fix.

---

## 2. Presence model extension

`remex.desktop/ViewModels/PhonePresenceMonitor.cs` and
`remex.desktop/Services/PhonePresence.cs`.

### New surface on `PhonePresenceMonitor`

| Member | Type | Source |
|---|---|---|
| `State` | `ShellConnectionState` | new enum: `HostDown`, `NoPhone`, `PhoneAttached` |
| `DeviceName` | `string?` | `PhonePresenceStatus.FirstDeviceName` — already computed, never surfaced |
| `RemoteAddress` | `string?` | new field on `PhonePresenceStatus`, single-phone only |
| `SummaryTooltip` | `string` | presence + host link + latency, composed and localized |

`RemoteAddress` follows the rule `FirstDeviceName` already follows and for the same
reason, stated at `PhonePresence.cs:75-76`: it is offered only when exactly one phone is
attached, because **naming one of several is arbitrary and reads as though it is the only
one.**

### `IsPhoneAttached` does not change

**ADDITIVE ONLY, AND THIS IS THE LOAD-BEARING CONSTRAINT.** RemEx-7zzw exists because
five status indicators drifted apart and the app contradicted itself screen to screen —
`PhonePresenceMonitor.cs:17-21` records that a user reads that as a bug in the *new*
indicator rather than a stale one in the old three. Home (`HomeView.axaml:446-448`),
Settings (`SettingsView.axaml:88`), Canvas (`CanvasView.axaml:305`) and the tray flyout
(`TrayFlyoutWindow.axaml:52`) keep binding `IsPhoneAttached` untouched. `State` is a
refinement available to whoever wants it, not a replacement.

A test pins the invariant so the two cannot diverge — see §9.

### Composing `SummaryTooltip`

Reuses the existing localized `PresenceText` as its first line, then appends the host-link
detail. It must recompute on the same triggers `PresenceText` does: the 3-second
`DispatcherTimer` tick and `LocalizationService.PropertyChanged`. `PhonePresenceMonitor`
does not currently observe `ConnectionViewModel`, so the latency portion is composed at
bind time in the view rather than inside the monitor — the monitor owns presence, the view
owns the join.

---

## 3. The control

`ShellView.axaml:238` — the `Border` becomes a `Button`.

```
Button .status-card   ToolTip.Tip ← Presence.SummaryTooltip
                      AutomationProperties.Name ← Presence.PresenceAccessibleName
└── Flyout (§4)
└── Grid ColumnDefinitions="64,*"
    ├── [0] Panel
    │   ├── Path {StaticResource IconPhone}   22×22
    │   └── Ellipse .status-dot   10×10, badged bottom-right
    └── [1] Grid  IsVisible ← IsDrawerOpen
        ├── StackPanel — the three existing text lines, unchanged
        └── Path {StaticResource IconChevron}  ← reads as actionable
```

Two additions to `App.axaml`'s icon block: `IconPhone` (Material `cellphone`) and
`IconChevron`. Both `StreamGeometry`, matching the 23 already there and moving with
RemEx-me22's direction of travel away from emoji glyphs.

**`ToolTip.Tip` is the highest-value line in this change.** It is what makes the collapsed
drawer — the state the whole complaint is about — carry the full picture.

---

## 4. The flyout

A `Border Classes="glass-card"`, `MaxWidth="320"`, opened by click.

| Region | Content |
|---|---|
| Header | status dot · `Presence.PresenceText` · `Presence.DeviceName` when non-null |
| Details | Address (`Presence.RemoteAddress`) · Host link (`Connection.StatusText`) · Latency (`Connection.LatencyText`) · Host runtime (`Connection.HostRuntimeSummary`) |
| Actions | state-dependent, below |

Each detail row hides when its binding is null or empty, so the flyout shrinks rather than
showing labelled blanks.

`HostRuntimeSummary` is read straight off `ConnectionViewModel`. `SettingsViewModel`'s
`HostRuntimeText` is a plain assignment from it (`SettingsViewModel.cs:1157`), so the
flyout binds the source directly and needs no new `ShellViewModel` plumbing.

### Action matrix

| `State` | Actions |
|---|---|
| `HostDown` | **Diagnostics** (`NavigateToDiagnosticLogsCommand`) · **Reconnect** (`Connection.ConnectCommand`) |
| `NoPhone` | **Pair a phone** (`NavigateToSettingsCommand`, existing key `Home_PairPhoneButton`) |
| `PhoneAttached` | **Settings** (`NavigateToSettingsCommand`, existing key `Nav_Settings`) |

`HostDown` is the only state offering two actions, because it is the only one where the
user needs to both *understand* and *act* — the other two are one obvious next step each.

### No Disconnect button

Deliberate. It is destructive-adjacent, it already exists on two other surfaces
(`SettingsView.axaml:112`, `HomeView.axaml:466`), and a flyout dismissed by clicking
anywhere outside it is the wrong home for an action you cannot undo without a reconnect
round-trip.

---

## 5. Theming

**No new theme resource keys.** The flyout uses only `DynamicResource` brushes already
defined in all four dictionaries: `CardBackgroundBrush`, `CardBorderBrush`,
`GlassBaseDarkBrush`, `TextPrimaryBrush`, `TextSecondaryBrush`, `TextMutedBrush`,
`AccentPrimaryBrush`, `SystemSuccessBrush`, `SystemErrorBrush`, `SystemWarningBrush`.

Verification is still required across **CyberNOC, Monolith, SolarFlare, BaseDarkGlass** —
`AGENTS.md:139-141` notes each has distinct contrast ratios and background treatments, and
a flyout floating over page content is exactly where a background treatment can fail. The
`glass-card` class is used on every settings card and is the safest available starting
point.

These four themes are **PC-only**. Nothing in this spec touches Android, so no M3 axis
applies.

---

## 6. Localization

Nine `.resx` files under `remex.desktop/Localization/` (`en`, `es`, `fr`, `hi`, `id`,
`pl`, `pt-BR`, `tr`, `uk`). New keys, all nine:

| Key | Use |
|---|---|
| `Shell_StatusTooltipFormat` | composes presence + host link + latency |
| `Shell_StatusFlyoutAddress` | detail row label |
| `Shell_StatusFlyoutHostLink` | detail row label |
| `Shell_StatusFlyoutLatency` | detail row label |
| `Shell_StatusOpenDiagnostics` | `HostDown` action |
| `A11y_ConnectionStatusButton` | names the button itself |

Reused unchanged: `Home_PairPhoneButton`, `Nav_Settings`, `Btn_Connect`,
`Shell_PhoneConnectedNamed`, `Shell_PhoneConnectedUnnamed`,
`Shell_PhonesConnectedSeveral`, `Shell_NoPhoneConnected`, `Shell_PhonePresenceHostDown`,
`A11y_PhonePresenceAttached`, `A11y_PhonePresenceNone`.

---

## 7. Accessibility

`AutomationProperties.Name` moves onto the `Button` and **comes off the dot**.
`PhonePresenceMonitor.cs:62-68` documents why, from RemEx-x12a: a dot's accessible name
must identify the control and must not change minute to minute, and an earlier pass bound
these dots to the presence message itself — which never names the element, varies with the
device name and count, and on the shell was announced twice because it is byte-identical
to the label beside it. Leaving the name on the dot *and* adding one to the button
reintroduces exactly that double announcement.

The flyout is keyboard-reachable because the control is now a real `Button`; it was not
focusable at all as a `Border`.

---

## 8. The trust card's opaque device names

**Bead `RemEx-9me77`.** Reported as: the File-Sharing Trust card shows `07ca4e9d5383…`,
which means nothing to anyone, while the Paired Devices card directly above it shows a
name the user chose.

### Root cause

- `SettingsView.axaml:398` binds `FileTrustDeviceItem.ShortId`.
- `SettingsViewModel.cs:1407` — `ShortId => ClientId.Length > 12 ? ClientId[..12] + "…" : ClientId`. A raw truncation that never consults the name map.
- `remex.desktop/Services/PairedDeviceDisplayName.Resolve(deviceId, names)` is the helper the Paired Devices card already uses.

**The fix already exists and was never wired in.** `PairedDeviceDisplayName.cs:39-41`
cites this very list as the bad example it was written to avoid:

> the existing File-Sharing Trust list already renders raw ShortIds and is described as
> opaque, and a blank row is strictly worse than opaque.

### Design

1. `FileTrustDeviceItem` gains `DisplayName`, resolved through
   `PairedDeviceDisplayName.Resolve` using the name-override map `SettingsViewModel`
   already loads for the paired list. `SettingsView.axaml:398` binds it.
2. `ShortId` **is kept**, demoted to a small monospace second line. An id is comparable
   against what the phone shows, and `Resolve`'s documented contract is that it never
   returns blank — falling back to the id is the intended behaviour for a trust entry with
   no matching paired device.
3. `SettingsViewModel.cs:1020` — the revoke confirmation — switches to `DisplayName`.
4. `remex.desktop.tests/ViewModels/DestructiveActionFailClosedTests.cs:586` currently
   asserts the dialog body contains `device.ShortId` and updates with it. **The property
   it pins is unchanged** — its own comment says "the user must be told WHICH device they
   are about to cut off" — only the string that satisfies it moves.
5. Applying a rename must refresh the trust list. Otherwise the two cards on the same page
   disagree until the user hits Refresh, which is a worse state than the one being fixed.

---

## 9. Testing

Unit tests, `remex.desktop.tests`. No new integration surface.

| Test | Pins |
|---|---|
| Host-down and no-phone map to **different** `ShellConnectionState` values | The reported defect. Currently indistinguishable. |
| `Evaluate` returns `RemoteAddress` only when exactly one phone is attached | Mirrors the existing `FirstDeviceName` rule; a regression here leaks an arbitrary peer's address. |
| `SummaryTooltip` composes correctly in all three states | The collapsed drawer's only information channel. |
| `IsPhoneAttached` agrees with `State` for every state | **The drift guard.** Without it, the new indicator can contradict the four old ones — the exact failure RemEx-7zzw was filed for. |
| Trust `DisplayName` resolves through the override map, and falls back to the id | RemEx-9me77, plus `Resolve`'s never-blank contract. |
| Revoke confirmation body contains `DisplayName` | Updated `DestructiveActionFailClosedTests`. |

`scripts/verify.ps1` is the completion gate, per `AGENTS.md:96`. Per
`bd remember a-green-test-run-whose-count-did-not-rise-did-not-run-your-test`: **read the
test total, not just the colour** — a green run whose count did not rise did not run the
new tests.

---

## 10. Alternatives considered and rejected

**Navigate straight to Settings on click** (no flyout). Simpler, and it needs no new popup
surface to verify across four themes. Rejected because it does not fix the collapsed state
— the user still has to leave the page they are on to learn whether their phone is
attached, which is the complaint one level removed.

**Inline expanding panel** inside the drawer. No popup at all. Rejected because it is
useful only with the drawer already open, so the collapsed state — the reported defect —
stays exactly as weak as it is now.

**Rebuild the Home page footer in the same pass.** Connor raised turning
`HomeView.axaml:439-468` into a sliding drawer, possibly linked to the forthcoming
clipboard screen. Deliberately not in scope: it is worth designing alongside the screen it
would point at. Recorded on `RemEx-drhfx`.

---

## 11. Explicitly out of scope

**PC→phone file browsing (`RemEx-xt0af`).** Reported in the same session: the 🖴 button on
the File Transfer screen fails with "A paired client identity is required to browse
volumes." Diagnosed in full on that bead — the button asks the *PC* to enumerate the *PC's*
drives, and fails because the desktop UI never sets `ClientId` on any outbound message
(`PingPongHandler.cs:212`) and loopback connections are frozen at no identity by RemEx-4215
(`PingPongHandler.cs:215`, pinned by `LoopbackIdentityClaimTests`).

Connor chose the full feature over three smaller fixes. **Until it lands the button keeps
showing that error**, and deciding whether to hide it in the interim is step one of that
epic, not of this one.
