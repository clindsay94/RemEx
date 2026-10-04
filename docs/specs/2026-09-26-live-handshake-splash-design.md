# Live Handshake splash (RemEx 3.0)

Bead: RemEx-8g6n0. Chosen 2026-09-26 over "Signature" and "Shape-morph hand-off".
Branch: `feat/live-handshake-splash`.

Live Handshake becomes the **default** splash on Android and PC. Cosmic Zoom, Pong and RemEx
Command stay selectable.

Motion reference (interactive, all numbers below come from it):
- Published: https://claude.ai/artifact/Qy8NpanuctZmuYuL9Hxsuw
- In repo: `docs/specs/assets/live-handshake-lab.html` (open locally; `#t1.05-trio` freezes a frame,
  scenarios: `trio solo slow asleep first pc`).

## The idea

Every other splash is a fixed-length film. This one is the real startup, drawn as light:

1. **Ignite.** The RemEx mark comes alive (Android: continues the system splash icon, a sheen
   sweeps it; PC: the window outline traces itself on, dots pop, the R springs in).
2. **Pulse.** The mark's amber cursor fires and a shockwave rolls across a dot lattice. The
   lattice is a GPU shader: the wave front *refracts* the dots, lights them, and leaves an afterglow.
   One pulse per real reachability sweep.
3. **Answers.** Your paired PCs (on the PC: your paired phones) sit on an orbit as dim ghosts.
   When one really answers, it ignites, sends its own smaller ripple through the lattice, an echo
   packet flies home to the mark, and it settles inward to a radius set by its **measured round
   trip** (faint range rings labelled 5 ms / 25 ms / 150 ms). Name and latency type in.
4. **Lock-on.** The PC the app is connecting to gets a hunting reticle and a dashed beam. When the
   connection's handshake is acknowledged, the reticle snaps shut on a spring, the beam goes solid,
   packets trade both ways, a lock ripple fires, and the phone gives one CONFIRM haptic tick.
5. **Portal.** The app is ready, so the splash opens *out of that PC*: a circular portal with a
   bright rim grows from the node, the field flies in toward it, and on Android the live dashboard
   underneath refracts through the rim like glass and settles from 1.12x to 1x.

Nothing is simulated. If the PC is asleep the splash does not pretend: no lock-on, the status line
says so, and after a short grace period it opens from the mark into a dashboard that offers Wake.

## Signals (what each beat is wired to)

| Beat | Android source | PC source |
|---|---|---|
| Peers (ghosts) | paired PCs: `KnownHosts` store (identity, nickname, last address/port) | paired phones: `IPairedDeviceSource` via `App.EmbeddedHostServices` |
| Target | the saved PC (`settings.hostFlow`) matched to a known host | a phone already connected (first authenticated session) |
| Answer + RTT | NEW `PeerReachabilityProbe`: TCP connect to each known PC's last address:port, timed | a phone appearing in `IClientSessionSource.Snapshot()` (no RTT; label says "linked") |
| Lock-on | `RemexClientManager.authenticatedConnection` becomes non-null (host acked the reconnect challenge) | same moment as the phone's answer |
| Fail | the heartbeat's connect attempt ends without authentication (`isConnecting` true to false, not authenticated) | n/a |
| Listening (PC only) | n/a | embedded host services published (`App.EmbeddedHostServices != null`); port if resolvable |
| Ready | the dashboard's first frame has composed under the overlay | host services published (same as Listening) |

The probe is phone to PC only (AGENTS.md: connection is always Android to PC, never loopback). It
is not NSD discovery and must not touch `ConnectionViewModel.discoveryJob` (REGRESSION-GUARDS
single in-flight discovery). It opens and immediately closes one TCP socket per PC, timeout
1500 ms, at most 8 in parallel, on `Dispatchers.IO`, once per splash.

## The director (pure, shared rules)

Both platforms implement the same pure function, unit-tested against
`docs/specs/live-handshake-director-vectors.json` (both test suites read that file).

Constants: FLOOR 1.9 s (1.4 s until RemEx-pp4cm.11), GRACE 1.2 s, CAP 3.0 s, LOCK_HOLD 0.7 s, EXIT 0.72 s, FADE_EXIT 0.28 s,
FIRST_PULSE 0.32 s, PULSE_PERIOD 1.0 s, ANSWER_MIN 0.62 s, ANSWER_GAP 0.18 s, LOCK_AFTER 0.5 s,
LINE_GAP 0.32 s.

**Staging (revised 2026-09-26 after the first device test).** On a fast LAN every real event lands
inside ~0.5 s, and the first build flashed through. Events are never faked or reordered, but each
is SHOWN no sooner than it can be read:
- A link also counts as the target answering: the target's *effective* answer time is the earlier
  of its probe answer and `linkedAt`.
- Answers are shown in real order at `shown_i = max(effective_i, ANSWER_MIN, shown_{i-1} +
  ANSWER_GAP)` (ANSWER_MIN is roughly when the first pulse's wave reaches the orbit).
- The lock is shown at `lockShown = max(linkedAt, shown(target) + LOCK_AFTER)`.
- Nothing staged past the hand-off is shown. The director uses shown times for everything visual
  and for the hand-off.

`candidate(known)` where `known` holds only events whose time <= now:
```
if skipAt known:                         c = skipAt
else if readyAt unknown:                 c = CAP
else if peers == 0 or no target:         c = max(FLOOR, readyAt)
else if linkedAt known:                  c = max(lockShown + LOCK_HOLD, FLOOR, readyAt)
else if target answered the probe:       c = CAP        (awake, mid-handshake: wait for the lock)
else if failedAt known:                  c = max(FLOOR, readyAt, failedAt)
else:                                    c = max(FLOOR, min(readyAt + GRACE, CAP))
return min(c, CAP)
```
The "target answered" arm (RemEx-pp4cm.11) comes before the failure arm on purpose: Android's
connect signal reports a failure at t = 0 on a normal open (the native connect reports
"disconnected" while it replaces the socket), and readiness now lands at t = 0 too, so the old rules
handed off at FLOOR before the host's ack and the lock-on never played. See
`docs/SPIKE-splash-detail-3.0.md`.
The hand-off starts on the first frame where `now >= candidate(known)`; after that it is fixed and
later events are ignored. Exit origin: the target node if the lock was SHOWN at or before the
hand-off and the platform opens from nodes (Android), else the mark (always the mark on PC). Pulses
start at FIRST_PULSE + k * PULSE_PERIOD while strictly before the hand-off; none under reduced
motion. Tests step `now` from 0 in 1 ms increments and expect the hand-off within 2 ms of
`expectHandoff`, plus `expectLockShown` and `expectAnswersShown`.

A fast launch now hands off at ~1.9 s (portal done ~2.6 s); asleep ~1.9 s; the cap is 3.0 s,
still under Cosmic Zoom's 3.1 s. Tap/click skips at any moment.

## The console (revised 2026-09-26)

The single status line is replaced by a three-line terminal readout under the wordmark, matching
the terminal-window mark. It narrates the same staged events:

| Line (en) | Due | Android | PC |
|---|---|---|---|
| `Pinging %d PC(s)` / `%d phone(s) paired` | FIRST_PULSE | yes | yes (phones) |
| `No PCs paired yet` / `No phones paired yet` | FIRST_PULSE, when there are none | yes | yes |
| `Listening on port %d` (or `Listening`) | listeningAt (not before 0.4) | | yes |
| `%1$s answered in %2$d ms` | each answer's shown time | yes | |
| `Linked to %1$s` / `%1$s is linked` | lockShown (accent colour) | yes | yes |
| `%1$s is not answering` | min(readyAt + 0.5, hand-off - 0.3), target never linked | yes | |
| `Opening RemEx` | hand-off (accent colour) | yes | yes |

Lines are sorted by due time and each appears at `max(due, previous + LINE_GAP)`. Only three are
visible: newest at full ink (accent when marked), the one above at 55 % alpha, the oldest at 30 %.
A new line types in at 55 chars/s with the amber block cursor, and the stack slides up one line
height over 0.18 s (standard easing) as it arrives; the cursor keeps blinking (0.53 s) on the newest
line. Prefix `›` in accent. Monospace, 12 dp (PC 13.5 dp), line height 19 dp (PC 21 dp), a
left-aligned column `min(W - 48 dp, 320 dp)` wide (PC 420 dp) centred under the wordmark. The
wordmark moves up to 0.785H (PC 0.80H) and the console starts 30 dp (PC 34 dp) below it; keep it
clear of the PC host's version label and skip hint. Node labels grow to 12 dp (PC 14 dp). Reduced
motion: lines appear whole, no slide, same timing. The lab (`solo` scenario) is the reference.

## Choreography (reference values; dp = density-independent px)

- **Layout.** Android: mark centre (W/2, 0.40H), window width 104 dp; orbit ellipse rx 0.37W,
  ry 0.215H. PC: mark centre (W/2, 0.46H), window width 150 dp, orbit rx 0.27W, ry 0.30H. Peer
  angles: base = -pi/2 + 0.62 + hash(names) * 0.5, evenly spaced (+0.4 rad offset when there are
  exactly two). Wordmark "RemEx" at 0.84H (PC 0.86H), status line 26 dp below it.
- **Radius from RTT.** f(rtt) = 0.58 + 0.42 * clamp(log10(rtt) / log10(250)); ghosts sit at f = 1.
  On answer, f springs from 1 to f(rtt).
- **Spring.** s(tau) = 1 - exp(-9 tau) cos(16 tau) (about 16 % overshoot, settled by 0.4 s).
- **Easing.** Emphasized decelerate (0.05, 0.7, 0.1, 1) for reveals; standard (0.2, 0, 0, 1) for
  packets and the fly-in; portal radius (0.45, 0, 0.15, 1).
- **Intro.** Field reveal radius grows over 0.9 s from the mark. Ghosts appear at 0.16 + 0.08 i s,
  fading in over 0.3 s.
- **Pulse.** Ring speed = screen diagonal / 1.25 s. Mark breathes 1 + 0.035 * env, env = sin(pi *
  age / 0.3) for the first 0.3 s of each pulse; cursor glows on env, otherwise blinks at 0.53 s.
- **Answer.** Node fill fades in over 0.12 s; flare decays exp(-5 age); echo packet travels node to
  mark in 0.3 s; RTT suffix types at 40 chars/s with a block cursor; node ripple strength 0.5.
- **Lock.** Beam grows over 0.35 s from connect start (dashed, flowing at 70 dp/s). On lock: reticle
  size 28 to 19 dp and rotation to 0 on the spring; beam flare exp(-4 age); three packets out
  (staggered 0.07 s, 0.24 s each), three back starting 0.2 s later; lock ripple strength 0.85;
  haptic CONFIRM once.
- **Portal.** Starts at 10 dp around the origin and reaches the farthest corner + 40 dp over EXIT
  (0.72 s). Field zoom 1 to 1.5 (standard easing) about the origin; vector layer zooms with it,
  clips to outside the portal and fades out over the first 55 %. Hand-off shockwave strength 1.4.
  Android dashboard lens: refraction 26 dp and chroma 4.5 dp inside a 28 dp rim band; content
  scale 1.12 to 1.0 (emphasized decelerate).
- **Reduced motion.** No pulses, no displacement (uStill = 1), nodes appear in final state, lock is
  instant, the hand-off is a FADE_EXIT crossfade instead of a portal, no haptics, no parallax.
- **Depth.** Android: gyroscope parallax (game rotation vector, up to 9 dp, low-pass) for the
  splash's lifetime only. PC: pointer-position parallax, same range.

## Rendering

- **Field**: `remex.branding/Shaders/live_handshake_field.sksl` and
  `remex.android/app/src/main/res/raw/live_handshake_field.agsl` are one shader and must stay
  byte-identical (parity test). It compiles on SkiaSharp 3.119.4 (verified 2026-09-26). Uniforms:
  `uRes uTime uPx uCenter uBg0 uBg1 uPri uAcc uRings[10] uRingCount uSpeed uIntro uPortal uZoom
  uStill uPar uAlpha`. Rings are (x, y, startTime, strength); pass the 10 most recent started
  rings. Output is premultiplied; inside the portal it is transparent.
- **Vectors** on top: mark (existing brand geometry), range rings, nodes (PC glyph = small rounded
  window 24x17 dp with an accent dot; phone 11x19 dp; tablet 15x20 dp), beam, packets, reticle,
  labels (monospace), wordmark, status line.
- **Android lens**: a second AGSL shader applied as `RenderEffect.createRuntimeShaderEffect` to the
  NavHost layer during the exit only.
- **PC GPU vs raster.** Use the shader when the leased canvas is GPU-backed. On a raster lease the
  shader costs about 200 ms per 540x1170 frame, so fall back to the gradient backdrop plus vectors
  (no field). `BrandRasterizer` renders the full shader on CPU for tests; one frame is fine there.
- **Palette.** Android: from the resolved M3 scheme via `SplashPaletteResolver` (backdrop from
  surface, primary, amber accent with the existing contrast fallback). PC: the existing
  `SplashPaletteResolver.ResolveFromSidecar()` / `SplashBrand.ApplyPalette` path.

## Strings (new, all locales)

Android (en + es fr hi in pl pt-rBR tr uk) and PC (Strings.resx + 8). Sentence case, drawn as
written (the console reads as a terminal log, not a caps banner). Style label "Live Handshake"
follows whatever convention the other style names use on that platform. The console lines are in
the table under "The console"; besides those:

- rtt unit for node labels and range rings: "%1$d ms" (uk uses "мс")
- PC node suffix for a linked phone: "linked"

## Defaults and migration

New installs get `LiveHandshake`. **Everyone who upgrades to 3.0 is moved to `LiveHandshake`
once, whatever style they had** (Connor, 2026-09-26: small user base, the 3.0 splash should be
seen). After that one move, a style picked in the picker sticks. PC: the `CustomizationMigration`
schema-8 arm rewrites any stored `SplashStyle`. Android: the one-time migration flag moves any
stored value.

## Out of scope

- Signature glyphs (per-PC generated art) and pairing verification.
- Any change to how or when the app connects: the splash observes the existing heartbeat
  auto-connect, it does not start its own connection.
- Refracting the PC shell through the portal (the PC portal is a clean transparent hole with the
  rim; refraction is Android-only in v1).
