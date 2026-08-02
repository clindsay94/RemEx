# Measurement: font-loading diet and two pinned dependencies (RemEx-r6dn)

**Status:** measurement and recommendation. **Nothing changed.** The bead's version-change policy
requires asking Connor before bumping either dependency, and "stay pinned, here is why" is an
explicitly valid outcome.

---

## 1. Fonts — the numbers

Eight files, **2326 KB total**, all loaded at startup (`App.axaml:46-51`).

| Font | Size | Share of payload | Where it is used |
|---|---:|---:|---|
| **Nabla-Regular** | **1604 KB** | **69%** | typography picker only |
| victor_mono_bold | 296 KB | 13% | typography picker only |
| BungeeShade-Regular | 293 KB | 13% | **one** use (`TrayFlyoutWindow.axaml:43`) |
| Sixtyfour-Regular | 69 KB | 3% | typography picker only |
| Orbitron ×4 weights | 60 KB | 3% | 5 files — genuine UI use |

**The headline: one picker-only option is 69% of the font payload.** Nabla is a chromatic/variable
display face, which is why it is an order of magnitude larger than everything else. Nabla, Victor
Mono and Sixtyfour together are **1969 KB — 85% of the total — and none of them is used by the UI at
all**; they exist so the user can pick them.

Bungee Shade is the interesting middle case: 293 KB for exactly one glyph run in the tray flyout.

## 2. Fonts — the removal safety question is already answered

The bead says to "check the RemEx-fahn savefile schema before removing anything a saved profile can
name". **Verified, and the graceful fallback the bead asks for already exists:**

- `SystemFontService.TryResolveFont` is the resolution entry point, with `ResolveFontOrDefault`
  layered on it.
- `CustomizationViewModel.ValidateFonts` calls it for both the page-title and body font, on
  construction *and* on every change, and surfaces a **localized** `Custom_FontUnavailable` warning
  naming the fonts that could not be resolved.
- GitNexus shows `TryResolveFont` participating in both `BuildSavefileAsync` and `InitializeAsync`,
  so the savefile round trip already goes through it.

**So a saved profile naming a removed font degrades to the default and tells the user why, today,
with no new code.** That removes the blocker the bead was worried about.

## 3. Fonts — recommendation

**Lazy-load the three picker-only faces; keep Bungee Shade; keep Orbitron.**

- **Nabla, Victor Mono, Sixtyfour → load on first use in the picker.** 1969 KB and 85% of the payload
  for options most users never open. The fallback path above means a profile that names one and
  cannot get it is already handled.
- **Bungee Shade stays.** 293 KB is real, but it has a genuine UI use in the tray flyout, and
  lazy-loading a face the tray needs at startup buys nothing.
- **Orbitron stays.** 60 KB across four weights with five usages is not where the weight is.

**Expected saving: ~1969 KB of startup font loading, 85% of the font payload.** Not measured as a
startup *time* delta — that needs an instrumented run, and the byte figures are strong enough to act
on without it.

**Do not simply delete them from the default set.** They are picker options; deleting makes the
picker offer faces it cannot supply. Lazy-load is the correct shape.

## 4. SkiaSharp — pinned 2.88.9, 3.x current

**Blast radius: one file.** `remex.desktop/Controls/Splash/SkiaSplashControl.cs` is the only
consumer in the repo.

**Recommendation: STAY PINNED for now, and this is not a deferral for lack of effort.** SkiaSharp 3
is a genuine API migration, and the single consumer is a *splash animation* — a component whose
entire value is that it looks right, and which has no automated verification possible. The upgrade
buys nothing a user can perceive and risks the one thing they see first.

The small blast radius cuts both ways: it makes the migration cheap **and** makes the benefit
negligible. Revisit when something else forces it — an Avalonia version that requires Skia 3, or a
security advisory. Neither applies today.

## 5. MaterialColorUtilities — pinned 0.3.0 (pre-1.0)

**Blast radius: one file.** `remex.desktop/Services/DynamicColorGenerator.cs`.

**Recommendation: STAY PINNED, and here the reason is stronger than for Skia.** Newer versions may
change the generated palettes. `DynamicColorGenerator` drives hardware-sync themes, so a palette
change is **a visual diff on every user's existing customization** — the app would silently repaint
itself in slightly different colours after an update, with no changelog entry a user could connect to
what they are seeing.

If it is ever upgraded, it needs a before/after palette comparison across the four themes as part of
the change, not as a follow-up. That is the real cost, and it is much larger than the one-file blast
radius suggests.

**Being pre-1.0 is an argument for pinning, not against it.** A 0.x dependency is permitted to change
its output between minors, which is exactly the risk above.

---

## Summary

| Item | Recommendation | Basis |
|---|---|---|
| Nabla, Victor Mono, Sixtyfour | **Lazy-load** | 1969 KB / 85% of payload, picker-only, fallback already exists |
| Bungee Shade | Keep | 293 KB but a real startup UI use |
| Orbitron | Keep | 60 KB, five usages |
| SkiaSharp 2.88.9 | **Stay pinned** | One consumer, a splash animation, no perceivable benefit |
| MaterialColorUtilities 0.3.0 | **Stay pinned** | Palette change = silent visual diff on saved profiles |

Two of the three dependency questions close as "stay pinned, here is why", which the bead names as a
valid conclusion. The font work is the one with a number worth acting on, and is filed as `RemEx-racq`.
