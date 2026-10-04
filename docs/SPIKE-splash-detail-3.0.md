# Spike: the splash lost its detail (RemEx 3.0)

Bead: RemEx-pp4cm.11. Date: 2026-10-03.

Connor: "Some of the level of detail in the opening splash screen went away. It looked very
aesthetically appealing before but it lost part of its oomph", on both the PC and the phone. And:
"The Live Handshake one made the other ones look so lame in comparison."

## Findings (cause -> fix)

### What did not change

The Live Handshake renderers, the shared field shader (`live_handshake_field.sksl`/`.agsl`) and both
directors are byte-for-byte what landed on 2026-09-26 (`git log` on `remex.branding/LiveHandshake*`,
`ui/splash/LiveHandshakeSplash.kt`, `Shaders/`: nothing after 77724a06/627d48a2 except b42abf40's
haptic call swap). So the detail was not removed from the drawing. It was removed from what a
normal open gets to show.

Other suspects checked and cleared, with evidence:
- **Reduced motion from the OS (bb5a401d).** Connor's PC reports `SPI_GETCLIENTAREAANIMATION = True`
  and his profile has `isReducedMotion: false`, so the PC splash is not in its still mode.
- **Typography (f310dd19 "real Victor Mono", 8a5a30bd Bungee Shade/Orbitron).** The PC splash text
  went from the fallback face to Victor Mono Bold on 09-30. Rendered both on the CPU
  (`BrandAssetGen splash LiveHandshake`, old font file vs new): the wordmark is bolder now, not
  thinner. The phone's Live Handshake uses the theme's title family and `FontFamily.Monospace`, so the
  font swap does not touch it.
- **Palette / seed / contrast (0ed8a92e first-run colours, 798a0193 contrast detents).** Both only
  change installs that never saved colours, or the slider's stops. Connor's PC sidecar is
  `#C4256E, Vibrant, Dark, -0.14` and his phone `#007F69, vibrant, dark, 0.24`, set by him.
- **Renderer path.** No Avalonia rendering-mode or SkiaSharp change since 09-26; the field still runs
  on the GPU lease.

### Phone: the hand-off now lands before the PC's handshake ack, so the lock-on never plays

Measured on the AVD paired with the real PC, release build, with a one-line hand-off log added for
this (`LiveHandshake: hand-off ...`):

```
hand-off 1.92s origin=Mark ready=0.00 connect=0.00 linked=- failed=0.00 targetAnswer=0.00
```

and the recorded frames of the build before the fix show the reticle hunting, then the portal opening
from the mark with no lock, while the console said "DESKTOP-NH32DFR is not answering" and then
"DESKTOP-NH32DFR answered in 414 ms".

- **Cause 1, readiness is instant now.** `ready=0.00`: the destination under the overlay is the light
  Home tab (0ed8a92e five tabs, c8db9741 Home content) instead of the old dashboard, so it composes
  before the splash clock starts. The director's wait is measured from readiness.
- **Cause 2, a failure is stamped at t = 0 on every normal open.** `failed=0.00` while the probe had
  already heard the PC: the native connect reports "disconnected" while it replaces the socket
  (`RemexClientManager.onConnectionStateChanged`, the RemEx-bz9t comment), which clears `isConnecting`
  with nothing connected, and `LiveHandshakeSignals.observeConnectAttempts` reads that as the attempt
  failing.
- Together the director took its "failed" arm, `max(FLOOR, readyAt, failedAt)` = FLOOR = 1.4 s. On a
  normal open the host's ack lands after that (on the AVD the socket alone took ~1.3-1.9 s), so the
  whole lock-on beat (reticle snap, solid beam, packets, lock ripple, haptic, the portal opening out of
  the PC, the console's "Linked to ...") was skipped. With the old dashboard, readiness itself was late
  enough to cover the ack; that part is inferred, the rest is measured.
- **Fix (both directors, shared vectors):** a target that has answered the probe is awake and
  mid-handshake, so the splash waits for its lock up to CAP (3.0 s), and this arm comes before the
  failure arm. A real failure against an awake PC now costs at most the wait to CAP. The console no
  longer says "not answering" about a PC that answered the probe. FLOOR goes 1.4 s -> 1.9 s (below).
  Verified on the AVD after the fix: `hand-off 3.02s ... targetAnswer=0.00`, three pulses, the
  answer and its round trip, no contradiction. (This AVD's pairing is not accepted by the PC, so the
  lock itself cannot be shown there; on a paired phone it now has until 3.0 s to land.)

### PC: the open is at its bare minimum, and the films ran unseen

- **Cause 1, the director's floor is the whole show.** With no phone linked at the moment the
  window first renders (the usual case at sign-in: the phone's heartbeat retries every 5 s, logcat
  `backoff 5000ms`), the PC hands off at `max(FLOOR, readyAt)`, and readiness (the embedded host's
  services) is already there by the first frame. At FLOOR 1.4 s the second pulse is born at 1.32 s and
  cut at once, the ghost phones never light, and the console's last line is barely typed: one pulse
  and a portal. **Fix:** FLOOR 1.9 s on both platforms, so the second pulse rolls half the screen and
  the console reads. Pinned by the shared vectors.
- **Cause 2, the fixed films played before the window was visible.** `SplashClock` ran Cosmic Zoom,
  Pong and RemEx Command from attach (only Live Handshake waited for the first frame). The Debug window
  here became visible 2.6 s after launch (3.6 s in the RemEx-8g6n0 measurement), which is most of a
  ~3 s film. **Fix:** one clock rule for every style: wait for the first rendered frame, clamp steps.

## Part 3: the other styles

Every non-Live-Handshake style now plays in a world of its own, drawn by one new shared shader,
`splash_film_field.sksl` / `.agsl` (byte-identical, parity tests on both sides), under the style's
existing vectors:

| Style | World | Build-up (`uDrive`) | Beat |
|---|---|---|---|
| RemEx Command ("Original Scan") | command deck: perspective grid floor under a glowing horizon, two depths of falling data columns, scanlines, a rolling bar | the floor and rain speed up pass by pass | each sweep pass ends on a shockwave; the session coming up is the biggest, from the lockup |
| Cosmic Zoom | deep space: seed-tinted nebula, three star layers flying out of the mark | the stars stretch into warp streaks toward the strike | the strike, with a chroma-split crest through the field |
| Pong ("Signal Pong") | phosphor CRT court: glowing rails, dashed net, aperture grid, scanlines | the court heats up over the rally | every paddle contact, then the mark assembling |

- **Colour.** Phone: `FilmPalette` from the M3 scheme via `LiveHandshakePalette` (dark scheme: its
  surfaces and primary; light scheme: its inverse roles as a night ground; amber accent with the
  contrast fallback; tertiary as the second light). Monochrome reads as grey light with the amber
  accent. PC: the seed palette `SplashBrand` already carries, with a dark ground mixed from the
  primary when the palette is light. Every film now fades out to the app's own background, so the
  hand-off into the app is one colour.
- **Reduced motion.** Each film shows a designed still frame (its world frozen at a hero moment with
  the settled mark and wordmark) for 1.35 s, then hands over; a tap skips. The old phone frame finished
  at once and read as a blank; the PC films ignored reduced motion entirely.
- **Fallbacks.** PC: a raster lease draws the old gradient (runtime shaders cost ~200 ms a frame
  there). Phone: minSdk is 34, so AGSL is always available and no Canvas fallback is needed.
- **Fixed on the way.** Cosmic Zoom's chromatic bloom was centred in raw px (`cy - 30 * restScale`)
  while the mark it rings is in dp, so on a 3x phone it sat below the mark (density trap). Pong's
  finale comet was 14/5 raw px. Both now scale by density.

## First-frame cost

Nothing new runs before the first frame on either platform. The PC warms the film shader's compile
off the render path, like Live Handshake's. On the phone the shader compiles when the film composes,
as Live Handshake's does. FLOOR only lengthens the end of the splash, by 0.5 s, and a tap still skips.
