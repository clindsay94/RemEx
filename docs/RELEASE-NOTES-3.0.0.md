**RemEx 3.0: Routines, and one look on the PC and the phone**

This one is mostly about two things. Your phone can now act on its own, with routines that start when you get home, tap an NFC tag or a PC sensor passes a limit. And the PC app and the phone app now look and read the same, with the same pages, names, cards and colours.

This release covers everything since 2.5.0. The complete list is in [`docs/CHANGELOG.md`](https://github.com/clindsay94/RemEx/blob/main/docs/CHANGELOG.md).

---

## Both apps

- **Routines.** A routine is a trigger plus steps, made on the phone. Triggers include arriving or leaving home (recognised from your Wi-Fi network, so no location permission is needed), tapping an NFC tag, and a PC sensor staying above or below a limit. A Tap Run routine can sit on the home screen as a shortcut or as a widget that shows its last result.
- **Routines that run on the PC.** The PC can run a routine itself when it goes idle, or when it is locked or unlocked, even if the phone is away. The PC keeps a history of what ran, lets you switch a routine off, pause everything or block a phone, and the phone hears about it straight away. Shut down, restart, sign out, sleep and hibernate always show a countdown first.
- **One look.** A fresh install of either app starts on the same RemEx colours. Cards, page titles, fonts, the Commands groups and the page names (Sensors, Commands, Apps, Processes, Files) now match on both.
- **Pinned sensors are one list.** Pin or unpin a sensor on either one and the other follows within a couple of seconds.
- **A new startup splash.** Live Handshake is the default. It shows RemEx actually starting and finding your PC. Original Scan, Cosmic Zoom and Pong have their own scenes too.
- **Easier on the battery.** The PC only streams live sensor readings while a phone screen is showing them, and the phone asks it to stop while the app is in the background.

## On the phone

- **A new layout with five tabs:** Home, Desktop, Apps, Control and More. Home shows your PC (online status, Lock, Sleep, or Wake when it is off) and your pinned sensors.
- **Sensors is a grid.** Press and hold a card to edit: move, resize, remove, add, or pin to Home. You can choose how wide the grid is (Auto, 2, 3 or 4 columns).
- **Sensor alerts from your PC.** The alert rules you set on the PC arrive as notifications on the phone while it is connected. Critical alerts pop up and warnings arrive quietly. You can add, change or remove a rule from either one.
- **The PC's logs and diagnostics.** More has a new read-only page with what the PC has logged and the same health checks the PC shows. Anything private in a log line is hidden on the PC before it is sent.
- **More ways to personalize.** App backgrounds (six textures and three slow animated styles), six more card shapes, and seven Nerd Fonts that ship inside the app. The font list now shows plain names.
- **Motion and haptics.** Screens slide in, readings roll when they change, and switches, sliders and finished transfers give a short vibration. Both follow your phone's own settings, including Remove animations.
- **Apps, Connection and Files are restyled.** The splash plays every time you open the app.

## On the PC

- **A Routines page,** right after Commands, listing the routines your phones have set to run on this PC.
- **Browse your phone from the PC.** The Files page has a Source picker: This PC, or any connected phone. You can browse the folders the phone shares, search them, see details and thumbnails, and download from them.
- **Pair a phone from Home.** While no phone is connected, Home shows a Pair a phone button that opens Settings with the QR code and PIN ready.
- **Personalize is tidier.** It is five tabs, the panel can be dragged wider, there are four text size sliders, and Mica is back as a background on Windows 11 22H2 and later.
- **A calmer tray popup.** Your pinned sensors show as the same cards the Sensors page uses, and the actions are one row of icons.
- **Motion.** Pages slide in and settle, and every animation is instant when Reduced motion is on. It starts out matching your computer's own setting.
- **Settings lost the connection-address box.** The PC app is the host, so there was nothing to connect to.

## Security

- Apps and links RemEx opens on your PC now run with your normal permissions instead of administrator rights. That covers apps started from the phone, the Apps page or a routine.
- The phone only opens folders you share. When the PC asks the phone for a file, the phone checks your current Access from your PC settings, so turning off full-device browsing takes effect straight away.

## Fixed

- Downloading a folder no longer skips files, repeats files or stops early, including folders inside shared folders. Resume works again.
- A failed download now says so, and the Files screen asks for notification permission. Download notifications show up again on Samsung phones.
- Two-finger scrolling works again in Remote desktop, including in portrait.
- A refused alert Save now says why, instead of closing silently.
- Waking a PC uses that PC's address, not the last one you connected to.
- When a PC no longer recognises your phone, the phone says it needs pairing again instead of showing the PC as online.
- On the PC, Match phone is still there after a restart, and your dashboard layout is no longer swapped for an older one a few seconds after RemEx starts.
- A paired PC without a nickname shows its own name on the phone, not its IP address.
- The splash really draws RemEx in Victor Mono Bold, and the Aurora background actually draws.
- The PC no longer warns about screen capture being degraded every time it starts.
- Installing an update no longer turns Launch RemEx when you sign in back on after you turned it off.
- The PC's whole-device button in Files no longer shows a confusing error.
- Sensors on the phone: Clear all stays cleared after a restart, and Undo undoes only the last removal.
- Plain-English cleanup of labels and help text on both apps, in all nine languages.

---

## Install and upgrade

- Install the new PC app over the old one. Your settings, paired phones and layout stay as they are.
- You do not need to pair again. Nothing in this release changes pairing or certificates.
- Update the PC and the phone together. A PC older than 3.0 still works with the new phone app, but without sensor alerts, the logs page, shared pinned sensors and the PC-run routines.
- If you had "Launch RemEx when you sign in" turned off, the update leaves it off.
- If you had picked the old "JetBrains Mono" font on the phone, you keep a JetBrains Mono look. It now shows as JetBrainsMono Nerd Font.
- The Google Play build is distributed separately through the Play Store.

## Known issues

- The PC window does not remember its size and position. It opens at the default size each time.
- While streaming, the cursor shape does not always change on the first display. The second display is fine.
- Resuming a paused file download starts it again from the beginning, not from where it stopped.
- If Windows locks the session when a Remote Desktop connection drops, remote input stops until you unlock the PC.
- Reduced motion has not been checked on CachyOS yet, so on Linux it may not follow your system setting.

---

## Downloads

| Platform | File | Size |
|---|---|---|
| **Windows** | `RemEx-v3.0.0-Setup.exe` | 55.6 MB |
| **Android** | `RemEx-V3.0.0-release.apk` (sideload) | 38.2 MB |
| **Linux (x64)** | `remex-agent-v3.0.0-linux-x64.tar.gz` | 69.7 MB |

Full detail is in [`docs/CHANGELOG.md`](https://github.com/clindsay94/RemEx/blob/main/docs/CHANGELOG.md).
