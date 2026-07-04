# PRD — Post-2.0 Polish Backlog (deferred from the 2.0 ship-day pass)

**Date:** 2026-07-04 · **Author:** Claude (2.0 final polish pass, bead RemEx-87vl)
**Status:** Proposed · **Target:** 2.0.x patch / 2.1

## Context

On 2.0 release day a four-track audit (Compose/M3 animations, splash screens, user-facing
text, UX edge cases) was run across `remex.android` and the PC-side UI. Everything low-risk
and user-perceivable was fixed on the spot (see CHANGELOG 2.0.0). The items below are
**real, verified findings** that were deliberately deferred because they are feature-shaped,
touch navigation/state machinery, or require a coordinated terminology sweep across nine
locale files — too much risk for ship day. File paths and line numbers were verified on
2026-07-04 and may drift.

Priorities: **P1** = first patch release · **P2** = 2.1 · **P3** = opportunistic.

---

## 1. Connection & pairing UX (P1)

### 1.1 Certificate-changed → guided re-pair flow
- **Where:** `remex.android/.../RemexClientManager.kt` (error emission), `ui/screens/ConnectionScreen.kt:~346` (error card), `remex.agent/Services/Security/CertificateService.cs:112` (host-side warning).
- **Issue:** When the PC's certificate fingerprint changes (reinstall, cert regeneration), the host explicitly logs "you must re-pair", but the Android user only sees the raw native error string in the error card. No guidance, no action.
- **Proposed:** Detect the pin-mismatch failure reason in `RemexClientManager`, map it to a localized message ("This PC's security identity has changed. If you reinstalled RemEx on the PC, remove the pairing and pair again.") plus a **Re-pair** button that clears the stored pin (`PinnedHostStore.removePin`) and routes to the pairing screen.
- **Risk:** Medium — touches the only production auth path (see CLAUDE.md High-Risk Areas). Needs explicit sign-off and coordinated testing against a real cert rotation.

### 1.2 Wake PC gives no feedback on failure
- **Where:** `ui/screens/DashboardViewModel.kt:188-207` + `DashboardScreen.kt:~1602`.
- **Issue:** Dashboard "Wake PC" only logs when the MAC is unconfigured or the send fails — haptic fires, nothing else. The RemoteControl screen's equivalent surfaces `_commandStatus` properly.
- **Proposed:** Mirror `RemoteControlViewModel`'s status flow (reuse `rc_failed_mac_not_configured`), show a snackbar on failure/success.

### 1.3 Unknown native pairing results shown verbatim
- **Where:** `ui/screens/PairingScreen.kt:91,140` (`else -> result` branches).
- **Issue:** Unmapped native results reach users as raw protocol text (unlocalized).
- **Proposed:** Map unknown results to a generic localized "Pairing failed — please try again" and keep the raw string in logcat only.

### 1.4 "PC found" snackbar re-fires on rotation
- **Where:** `ui/screens/ConnectionScreen.kt:267-271` — `LaunchedEffect(discoveredHost)` re-runs with retained state after configuration change.
- **Proposed:** One-shot event (clear `discoveredHost` after autofill, or a consumed-flag in the ViewModel).

---

## 2. Splash screens (P2)

### 2.1 Splash skip machinery is dead code
- **Where:** `ui/navigation/AppNavigation.kt:120,174,198`, `MainActivity.kt:29`, `data/SettingsManager.kt:48,188,398`.
- **Issue:** `splashShown` is passed down but never read; `markSplashShown()`/`splashShownFlow` have zero callers; the comment at `AppNavigation.kt:198` ("immediately navigate if already shown") is false. The full ~4s splash replays on every cold launch.
- **Proposed:** Either wire the skip (navigate immediately when `splashShown`) or delete the parameters and the stale comment. Decide the product intent first: is the splash a per-install, per-boot, or per-launch experience?

### 2.2 Rotation mid-splash restarts the whole sequence
- **Where:** `SplashScreen.kt` — all animation state is non-saveable `remember`/`Animatable`.
- **Proposed:** Lock orientation for the splash route, or persist a "started" flag and skip on recreation. Pairs naturally with 2.1.

### 2.3 Android 12+ SplashScreen API integration (double-splash)
- **Where:** `res/values/themes.xml`, `app/build.gradle` — no `androidx.core:core-splashscreen`.
- **Issue:** Cold start shows the OS icon-splash, *then* the 3-4s custom splash; first-frame-to-app can exceed 5s.
- **Proposed:** Add core-splashscreen, set `windowSplashScreenBackground` to the app background so the OS splash hands off seamlessly into the custom one (or gate the custom splash to first-run only — see 2.1).

### 2.4 Splash taglines: hardcoded + inconsistent between styles
- **Where:** `SplashScreen.kt` — RemexCommand draws "COMMAND YOUR PC", CosmicZoom draws "⚡ COMMAND CENTER"; both are canvas-drawn English literals. `splash_app_name`/`splash_tagline` exist translated in 8 locales but are unused by the splash.
- **Proposed:** Keep the wordmark as brand art, but measure the tagline from `stringResource(R.string.splash_tagline)` so localized users get a localized tagline, and pick ONE tagline for both styles. Optionally add `BuildConfig.VERSION_NAME` bottom-center.

### 2.5 "Tap to skip" hint
- **Where:** `SplashScreen.kt:494-496` — tap-anywhere skip exists but is undiscoverable.
- **Proposed:** After ~1.2s fade in a small "Tap to skip" (new localized string × 8 locales) at low alpha, bottom-center.

---

## 3. Material 3 / theming (P2)

### 3.1 Success-color token
- **Where:** `ui/screens/ConnectionScreen.kt:1195,1209` — raw `Color(0xFF4CAF50)` for the "paired" success tint.
- **Issue:** No semantic success token exists in `theme/`; the literal green ignores dynamic color and custom seed palettes.
- **Proposed:** Add an extended-color "success" pair to the theme (M3 extended colors pattern) and migrate the two call sites. A design decision, not a mechanical swap — the green may be intentional brand language.

### 3.2 Navigation fade durations are three different magic numbers
- **Where:** `ui/navigation/AppNavigation.kt:731-732` (Splash, 500ms), `752-753` (Tutorial, 400ms), `849-852` (RemoteDesktop, 300ms — intentionally a pure crossfade per comment).
- **Proposed:** Standardize the plain-fade routes on one duration/easing pair. Note: the splash exit animation is tuned against the 500ms fade — retune together.

### 3.3 Tutorial emoji raw fontSize
- **Where:** `ui/screens/TutorialScreen.kt:394` — `fontSize = 64.sp` literal (decorative).
- **Proposed:** Derive from `displayLarge.copy(fontSize = ...)` when next touching the file.

---

## 4. Text & localization (P2, one coordinated sweep)

### 4.1 host/server/daemon → "PC" terminology sweep
- **Where (Android):** `values/strings.xml` keys at lines ~90,91,156,271,295,460,464,645,658,659,668,669,672 and remaining "host" phrasing; **(Desktop UI):** `remex.desktop/Localization/Strings.resx:149,553,556,738,741,1566,1790,1793,1377,806,550,2339-2444,2357,2360`.
- **Issue:** Product rule says end users read "PC"/"computer", never "host/server/daemon". Both terms currently mix, sometimes in the same screen ("PC Status" next to "HOST ONLINE" — the worst offenders were fixed on ship day).
- **Proposed:** One sweep across the English sources **and all 8 translations per platform** so terminology and translations stay coherent. Suggested rewordings are itemized in the 2026-07-04 text-audit report (items 5-11, 17, 23).

### 4.2 Jargon rewrites for non-technical users
- mDNS/NSD in `values/strings.xml:70` (tutorial), `~372` (FAQ), `Strings.resx` discovery strings → "automatic network discovery".
- "magic packet"/WOL internals (`Strings.resx:93`, `values/strings.xml:253`) → "wake signal".
- Native-layer errors surfaced verbatim (`values/strings.xml:92,527-529,531,532`) → plain "Couldn't reach your PC" phrasing (keep detail in logs).
- Security "What's New" body (`values/strings.xml:349,351`) → keep, but add a plain-English lead sentence.

### 4.3 Dead/orphaned desktop resources
- `Strings.resx:524` Main_Title "Remex — Ping / Pong" + `Main_Status`/`Main_Latency`/`Main_HostWatermark` — orphaned debug-window strings bound nowhere; remove across all 9 resx files.
- `values/strings.xml:497` `settings_ft_coming_soon` key name is stale (value is "Shared folders are active") — rename key at next string touch.

### 4.4 Branding consistency
- "Remex" → "RemEx" in `Strings.resx` `Tray_TooltipDefault` and `remex.desktop/MainWindow.axaml:6` title (Main_Title dies with 4.3).
- `about_pc_host_label`/`About_PCHost` "PC Host" → "Your PC".

---

## 5. Docs / repo hygiene (P3)

- **CLAUDE.md vs reality:** CLAUDE.md describes `remex.agent` as containing `Views/`, `ViewModels/`, `Localization/` — but the live PC-side UI (812 fully-translated resx keys) actually lives under `remex.desktop/`, which CLAUDE.md calls a legacy folder to be phased out. Reconcile the doc with the folder layout (or finish the physical merge) so future agents don't mis-route UI work.
- **CHANGELOG:** consider adding a `docs/CHANGELOG.md` symlink or root stub — project rules reference a root `CHANGELOG.md` that doesn't exist.

---

## Acceptance criteria (per item, when picked up)

1. Fix verified on both Windows and CachyOS where PC-side code is touched (parity rule).
2. Any new user-visible string exists in English + all 8 locales on the touched platform.
3. `./gradlew :app:compileDebugKotlin` and `dotnet build Remex.sln` green; existing tests pass.
4. High-risk areas (pairing, cert pinning, protocol) get explicit user sign-off before merge.
