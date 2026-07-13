# RD Stream — Quality/Data-Saving Slider + Surface-Reconfigure Fix

**Status:** PLANNED · 2026-07-12 · build in a **fresh session**.
**Before implementing:** restart with `$env:ECC_GATEGUARD='off'; claude` (large multi-file change, research already done — GateGuard would otherwise block each edit).
**Scope note:** Touches the RD stream (security-adjacent) but NOT pairing/cert/protocol-auth. Wire format is expected to stay **additive (no `protocolVersion` bump)** — the client already sends `quality`/`fps`/`scale`; presets are just client-side bundles of those three values.

---

## 1. How we got here (diagnosis summary)

Live remote-desktop stream showed macroblocking + a "duplicate cursor." Systematic debugging across two locations isolated **three independent problems**, not one:

| # | Problem | Where | Trigger | Status |
|---|---------|-------|---------|--------|
| A | **Black screen + zoom on preset switch** (decoder surface doesn't re-clamp to new encoded size; recovers only after a monitor switch forces a full re-bootstrap) | **Client** | Any mid-session change to resolution **scale** | **ROOT CAUSE — fix first** |
| B | **Cursor duplication / misplacement** | **Client** | Only when A's clamp is wrong (cursor overlay drawn into the wrong content rect) | Downstream of A |
| C | **High-motion pixelation** | **Client decoder throughput** | UNLIMITED preset generating 300+fps that the phone's H.264 decoder can't ingest | Mitigated by FPS cap (slider) |

Key confirming evidence:
- **Work (poor uplink):** host `Avg Send` ballooned to ~5,800 ms, thousands of frames dropped → bandwidth saturation from 60 Mbps UNLIMITED over a weak link.
- **Home (2 Gbps LAN):** host encodes in ~3 ms, `Avg Send` 0.0 ms, **0 dropped at 308 fps** — host is pristine; the phone decoder is the ceiling.
- **User's controlled test (the clincher):** `scale 100% + quality 75%` = clean, recovers fast even >200fps. `quality 100% + scale 75%` = zoom + cursor-dup + slow recovery. → The fault is a **surface-resize bug tied to resolution scale**, not a quality/scale tradeoff. Lowering **quality** only shrinks bits/frame (no dimension change → no reconfigure). Lowering **scale** changes encoded dimensions → trips the broken mid-session reconfigure.

`db8cd85` fixed encoded-dimension surface sizing for the **first connect only**; the **mid-session scale-change path** was never covered.

---

## 2. Design — the new stream-quality control

Replace the 4 discrete presets + "Unlimited" with **one continuous slider**:

- **Left label:** "Image Quality" · **Right label:** "Data Saving".
- **Far left:** FPS **120**, quality **100%**, resolution scale **100%**.
- Sliding right **gradually** lowers the three values (no discrete stops).
- **Ordering (from the diagnosis): drop FPS + quality first; touch resolution scale LAST** (only in the final ~20–25% of travel), because scale is the buggy/slow-recovery knob and quality-reduction looks nearly identical.
- **Remove "Unlimited"** entirely (a phone can't display >120 Hz or decode 300+fps; it only causes C).
- **Live readout** of the current `fps / quality% / scale%` under the bar.
- **"Custom" button** beneath the slider:
  - Tap → the three values become **editable fields**; on first typed value the **slider thumb disappears** and the config **pins to the typed values**.
  - Tap again → the thumb **returns**, fields **gray out** (read-only), config snaps back to the slider curve.
  - Values are always visible in both modes.

### Suggested slider→values curve (tunable during build)
Let `s ∈ [0.0 (quality) … 1.0 (data saving)]`:
- `fps    = round(120 − 90·s)`      → 120 … 30
- `quality= round(100 − 45·s)`      → 100 … 55  (user found ~75% ≈ visually identical)
- `scale  = s ≤ 0.70 ? 1.00 : 1.00 − ((s−0.70)/0.30)·0.40`  → 1.00 … 0.60 (only the last 30% of travel)

These are starting points — verify feel on-device and adjust.

---

## 3. Implementation phases

### Phase 1 — Fix the mid-session surface reconfigure (LINCHPIN, do first)
Symptom to kill: changing scale mid-session → black/zoomed until a monitor switch.
- Instrument first: log `encodedWidth/Height` received vs. the actual decoder/`TextureView` surface size on every desktop-meta/bootstrap update.
- Likely fix locus: when new encoded dimensions arrive mid-stream, the H.264 `TextureView`/`Surface` must be resized and the decoder reconfigured **without** requiring a target-switch re-bootstrap. Compare against the working first-connect path and the working target-switch path (which already recovers) — reuse that recovery for a plain scale change.
- Files: `RemoteDesktopViewModel.kt` (encoded-dim adoption, ~L561–574; guards against teardown loops), `H264StreamDecoder.kt` (`maybeReconfigureForNewSps`, `KEY_MAX_WIDTH/HEIGHT`, adaptive playback ~L209–213), `RemoteDesktopScreen.kt` (`ContentRect` ~L126–128, TextureView box sizing).
- **Exit criteria:** switching scale mid-session updates the surface in place — no black frame, no zoom, no monitor-switch needed.

### Phase 2 — Verify cursor overlay tracks the corrected content rect (B)
- Once A is fixed, the cursor overlay should map onto the correct `ContentRect` → single cursor. Confirm the overlay position math uses the encoded/scaled content rect consistently (not raw screen dims). Files: `RemoteDesktopScreen.kt` cursor overlay draw; cursor flows in `RemoteDesktopViewModel.kt` (~L168–201).
- **Exit criteria:** exactly one cursor at every scale value.

### Phase 3 — Continuous slider UI + view-model
- New Compose slider component + live value readout + Custom toggle (behavior in §2).
- View-model: replace preset-bundle application with a continuous `sliderPosition → (fps, quality, scale)` mapper; `CUSTOM` mode pins typed values. Files: `RemoteDesktopViewModel.kt` (`DesktopPreset` enum + `DESKTOP_PRESET_BUNDLES` ~L82–112, `applyDesktopPreset` ~L776, `selectCustomPreset` ~L793), the RD settings sheet in `RemoteDesktopScreen.kt`, `SettingsManager.kt` persistence (store slider position + custom values instead of a preset id).

### Phase 4 — Retire "Unlimited"; migration + localization
- Remove `UNLIMITED` from the preset enum / any host-side handling; migrate persisted `UNLIMITED`/preset ids to a slider position.
- Localize new strings ("Image Quality", "Data Saving", "Custom", value labels) across all Android locales (values + values-es/fr/hi/in/pl/pt-rBR/tr/uk and any others present). Check desktop-side preset UI for parity if it mirrors these presets.

### Phase 5 — Tests + verification
- Unit: slider-position → (fps, quality, scale) mapping (incl. scale-last curve); Custom pin/unpin state; decoder reconfigure on encoded-dim change.
- Manual (see §5). Theme-check the new control across **CyberNOC, Monolith, SolarFlare, BaseDarkGlass**. <--- ignore this because this is on android, not on the desktop. The stream quality is determined by what the client (android) requests, so we should also remove the Remote Desktop section of the settings in the desktop app.
- Update `CHANGELOG.md` (Changed: RD quality control; Fixed: mid-session reconfigure black/zoom + cursor dup).

---

## 4. Constraints / gotchas
- **NativeAOT:** any change in `Remex.Core` must stay reflection-free + source-gen JSON.
- **No `ConfigureAwait(false)`** anywhere.
- **Localization mandatory** for all new user-facing strings.
- **Protocol:** keep additive; only bump `protocolVersion` if you change the wire meaning of quality/fps/scale (you shouldn't).
- **Theme-safe** across all 4 themes.
- **Cross-platform:** if the desktop app exposes the same presets, keep parity or file a follow-up.

## 5. Verification (must pass before "done")
1. Drag a window in fast circles at several slider positions — pixelation acceptable and **recovers quickly**; far-left (120/100/100) is near-clean.
2. Move the slider (and switch to/from Custom) mid-stream — **no black screen, no zoom snap, no monitor-switch required**.
3. Exactly **one** cursor at every scale value.
4. All four themes render the slider + Custom control correctly.

## 6. Beads
- (A) P1 bug — mid-session surface reconfigure. **RemEx-x3eb** ← start here (linchpin)
- (B) P2 bug — cursor duplication (downstream of A). **RemEx-ctzz**
- (C) P2 feature — continuous quality/data-saving slider + custom. **RemEx-3o42**

Build order: **x3eb → ctzz (verify) → 3o42**. B and C both depend on A being fixed first.
