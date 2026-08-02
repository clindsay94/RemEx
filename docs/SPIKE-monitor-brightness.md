# Spike: monitor brightness control (RemEx-jqpx)

**Status:** investigation only. Nothing here is implemented, and this document exists so the
implementation bead can be scoped from evidence rather than from optimism.

**What RemEx has today:** `MonitorOff` on both platforms
(`WindowsSystemCommandService.cs:103`, `LinuxSystemCommandService.cs:105`, and an Android tile at
`RemexMonitorOffTileService.kt:12`). Brightness: nothing. The only `Brightness` identifiers in the
repo are HSV locals in `ColorPickerPopup.cs:40`, which are unrelated.

---

## The finding that should shape the bead

**There is no single mechanism.** Brightness control splits along *panel type*, not along operating
system, and the two halves share nothing:

| | Windows | Linux |
|---|---|---|
| **Laptop internal panel** | WMI `WmiMonitorBrightnessMethods` | `sysfs` backlight |
| **External monitor over a cable** | DDC/CI via `dxva2.dll` | DDC/CI via `ddcutil` / `i2c-dev` |

A machine can have both at once — a docked laptop is the normal case, not the exotic one — so any
design that picks *one* mechanism per OS is wrong on the most common configuration. The unit that
has a brightness capability is a **display**, not a host.

---

## 1. Windows, external monitors — DDC/CI via `dxva2`

The API is `GetPhysicalMonitorsFromHMONITOR` → `GetMonitorCapabilities` →
`GetMonitorBrightness` / `SetMonitorBrightness` (VCP feature `0x10`), all in `dxva2.dll`.

**The monitors-that-lie problem is the whole risk here**, and it takes three distinct forms that a
naive implementation treats as one:

1. **Capability string omits `0x10` but the monitor honours it anyway.** Trusting
   `GetMonitorCapabilities` alone hides brightness on hardware that supports it.
2. **Capability string advertises `0x10` and the monitor ignores writes.** The call returns success.
   Nothing changes. The user sees a slider that moves and a screen that does not.
3. **`GetMonitorBrightness` returns a stale or fabricated current value**, so a UI that seeds a
   slider from it starts in the wrong place and the first drag jumps.

None of these can be distinguished by return code. **Consequence for the design:** a brightness
control must be *probed*, not *advertised* — write a value, read it back, and only then report the
display as controllable. That probe costs an I²C round trip per monitor and belongs at capability
time, not on the hot path.

Also worth knowing: DDC/CI round trips are slow (tens of milliseconds, occasionally much worse on a
long DisplayPort chain) and some monitors respond badly to rapid writes. A slider must debounce and
must not send one message per pixel of drag.

## 2. Windows, laptop panels — WMI

`WmiMonitorBrightness` (read) and `WmiMonitorBrightnessMethods.WmiSetBrightness` (write), in the
`root\wmi` namespace. Simpler and far more reliable than DDC/CI, but:

- It addresses the *internal* panel only, and typically only one exists.
- `WmiSetBrightness` takes a timeout parameter, and it is real — passing 0 is not "immediate".
- **CLAUDE.md constraint:** anything under `System.Management` is reflection-heavy and must not land
  in `Remex.Core`, which is compiled NativeAOT for Android. This belongs in `remex.agent`, behind
  the existing platform split, like every other WMI use in the repo.

## 3. Linux — `ddcutil` and `sysfs`

**External:** `ddcutil setvcp 10 <n>` / `getvcp 10`. Needs the `i2c-dev` module loaded and access to
`/dev/i2c-*`. On CachyOS the practical answer is the `i2c` group plus a udev rule shipped by the
`ddcutil` package — not `pkexec`, which would prompt.

**Internal:** `/sys/class/backlight/<device>/brightness`, bounded by `max_brightness` in the same
directory. Reading is unprivileged; **writing is not** — the file is root-owned.

**This is the constraint the bead's premise gets wrong, and it matters.** The bead assumes
"repairs run elevated already (the agent is elevated)". That is true on Windows. On Linux the agent
is an **ordinary user process by design** — established while doing RemEx-gpe3, where the elevation
readiness row is `NotApplicable` on Linux for exactly this reason. So sysfs backlight writes are
*not* available for free and need one of:

- a udev rule granting the `video` group write access to `brightness` (the conventional answer, and
  what most desktop environments already rely on), or
- `logind`'s `org.freedesktop.login1.Session.SetBrightness` over D-Bus, which is designed for this
  and needs no rule, but requires an active session, or
- `pkexec`, which prompts — unacceptable for a control the user is operating from the couch.

Recommend `logind` first and a udev rule as the documented fallback. **Do not add a `pkexec` path:**
a remote control that raises a local password prompt on the machine nobody is sitting at is worse
than no feature.

## 4. Capability negotiation — correcting the bead's assumption

The bead says the display catalog "already flows to the phone with a JNI callback". **Verified, and
that is not accurate as stated.** `desktop_display_list` (`RemexMessage.cs:360`) is sent by
`RemoteDesktopHandler.cs:943` and consumed in `remex.core/Native/RemexDesktopClient.cs:773`, which
raises a `DisplayCatalogReceived` **.NET event**. There are no Kotlin references to
`desktop_display_list` or `displayList` anywhere in `app/src/main`.

So the catalog reaches the shared native library and stops there. Whoever implements brightness must
either surface it to Kotlin themselves or confirm an existing path — and per CLAUDE.md, a
client-bound message type that is not routed is **silently dropped with no error on either side**,
which is precisely how RemEx-y6x6 bricked v3 file transfer. Test the round trip on a real device
before believing it works.

**Shape to add**, once that is settled: per-display `supportsBrightness` plus a `[min, max]` range on
the existing catalog entry. Additive optional fields need no `protocolVersion` bump. Do not model
brightness as a host-level capability — see the table above.

## 5. UI sketch

A slider on the Android RemoteControl screen, in the same section as the media row shipped by
RemEx-hulc, which already establishes the pattern for a control that must be visibly unavailable
rather than silently ineffective. Specifically reusable from that work:

- **Gate on connectivity AND capability**, not capability alone. The capability flow is `replay = 1`
  and is never reset on disconnect, so a stale `true` outlives the connection.
- **Do not fire on touch-down** inside a scrolling list.
- Per-display when the catalog has more than one entry; a single unlabelled slider on a three-monitor
  desk is a guess about which screen the user meant.

Debounce writes hard, for the DDC/CI reasons in §1.

---

## Recommendation

Split the implementation **by mechanism, not by platform**, and do the laptop-panel half first: WMI
and `logind` are both reliable, testable, and free of the monitors-that-lie problem. DDC/CI is where
the hardware variability lives and deserves its own bead with a real external monitor attached — it
cannot be verified from a test suite, and a spike cannot tell you whether *your* monitor lies.

**Filed:** `RemEx-6jrt` (laptop panels — WMI and `logind`) and `RemEx-5kgx` (external displays over
DDC/CI, needs real hardware).
