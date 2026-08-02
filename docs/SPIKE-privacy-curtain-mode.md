# Spike: privacy / curtain mode for remote desktop (RemEx-w6fr)

**Status:** investigation and feasibility verdict. Nothing implemented under this bead.

**The ask:** while the phone is streaming the PC, anyone standing at the PC can watch. A curtain mode
— blank or lock the local display for the duration of the session — is a standard expectation in
this category.

**Starting state:** zero hits repo-wide for privacy/blank/blackout/curtain. `MonitorOff` exists as a
power command but is not wired to remote desktop.

---

## The central question is already answered, in this repo

**Does DXGI Desktop Duplication keep delivering frames when the display is powered off?**

**No — and RemEx already had to build machinery because of it.**
`remex.agent/Services/ScreenCapture/WindowsDisplayPowerMonitor.cs` exists specifically so that "the
capture loop can pause entirely while the display is powered off, rather than poking a powered-off
Desktop Duplication output." It is described as defence-in-depth on top of `DuplicationReinitThrottle`
(RemEx-crk), which bounds re-init attempts per backoff window; the power monitor "drives that to zero
while the monitor is asleep" (RemEx-960).

That is not a guess about DXGI behaviour — it is two beads' worth of remediation for the failure this
spike was asked to predict.

**Verdict: `SC_MONITORPOWER 2` is ruled out as a curtain mechanism.** It does not hide the screen
from a bystander while preserving the stream; it kills the stream. Anything built on it would produce
a black phone screen and a confused user, and would fight `WindowsDisplayPowerMonitor` — which is
doing the right thing — for control of the capture loop.

---

## 1. Windows — the black-window approach

With power-off ruled out, the viable Windows mechanism is a fullscreen black topmost window on the
local desktop, excluded from capture.

**It WILL be captured unless excluded.** The exclusion is
`SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE)`. **Verified: this repo calls
`SetWindowDisplayAffinity` nowhere**, so nothing currently honours or tests affinity exclusion, and
whether our DXGI path respects it is an open question that must be answered with a real window before
any UI is designed around it.

Two specific hazards for whoever implements it:

- **`WDA_EXCLUDEFROMCAPTURE` requires Windows 10 2004 or later.** The older `WDA_MONITOR` value
  renders the window black *in the capture* rather than excluding it — which is the exact inverse of
  what is wanted here and will look like it works during a careless test.
- **Excluded-from-capture is enforced by the compositor, not by the app.** If the curtain window
  fails to create, or loses topmost, the desk is uncovered while the stream continues. That is a
  privacy failure, not a cosmetic one, so the curtain must **fail closed**: if the window cannot be
  created or verified, refuse to enter curtain mode rather than entering it visually-only.

## 2. Lock the workstation instead — ruled out

`LockWorkStation` moves the session to the secure desktop, and Desktop Duplication cannot capture the
secure desktop. The stream dies. This is the same class of failure as §0 and can be ruled out without
experiment.

Worth noting the interaction: the session keep-unlocked design spec (in the gitignored `docs/superpowers/specs/`)
covers keeping the session *unlocked*, which is the opposite pressure. A curtain feature must not
quietly undo that work.

## 3. Linux

- **Wayland / PipeWire:** the portal delivers frames from the compositor's scene graph, not from a
  physical scanout, so DPMS-off does not necessarily stop frames the way DXGI does. This is
  compositor-dependent and cannot be settled from a desk — it needs testing on CachyOS specifically,
  under the compositor the user actually runs.
- **X11:** `xset dpms force off` plus a damage-based capture stream has the same "does the source
  survive?" question, with the added problem that any input event typically wakes DPMS immediately —
  so the curtain would lift the moment someone bumps the desk, which is the worst possible failure
  mode for a privacy feature.
- **Both:** a black override-redirect window is the analogue of the Windows approach, and X11 has no
  equivalent of `WDA_EXCLUDEFROMCAPTURE` — the window would be captured. Wayland portals may allow
  excluding a surface, but that is compositor-specific.

**Verdict: Linux is not v1.** Neither mechanism has a portable answer, and the X11 DPMS-wakes-on-input
behaviour is disqualifying on its own.

## 4. Input risk — scope it out honestly

**While the local display is blanked, the local keyboard and mouse stay live.** Someone at the desk
can type into the session they cannot see, and the remote user watches it happen.

Blocking local input requires a filter driver — `BlockInput` is not a solution, since it is
trivially defeated, requires the caller to keep running, and blocks the *remote* injected input too
on some paths. **RemEx does not ship a driver and should not start here.**

So v1 must be honest rather than complete: the curtain hides the screen; it does not lock the desk.
That belongs in the user-facing copy, not in a footnote — a user who believes their keyboard is
disabled will leave the machine unattended.

## 5. UX

- **Toggle** on the phone's remote-desktop toolbar, alongside the existing controls.
- **Consent before blanking, not after.** The PC shows a small on-screen pill *before* the curtain
  drops, stating that the machine is being streamed and is about to blank. Dropping a black screen on
  a bystander with no explanation reads as a crash or a compromise.
- **The pill must survive the curtain** — it is the only remaining local indication that the machine
  is live and streaming. That means the pill is itself a capture-excluded topmost window, i.e. the
  same mechanism as §1 and subject to the same fail-closed rule.
- **Exit path that does not need the phone.** A local key chord must lift the curtain, or a user
  whose phone battery dies is left at a black screen with a live session.

---

## Feasibility verdict

| Platform | Mechanism | Verdict |
|---|---|---|
| Windows | `SC_MONITORPOWER 2` | **Ruled out** — kills capture; the repo already works around this |
| Windows | Lock workstation | **Ruled out** — secure desktop is not capturable |
| Windows | Black topmost + `WDA_EXCLUDEFROMCAPTURE` | **Viable, unverified** — needs a real window; requires Win10 2004+; must fail closed |
| Linux | DPMS / portal / override-redirect | **Not v1** — compositor-dependent, and X11 DPMS wakes on input |
| Both | Block local input | **Out of scope** — needs a driver; say so in the UI |

**Recommendation:** one Windows-only implementation bead, gated on first proving that our DXGI
capture path honours `WDA_EXCLUDEFROMCAPTURE`. If it does not, the feature has no mechanism on any
platform and should be closed rather than approximated.

**Filed:** `RemEx-4wgh` — the gate.
