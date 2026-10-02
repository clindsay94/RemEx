# Cross-app cohesion (PC ↔ phone) — spec

Status: approved direction, 2026-10-01 (Connor). Companion to
`docs/specs/2026-09-30-android-ui-refresh.md`, which this amends where they overlap. Based on Connor's
phone screenshots (2026-09-30) and 26 PC screenshots (2026-10-01).

## Diagnosis

The two apps share their foundations (one MCU seed, the same nine theme strategies, the same features) but
read as two products: the PC is a translucent "command center" with decorative display fonts and
terminal-style labels; the phone is a plain Material app with blob shapes. The gap is mostly vocabulary,
headers and type, not features.

Already aligned, keep: colour seed + strategies; PC Commands' Graceful / Forced grouping (the model for
both); PC App Launcher and Sensors rounded-rectangle cards; PC per-sensor accent colours.

## Decisions

1. **One vocabulary.** The same nouns on both apps, and every page title equals its nav label:
   **Sensors, Commands, Apps, Processes, Files, Routines, Settings** (+ PC-only Home, Logs & Diagnostics,
   About; phone-only Desktop). Today the PC titles "Commands" as "PC REMOTE", "Launcher" as "APP LAUNCHER",
   "Processes" as "TASK MANAGER"; the phone says "Remote Control", "App Launcher", "Task Manager",
   "RemEx Home Base". The phone's Control tab holds Commands | Processes (android spec).
2. **Plain words on both (Connor, 2026-10-01).** Retire the terminal voice: "TERMINATE LINK" →
   "Disconnect", "LINE_STATUS" → "Status", "Host link" → "Connection", "// ACTIVE SENSORS" →
   "Active sensors", "Command Center" → the page name. Monospace/underscore labels go.
3. **Decorative display fonts on both (Connor, 2026-10-01).** The phone adopts the PC's display type as the
   shared default: page titles in the PC's page-title font, subtitles in its subtitle font (today Bungee
   Shade + Orbitron; confirm the PC defaults in the typography service rather than copying Connor's picks).
   Rules:
   - Display fonts are for page titles and subtitles only, never body text, controls or numbers.
   - Titles must fit on one line at 360dp in all 9 locales; shrink before wrapping, never break mid-word.
   - Bungee Shade and Orbitron are Latin-only. Ukrainian (Cyrillic) and Hindi (Devanagari) must render
     titles in a font that covers the script (Victor Mono covers Cyrillic; the system font covers
     Devanagari), chosen per locale, not left to per-glyph fallback that mixes fonts in one word.
   - Bundle the fonts on Android with their OFL licences (PC already ships `OFL-Orbitron.txt`; add the
     Bungee Shade licence on both). Extend `BundledFontFilesTests` to cover them.
   - Later (not this spec): a font picker on the phone, synced through "Match phone".
4. **One header pattern.** Title + one plain-language subtitle line on both (PC already has the subtitle;
   the phone's RemexFlexibleTopBar gains it). Subtitles are plain sentences, not tag-lines.
5. **Commands parity.** Same groups, order and names on both: Graceful (Lock, Sign out, Shutdown, Restart,
   Sleep, Hibernate) and Forced (Force shutdown, Force restart, Reboot to UEFI). "Log Off" becomes
   "Sign out". The phone hides actions the PC reports it can't do. Wake PC stays phone-only (and on the
   Sensors hero card).
6. **Card anatomy.** Same corner radius and spacing on both (one token; the PC's Corner Radius slider
   drives the PC). PC glass stays a PC option. Phone sensor cards adopt the PC's per-card accent colours
   through the existing card themes.
7. **Processes row anatomy.** Name + PID on line 1; CPU and memory with mini bars on both. Path and
   publisher are an extra line on the PC only.
8. **Personalize entry on the PC** moves from the floating palette button on every page to the sidebar
   footer (next to the connection chip), so the corner is free and the PC follows the
   one-floating-element rule.

## PC bugs found in the screenshots

1. Tray flyout: "No changes this session" text floats over the header.
2. Tray flyout: a horizontal line is drawn through the last row of cards.
3. Tray flyout: card titles truncate early ("Average Effective Clo…").
4. Sidebar: every item has a filled background, so the selected page isn't distinguishable.
5. Settings → Paired devices: a permanent "New name" box per device; rename should be an action.

(The "Victor Mono couldn't be loaded" warning in Personalize is the installed build predating f310dd19.)

## Phasing

1. Vocabulary + plain words (both apps, all 9 locales each). Highest cohesion per line changed.
2. PC bugs 1–5.
3. Commands parity.
4. Typography + header pattern (fonts bundled on Android, locale fallback, subtitles).
5. Card anatomy + phone sensor accents; Processes row parity.
6. PC Personalize entry point.

Android refresh phases (nav, Sensors, Apps/Control, Connection/Files, motion) continue in parallel and
adopt decisions 1–7 as they go.

## Out of scope

New features, the remote-desktop pipeline, theming axes themselves, a phone font picker.
