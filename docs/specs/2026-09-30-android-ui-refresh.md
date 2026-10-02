# Android UI refresh (3.0) — spec

Status: draft for Connor's review, 2026-09-30. Scope: `remex.android` only. The PC app gets its own
review once its screens have been captured; the two should end up related, not identical.

## Why

The app works, but it reads as unfinished. Three visual languages compete:
1. Shape-morph containers (dashboard tiles, Remote Control hexagons, App Launcher pentagons). The shapes
   clip their own content, break alignment and make every element equally loud.
2. Calm M3 cards and lists (Routines, Settings, File Transfer). Routines is the best screen in the app
   and the reference for everything below.
3. Raw forms (Connection, Stream Configuration).

Add one-colour monotony (every tile the same blue), always-on edit controls, buried headline features and
a layer of visible bugs, and the whole thing feels dated.

## Principles

- **Routines is the reference screen.** Rounded-rectangle cards, tonal surfaces, one clear primary action,
  plain language.
- **Expressive shapes are accents, never containers.** Allowed: the FAB, one hero element on Sensors,
  loading indicators, and the shape morph on press for buttons and toggles. Not allowed: any card,
  tile or button whose shape clips text, icons or controls.
- **Colour means something.** Surfaces use the tonal container ladder (surface → surfaceContainerLow →
  surfaceContainer → surfaceContainerHigh). Primary is for the thing you act on or the number you read.
  Status uses tertiary/error containers (online, warning, critical), never decoration.
- **One floating element per screen, at most.** Scrolling content gets bottom padding equal to whatever
  floats over it, so nothing is ever hidden.
- **Edit controls live in an edit mode.** Long-press (or an Edit action) enters it; pins, resize handles
  and "add card" only appear there.
- **Motion carries meaning.** Shared-axis X between nav destinations, container transform from a card
  into its detail, shape morph on press, animated number changes on sensor values. Respect the system
  "remove animations" setting.
- Holds under every theming axis: custom seed, dynamic colour, static fallback × light/dark × the 9
  `themeStyle` values × contrast −1..1 (monochrome and contrast 1.0 are the hardest cases).

## Navigation

Bottom bar (NavigationSuiteScaffold keeps the rail on large screens), five destinations, one-word labels,
labels always shown:

| Slot | Label | Contains |
|---|---|---|
| 1 | Home | New overview screen, the phone counterpart of the PC's Home (Connor, 2026-10-02) |
| 2 | Desktop | Remote Desktop (stream). Remote Mouse becomes a mode inside it ("Trackpad only") rather than its own screen |
| 3 | Apps | App Launcher |
| 4 | Control | Segmented toggle at the top: **Commands** (session, power, energy, media) / **Processes** (Task Manager) |
| 5 | More | Sensors (full canvas), Files, Routines, Connection, Settings, Help |

The full Sensors canvas is one tap from Home (the "Open Sensors" card) and is also listed in More.
Routines also gets an entry point from Home (a "Next routine" chip when one is scheduled), so the 3.0
headline is one tap away.

## Screens

### Home (new)
The phone's version of the PC Home: what's going on with my PC, at a glance. Not the canvas.
- **PC card** at the top: PC name (machine name since RemEx-odqj5), online/offline status chip, uptime,
  and Wake / Lock / Sleep as tonal buttons (Sleep hidden when the PC reports it can't, same rule as
  Commands). This is the one place an expressive shape is allowed (e.g. a soft cookie-shape status badge
  behind the PC icon). When disconnected it becomes the connect card (known PCs, Connect, Wake).
- **Pinned sensors:** a compact row/grid of the sensors pinned for the Home screen. Same list as the PC
  Home's "Pinned sensors" (Personalize > Layout), synced, so both Homes show the same readings; editable
  from the phone too.
- **Open Sensors** card into the full canvas (with a live mini-preview if cheap).
- **Shortcuts:** Desktop, Files, Routines (with the "Next routine" chip), as a short row of tonal tiles.
- **Recent activity** (optional, if the data already exists on the phone): last routine run, last file
  transfer, last connection change. Plain words, newest first, at most a handful of rows.
- Matches the PC Home's sections where they exist so the two read as the same screen.

### Sensors (full canvas; reached from Home and More)
- No hero card here any more (Home has it). The canvas is just sensors.
- **Sensor grid:** uniform rounded-rectangle cards on surfaceContainer, label top-left, value large
  in primary, the existing gauge/sparkline styles inside. Sizes stay user-chosen (1×1, 2×1, 2×2) but on a
  real grid so edges align.
- **Edit mode** (long-press or overflow → Edit): shows pin, resize and remove on each card plus the
  "Add card" FAB. Outside edit mode there is no FAB and no handles.

### Desktop
- Stopped state: the stream preview area with "Start streaming" as the primary button. The top bar title
  is short ("Desktop") and the actions collapse into an overflow when they don't fit (fixes the
  letter-per-line title).
- Streaming: a single floating toolbar that auto-hides after a few seconds of no touch and comes back on
  a tap near the top edge. The FPS readout lives inside the toolbar, not underneath it. The keyboard helper
  bar keeps its current behaviour.
- Trackpad mode: the old Remote Mouse screen as a mode of this destination, themed like the rest.
- Do not touch the decoder, SurfaceView zoom/pan or stream pacing (docs/REGRESSION-GUARDS.md).

### Apps
- Grid of rounded-square tiles (or plain icon + label, launcher style). Labels are the friendly name,
  max two lines, ellipsised at word boundaries (no "SparkingZE / RO").
- Refresh moves to the top bar (pull-to-refresh as well). The floating toolbar goes.

### Control
- **Commands:** section labels as text (not full-width coloured bars). Session and Energy actions as a
  grid of tonal cards with icon + label; tapping runs or asks for confirmation as today. Destructive
  actions (Force Shutdown, Force Restart) sit in their own "Force" group on errorContainer. The media
  mini-player docks at the bottom of this tab only. The duplicate floating toolbar goes.
- **Processes:** the current Task Manager with rounded-rectangle rows instead of capsules, search and
  sort chips unchanged.

### More
- **Files:** keep the current structure. Hide dot-folders and Windows system folders ($Recycle.Bin,
  Config.Msi, Documents and Settings…) behind a "Show hidden items" toggle, off by default.
- **Routines:** unchanged (it's the reference).
- **Connection:** lead with "Your PCs": one card per known PC (name, status, last connected, Connect).
  "Add a PC" opens discovery / QR / manual entry. Manual IP, port, MAC, broadcast, subnet and PIN live in
  "Add manually" and in a per-PC details sheet. Remote Desktop defaults move out (they already exist in
  the stream sheet). The status line wraps instead of truncating.
- **Settings:** unchanged.

## Bug batch (independent of the redesign, do first)

These survive the redesign, so fixing them now is not wasted:
1. Remote Mouse renders on a light surface with black icons in dark theme.
2. Remote Desktop top-bar title wraps one letter per line.
3. FPS pill renders underneath the streaming toolbar buttons.
4. App Launcher labels break mid-word.
5. Connection status line is truncated at the bottom of the screen.
6. Floating toolbars / mini-player / FAB cover the last items of scrolling lists (missing bottom padding).

## Phasing

1. Bug batch. (done, 5abae8d9 + 90817d53)
2. Navigation (five destinations incl. Home, labels, Control's segmented toggle, Remote Mouse as a
   Desktop mode, Sensors moves into More). Home can land as a simple placeholder of PC card + Open
   Sensors here so the nav is real; phase 3 fills it.
3. Home (PC card, pinned sensors synced with the PC Home, Open Sensors, shortcuts, recent activity) +
   Sensors canvas (aligned grid, edit mode).
4. Apps + Control restyle. (Commands restyle done in cohesion phase 3, b514d644.)
5. Connection restructure + Files hidden-items toggle.
6. Motion pass across all destinations.

Each phase: release build, the theming-axis sweep on the AVD (screenshots), Connor's device check, then
commit. New user-facing strings go through all 9 locales.

## Out of scope

The PC app (separate review), new features, the remote-desktop pipeline, Routines internals.
