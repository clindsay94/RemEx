# Code Review Report — Android UI Polish Issues

**Date:** 2026-04-15  
**Reviewer:** Microscopic Code Reviewer  
**Scope:** User-reported UI/UX issues across RemEx Android client  

---

## Issue 1 — Remote Commands: Category Separators Are Visually Weak

```yaml
---
type: Refactor
severity: Medium
breaking_changes: False
target_files:
  - RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/RemoteControlScreen.kt
---
```

### Issue Summary
The three command categories (Session, Power, Energy) use a simple `labelLarge` text + thin `HorizontalDivider` as their only visual separator. In the grid layout, this blends into the surrounding cards and does not create the visual hierarchy needed to communicate that these are distinct, consequential groups of actions — especially when "Power" commands can shut down or restart the PC.

### Root Cause Analysis
`CommandCategoryHeader` (around line 170 in RemoteControlScreen.kt) renders as:
```kotlin
Text(text = label, style = MaterialTheme.typography.labelLarge, ...)
HorizontalDivider(color = MaterialTheme.colorScheme.outlineVariant)
```
There is no background differentiation, no icon or visual motif, no padding contrast, and no semantic weight difference between categories that contain harmless actions (Wake, Lock) and destructive ones (Force Shutdown, Force Restart). The `outlineVariant` divider color is deliberately subtle in Material 3 — it's designed for secondary borders, not section breaks.

### Proposed Solution
1. **Give each category header a tinted background chip or surface color** — for example, wrap the header in a `Surface` with `tonalElevation` or use `MaterialTheme.colorScheme.surfaceContainerLow` as a background strip across the full grid width.
2. **Add a leading icon per category** — a small icon (e.g., desktop icon for Session, power icon for Power, battery/leaf icon for Energy) next to the label text to give instant visual recognition.
3. **Increase vertical spacing before each category header** — add `Spacer(Modifier.height(16.dp))` before non-first categories to create breathing room in the grid.
4. **Consider color-coding destructive categories** — Power commands could use `errorContainer` or a warm tint to signal danger, while Energy could use a cooler/muted tone.

---

## Issue 2 — Remote Desktop: Scroll/Zoom Toolbar Is Permanently Visible

```yaml
---
type: Refactor
severity: Medium
breaking_changes: False
target_files:
  - RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/RemoteDesktopScreen.kt
---
```

### Issue Summary
The scroll up/down and zoom +/−/reset controls are rendered as a permanent `Surface` bar at the bottom of the screen when streaming. In fullscreen mode, this bar occupies screen real estate and detracts from the immersive remote desktop experience.

### Root Cause Analysis
In RemoteDesktopScreen.kt (approximately lines where `if (isStreaming)` wraps the `Surface` block), the toolbar is unconditionally rendered inside the `Column` whenever the stream is active:
```kotlin
if (isStreaming) {
    Surface(tonalElevation = 3.dp, ...) {
        Row(modifier = Modifier.padding(horizontal = 12.dp, vertical = 4.dp), ...) {
            // scroll up, scroll down, zoom +, zoom −, reset
        }
    }
}
```
There is no visibility toggle, no auto-hide timer, and no animation to overlay/dismiss it. The bar pushes the desktop image upward, reducing the viewable area.

### Proposed Solution
1. **Make the toolbar an overlayed, auto-hiding element** — position it with `Box(modifier = Modifier.align(Alignment.BottomCenter))` overlaying the desktop image rather than being a sibling in the Column.
2. **Add auto-hide with a fade timer** — show the bar when the user taps the screen or performs a gesture, then auto-hide after 3–4 seconds of inactivity using `LaunchedEffect` + `delay` + `AnimatedVisibility`.
3. **Use semi-transparent background** — `MaterialTheme.colorScheme.surfaceContainerHighest.copy(alpha = 0.75f)` so the desktop content behind is still partially visible.
4. **In fullscreen mode, start hidden** — the bar should default to invisible, appearing only on deliberate interaction (e.g., a quick triple-tap or swipe-up from bottom edge).

---

## Issue 3 — Remote Desktop: Mouse Button FABs Are Poorly Placed in Fullscreen

```yaml
---
type: Refactor
severity: High
breaking_changes: False
target_files:
  - RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/RemoteDesktopScreen.kt
---
```

### Issue Summary
The floating mouse button controls (left/middle/right click + expand FAB) on the Remote Desktop screen are positioned as a static column at the bottom-right. In fullscreen mode, they are always visible, obstruct the remote desktop view, and the expand/collapse mechanism (rotating + icon) is not intuitive. The user notes that the floating mouse implementation on other screens (Remote Mouse screen) works well, but the Remote Desktop fullscreen version is "awful."

### Root Cause Analysis
The FABs are nested inside the `Column(modifier = Modifier.fillMaxSize().padding(...))` layout at approximately lines 700–780 in RemoteDesktopScreen.kt. Key issues:
1. **Placement within the Column** rather than overlaying the Box — the FABs are siblings of the desktop image, not floating over it.
2. **No auto-collapse in fullscreen** — `mouseControlsExpanded` is `rememberSaveable { mutableStateOf(false) }` and persists across rotations, but there's no logic to auto-collapse when entering fullscreen.
3. **The buttons are always rendered when `isStreaming`** — there's no fullscreen-aware hiding or repositioning.
4. **The mouse button icons are all `Icons.Default.Mouse`** (just flipped/unflipped) — not visually distinctive for left vs. middle vs. right.

### Proposed Solution
1. **Merge scroll/zoom toolbar and mouse controls into a single hideable overlay bar** — a translucent bottom bar (or a swipe-up sheet) that contains scroll, zoom, AND mouse click buttons in one row. This reduces visual clutter from two separate UI elements.
2. **Auto-hide in fullscreen** — default to hidden, reveal on a dedicated gesture (e.g., swipe up from bottom edge or long-press on an area without active remote content).
3. **Use distinct icons** for L/M/R clicks — not three copies of the Mouse icon. Consider simple labeled circles ("L", "M", "R") or different icon shapes.
4. **Match the pattern from RemoteMouseScreen** which the user already approves of.

---

## Issue 4 — Remote Desktop: No Visible Cursor in Trackpad Mode

```yaml
---
type: Bugfix
severity: High
breaking_changes: False
target_files:
  - RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/RemoteDesktopScreen.kt
  - RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/RemoteDesktopViewModel.kt
---
```

### Issue Summary
When using the default trackpad mode on the Remote Desktop screen, the user cannot see where the mouse cursor is positioned on the remote desktop. The cursor overlay is only rendered when `directTouch || isStylusActive`, meaning trackpad-mode users have no visual feedback of cursor location.

### Root Cause Analysis
The cursor overlay Box in RemoteDesktopScreen.kt (approximately line 690) is gated by:
```kotlin
if (isStreaming && cursorVisible && (directTouch || isStylusActive)) {
    Box(modifier = Modifier.offset { ... }.size(cursorSizeDp).clip(CircleShape)...)
}
```
In trackpad mode (`directTouch = false`, `isStylusActive = false`), the cursor Box is never rendered. The `cursorX`/`cursorY` state variables are only updated when `useAbsolute` is true (which requires direct touch or stylus). In trackpad mode, cursor movement is relative — the host moves the cursor, but the Android app has no knowledge of where the cursor actually ends up on the remote screen.

### Proposed Solution
This is architecturally challenging because in trackpad mode the host owns the cursor position. Two approaches:

**Option A (Recommended): Request cursor position from host**  
Have the host periodically send cursor coordinates back in the desktop metadata stream. The ViewModel can then expose `hostCursorX`/`hostCursorY` as state, and the screen can map those host coordinates to local image coordinates (inverse of `mapLocalToHost`) to render a cursor overlay. This gives accurate cursor position at the cost of slight latency.

**Option B: Render cursor in the streamed image**  
Have the host capture the cursor in the screenshot itself (`CopyFromScreen` with cursor overlay enabled on Windows, or `xdotool` cursor compositing on Linux). This has zero-latency cursor display but prevents the Android app from styling or sizing the cursor independently.

---

## Issue 5 — Task Manager: CPU Usage Always Shows 0%

```yaml
---
type: Bugfix
severity: High
breaking_changes: False
target_files:
  - Remex.Host/Services/ProcessMonitor/WindowsProcessMonitorService.cs
  - Remex.Host/Services/ProcessMonitor/LinuxProcessMonitorService.cs
  - RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/TaskManagerViewModel.kt
---
```

### Issue Summary
The Task Manager screen displays 0% CPU for all processes, as shown in the user's screenshot (java, plasmashell, code, chrome, Remex.Client.De — all at 0%).

### Root Cause Analysis
Both `WindowsProcessMonitorService` and `LinuxProcessMonitorService` use a differential CPU tracking approach that requires **two consecutive readings** to compute a non-zero percentage. On the first call to `GetProcessesAsync()`, every process is encountered for the first time and gets initialized with `CpuUsage = 0`:

**Windows** (`WindowsProcessMonitorService.cs`):
```csharp
if (!_cpuTrackers.TryGetValue(p.Id, out var tracker))
{
    tracker = new ProcessCpuTracker { LastCpuTime = cpuTime };
    _cpuTrackers[p.Id] = tracker;
    info = info with { CpuUsage = 0 };  // First encounter → 0%
}
```

**Linux** (`LinuxProcessMonitorService.cs`):
```csharp
if (!_cpuTrackers.TryGetValue(pid, out var tracker))
{
    tracker = new ProcessCpuTracker { LastCpuTime = processTotalCpuTime };
    _cpuTrackers[pid] = tracker;
    // cpuUsage stays at 0 (initialized above)
}
```

The Android client's `TaskManagerViewModel` calls `refreshProcesses()` once during `init {}`, which sends a single `process_list_request`. The host responds with the first snapshot where every process has 0% CPU. Without a second request after a short interval, no delta is ever calculated.

### Proposed Solution
1. **Double-fetch on init** — In `TaskManagerViewModel.init{}`, send two `process_list_request` messages separated by a 1–2 second delay. The first primes the CPU trackers, the second returns real CPU percentages.
2. **Add periodic auto-refresh** — Start a coroutine in the ViewModel's `init` that calls `refreshProcesses()` every 3–5 seconds while the screen is active (scoped to `viewModelScope`). This ensures CPU data stays fresh and the first non-zero reading appears within seconds.
3. **Show a "calculating..." indicator** — When all CPU values are 0 (first response), display a subtle hint like "CPU usage calculating..." rather than showing misleading 0% values.

---

## Issue 6 — NotConnectedBanner Shows When Host Is Connected and Functional

```yaml
---
type: Bugfix
severity: High
breaking_changes: False
target_files:
  - RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/CommonComponents.kt
  - RemEx.Android/app/src/main/java/com/clindsay94/remex/RemexClientManager.kt
---
```

### Issue Summary
The error banner "No PC connected" displays on screens like Remote Control and Task Manager even when the host service is running and the user can successfully send commands (wake, lock, etc.) and receive process lists. The banner creates a false sense of disconnection.

### Root Cause Analysis
The `NotConnectedBanner` is controlled by `RemexClientManager.isConnected`:
```kotlin
val isConnected by RemexClientManager.isConnected.collectAsState()
// → banner shows when !isConnected
```

`RemexClientManager.isConnected` is a `MutableStateFlow(false)` that is **only set to true** by the native `onConnectionStateChanged(true)` callback. However, several scenarios can cause a disconnect/reconnect cycle that leaves `isConnected` temporarily or permanently false:

1. **Heartbeat auto-connect** — The `initialize()` heartbeat loop in `RemexClientManager` calls `connect()` when `isConnected` is false. `connect()` calls `InitRemex()` which triggers the native library to establish a WebSocket connection. If the native code successfully initializes but sends the `onConnectionStateChanged(true)` callback on a different thread before the Kotlin coroutine has resumed, there could be a race.
2. **Transient disconnects during reconnection** — The exponential backoff loop may re-trigger connections while the previous connection is still alive, causing momentary state toggles.
3. **Initial state** — `_isConnected` starts as `false` and stays false until the native callback fires. If the app navigates to Remote Control or Task Manager before the callback fires on first launch, the banner appears and may not be dismissed if the callback timing is off.

The critical observation is that the user can USE the app's features (commands work, process lists arrive) even while `isConnected` reports false. This strongly suggests the WebSocket/native connection is functional but the state callback is either delayed, not firing, or being reset.

### Proposed Solution
1. **Verify connection state from command responses** — When `sendSystemCommand()` or `refreshProcesses()` receives a successful response, update `isConnected` to true as a secondary signal. Don't rely solely on the native callback.
2. **Add a debounce to the banner** — Don't show the banner immediately when `isConnected` flips to false. Wait 3–5 seconds before displaying it, using `LaunchedEffect` with a delay. This prevents flashing during transient reconnection cycles.
3. **Log diagnostic info** — Add logging in `onConnectionStateChanged` to track when it fires and what value it reports, to empirically diagnose the callback timing issue.
4. **Consider using command success as a heartbeat** — If a command (process_list_request, telemetry poll, etc.) succeeds, treat that as proof of connection and set `isConnected = true`. This makes the connection state reflect actual operational capability rather than relying on a single callback.

---

## Issue 7 — Splash Screen: Text Is Too Small, Too Dim, Wrong Font Rendering

```yaml
---
type: Bugfix
severity: Medium
breaking_changes: False
target_files:
  - RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/SplashScreen.kt
  - RemEx.Android/app/src/main/res/values/font_certs.xml
---
```

### Issue Summary
The splash screen brand text ("REM(ote) EX(ecution)") is not rendering in Victor Mono as intended, appears too small, and is not bright/pronounced enough against the dark substrate background. The user specifically wants Victor Mono featured prominently.

### Root Cause Analysis
Three separate sub-issues:

**A. Font may not be loading (fallback to system default):**
The code uses `GoogleFont.Provider` with the Google Play Services font authority:
```kotlin
private val splashFontProvider = GoogleFont.Provider(
    providerAuthority = "com.google.android.gms.fonts",
    providerPackage = "com.google.android.gms",
    certificates = R.array.com_google_android_gms_fonts_certs
)

private val victorMonoFamily: FontFamily by lazy {
    val font = GoogleFont("Victor Mono")
    FontFamily(Font(googleFont = font, fontProvider = splashFontProvider, weight = FontWeight.Bold), ...)
}
```
Victor Mono IS available on Google Fonts, but the downloadable fonts API is asynchronous — the font may not be ready when the splash screen first composes. If the font isn't cached and the network fetch hasn't completed, Compose silently falls back to the system default font. The splash screen has a tight animation timeline (total ~4 seconds) which may not leave enough time for font download.

Additionally, the `font_certs.xml` must contain the correct GMS font provider certificate hashes. If these are wrong or outdated, font loading will silently fail.

**B. Text is too small:**
Current sizes:
- `brandMainStyle` ("REM", "EX"): `fontSize = 40.sp, letterSpacing = 3.sp`
- `brandCompleteStyle` ("(ote)", "(ecution)"): `fontSize = 32.sp, letterSpacing = 1.sp`
- `taglineStyle` ("Command Your PC"): `fontSize = 14.sp, letterSpacing = 0.5.sp`

On modern high-DPI phones (400dp+ width), 40.sp is a modest headline. For a splash screen that should FEATURE the brand name, 56–72.sp would be more appropriate for the main text, with the completions at 40–48.sp.

**C. Text is too dim:**
The text colors are:
- "REM" / "EX": `primary` — Material 3 primary on a dark substrate. Depending on the theme seed, `primary` in a light theme could be a muted tone. On the `Color(0xFF050508)` substrate, M3 `primary` may not have enough contrast.
- "(ote)" / "(ecution)": `onBackground.copy(alpha = 0.75f)` — deliberately dimmed.
- "Command Your PC": `onBackground.copy(alpha = 0.6f)` — very dim.

The user wants the text to be "brighter, more pronounced." The alpha-reduced onBackground colors are working against this goal on the near-black substrate.

### Proposed Solution
1. **Bundle Victor Mono as a local font asset** — Instead of relying on Google Fonts download at runtime, include the Victor Mono .ttf files in `src/main/res/font/` and reference them with `FontFamily(Font(R.font.victor_mono_bold, FontWeight.Bold), ...)`. This guarantees the font is available immediately with zero latency.
2. **Increase font sizes significantly:**
   - `brandMainStyle`: 56–72.sp
   - `brandCompleteStyle`: 40–48.sp
   - `taglineStyle`: 18–20.sp
3. **Increase text brightness:**
   - "REM" / "EX": Use `Color.White` or `onPrimary` instead of `primary`
   - "(ote)" / "(ecution)": Use `primary` or `onBackground.copy(alpha = 0.90f)` instead of 0.75f
   - "Command Your PC": Use `onBackground.copy(alpha = 0.80f)` instead of 0.6f
4. **Add a subtle text glow/shadow** to make the text pop further against the dark substrate — a `drawBehind` modifier with a blurred `drawText` in a brighter color, or use the Canvas `drawText` double-pass technique (draw once with a wider stroke in a glow color, then draw again normally).

---

## Summary of Findings

| # | Issue | Severity | Type |
|---|-------|----------|------|
| 1 | Remote Commands category headers lack visual weight | Medium | Refactor |
| 2 | Remote Desktop scroll/zoom bar is permanently visible | Medium | Refactor |
| 3 | Remote Desktop mouse FABs are poorly placed in fullscreen | High | Refactor |
| 4 | No visible cursor in Remote Desktop trackpad mode | High | Bugfix |
| 5 | Task Manager CPU usage always shows 0% | High | Bugfix |
| 6 | NotConnectedBanner shows when host IS connected | High | Bugfix |
| 7 | Splash screen: wrong font, too small, too dim | Medium | Bugfix |

### Positive Observations
- The overall architecture is well-structured: clean ViewModel separation, proper StateFlow usage, defensive null handling.
- The gesture state machine in RemoteDesktopScreen is comprehensive — supporting finger/stylus, tap/long-press/double-tap-hold/drag/inertia/pinch-zoom/scroll with proper debouncing and throttling.
- The splash screen animation choreography (scan radar → wave radar → connection glow → zoom pull-in) is creative and well-sequenced.
- Bitmap pooling with `inBitmap` reuse in the RemoteDesktopViewModel is a solid performance optimization.
- The exponential backoff reconnection patterns in both RemexClientManager and RemoteDesktopViewModel are correctly implemented.
