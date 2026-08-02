# Spike: host audio streaming to the phone (RemEx-x4mp)

**Status:** investigation and spec. No implementation under this bead.

**The gap:** remote desktop is silent. Video playback, calls and games are one-eyed. Verified
starting state: zero `AudioCapture` / `IAudioClient` / `WASAPI` / `Opus` / `NAudio` hits anywhere in
the repo, and no audio field on `DesktopConfig` or `DesktopMeta`.

---

## 1. Capture

**Windows — WASAPI loopback.** `IAudioClient` in loopback mode on the default render endpoint.
Mature, no driver, no elevation beyond what the agent already has. Two things that bite:

- **Loopback is silent when nothing is playing.** WASAPI delivers no packets rather than silence, so
  a naive pump either spins or stalls. The stream needs explicit silence insertion, or the phone's
  clock drifts against a stream that simply stopped.
- **Endpoint changes mid-session** (headphones plugged in, a device disabled) invalidate the client.
  This is the same reinit-storm shape the video path already hit: see `DuplicationReinitThrottle`
  (RemEx-crk). Whatever gets written here must be throttled the same way, or a flapping USB DAC
  produces a reinit loop.

**Linux — PipeWire.** Capture a monitor source. **Parity is mandatory — CachyOS is a first-class
target** — and PipeWire is already a dependency of the Linux capture path, so this adds a use rather
than a dependency. The `--doctor` prerequisite checks are the natural home for "no monitor source
found".

**Verdict: both feasible, comparable effort, no blocking unknown.**

## 2. Codec

**Opus, and the AOT constraint decides the binding.** `Remex.Core` is compiled NativeAOT for Android
(`libRemexCore.so`), so anything in the shared assembly must be reflection-free and trimming-safe.

- **Concentus** — pure managed Opus. AOT-safe by construction, no native artefact to ship per ABI.
  Slower than libopus, but the target bitrate here is 64–96 kbps stereo, which is not where a managed
  decoder struggles.
- **P/Invoke libopus** — faster, but adds a native `.so` per Android ABI and a `.dll` for Windows, to
  the build, the installer and the AOT link step.

**Recommend Concentus for v1**, on the grounds that the encode side runs on the PC where headroom is
plentiful, and the decode side on Android is the one that must not regress battery — and measuring
that is cheaper than un-shipping a native dependency later. Revisit only if measurement says so.

**Do not put the codec in `Remex.Core` without checking the AOT link.** A managed Opus implementation
is large; the NativeAOT size and link-time cost want measuring before it becomes load-bearing.

## 3. Transport — this is the interesting decision

The existing framing is `DesktopFrameEnvelope`: magic `RDXF`, `Version = 1`, `HeaderSize = 28`,
carrying `streamSerial`, `sequence`, a `DesktopCodecKind` byte at offset 5, and `DesktopFrameFlags`.
`DesktopCodecKind` today is `{ Mjpeg, H264 }`.

**Recommend interleaving on the existing `/ws/desktop` socket** as a second envelope kind, not a
third socket:

- The header already has a codec byte with unused values. Adding `Opus` is additive and costs no
  header space, no version bump of the envelope, and no new handshake.
- **A third socket would need its own pairing, its own reconnect, and its own `streamSerial` reset
  semantics**, and would then need those semantics kept *in step* with the video socket's — two
  independent reconnect state machines that must agree about which generation of the stream they are
  on. That is a correctness liability far larger than the multiplexing it avoids.
- `streamSerial` already exists precisely to invalidate stale frames across a stream restart. Audio
  gets that for free by sharing it, and — critically — audio and video are then guaranteed to agree
  about what generation they belong to.

**The cost to weigh honestly:** a large keyframe now delays audio behind it on the same socket. A
frame's worth of head-of-line blocking is tens of milliseconds, which is inside the sync budget
below, but it is not zero and it gets worse on a bad link. If measurement shows it dominating,
the escape hatch is a priority interleave on the sender rather than a second socket.

## 4. Sync

**Budget:** audio leading video is far more objectionable than lagging. Target **audio within
−30 ms to +80 ms of video** — the range broadcast practice treats as imperceptible — and prefer to
land late rather than early.

**Do the timestamps exist?** The envelope carries `sequence` and `streamSerial`, which order frames
but do not timestamp them against a wall clock. **So no — a presentation timestamp has to be added.**
The header has a fixed 28-byte size, so this is either a flags-gated header extension or a field
inside the audio payload. **Put it in the audio payload for v1**: it changes no existing header, and
video already displays on arrival, so only audio needs to know when it belongs.

## 5. Android playback

`AudioTrack` in `MODE_STREAM` with `PERFORMANCE_MODE_LOW_LATENCY`, at the device's native sample
rate and buffer size from `AudioManager` `PROPERTY_OUTPUT_SAMPLE_RATE` / `PROPERTY_OUTPUT_FRAMES_PER_BUFFER`
— resampling on the phone costs latency and battery for nothing.

**The jitter buffer is where this feature succeeds or fails.** Too small and it underruns on any
network hiccup; too large and A/V sync drifts past the §4 budget. Adaptive, starting around 60 ms.

## 6. Negotiation

Additive: `SupportsAudioStreaming` on `HostCapabilities`, plus a `DesktopConfig` field for
enable/bitrate. **No `protocolVersion` bump** — an older client that never asks simply gets no audio,
and an older host that never advertises makes the phone hide the control.

**The routing trap applies and must not be skipped.** Audio arrives as binary frames on `/ws/desktop`,
which the client already reads, so it does *not* need `OnNativeMessageReceived` routing. But
`SupportsAudioStreaming` reaching Kotlin does — and per CLAUDE.md an unrouted client-bound type is
**silently dropped with no error on either side**, which is exactly how RemEx-y6x6 bricked v3 file
transfer. Note also that the display catalog was found during RemEx-jqpx to stop at the native
library and never reach Kotlin, so "capability flows to the phone" is not a safe assumption in this
codebase — verify on a device.

---

## Go / no-go

**GO, in phases, with the caveat that this is the largest single feature left in the product.**

Nothing in the investigation surfaced a blocking unknown: capture is mature on both platforms, the
codec choice is constrained-but-decided by NativeAOT, and the transport question has a clear answer
that reuses machinery already built for video.

**Effort estimate:** roughly 4–6 focused sessions, dominated by phases 3 and 4 rather than by capture.

| Phase | Content | Notes |
|---|---|---|
| 1 | WASAPI loopback capture → Opus encode → discard | Provable off-device with a file dump; no protocol changes |
| 2 | PipeWire capture to the same interface | Parity gate; do NOT let phase 1 ship alone |
| 3 | Envelope kind, presentation timestamp, interleave on `/ws/desktop` | The correctness-critical phase |
| 4 | `AudioTrack` playback + adaptive jitter buffer | Where the perceived quality actually lives |
| 5 | Capability negotiation, UI toggle, 9-locale strings | Additive; verify the flag reaches Kotlin on a device |

**Filed:** `RemEx-idx7` (phases 1-2, capture and encode) and `RemEx-jel2` (phases 3-5, transport, playback and negotiation).

**Phase 2 is a gate, not a follow-up.** Shipping Windows-only audio and filing Linux as "next" is how
a first-class target becomes a second-class one.

---

**Note on the output path:** this bead names `docs/superpowers/specs/` as the precedent home. **That
directory is gitignored** — a spec written there is never committed, and the bead would close with no
artefact in the repo. Recorded on RemEx-w6fr, which hit the same thing. This lands in `docs/`.
