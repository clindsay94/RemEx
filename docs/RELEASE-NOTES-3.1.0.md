**RemEx 3.1: Files, redesigned on the PC and the phone**

This release is about one thing: moving and checking files between your phone and your PC. Both apps now show the two devices in one folder tree, preview the file you pick, and show the SHA-256 fingerprint that proves a file arrived exactly as it was sent.

This release covers everything since 3.0.0. The complete list is in [`docs/CHANGELOG.md`](https://github.com/clindsay94/RemEx/blob/main/docs/CHANGELOG.md).

---

## Both apps

- **One folder tree for both devices.** The PC's File Transfer page lists This PC and each connected phone; the phone's Files screen lists This phone and your PC. Open a folder level by level with the arrow next to it, and the tree follows you as you browse.
- **Live preview.** Pick a file to see it straight away: photos at full quality with zoom and pan, and text, code and logs in color, with line numbers. Turn on **Live** to follow a log as new lines are written. Files that can't be previewed show their details.
- **SHA-256 fingerprints you can see.** A finished transfer says **Verified** when its fingerprint was really compared and matched, and **Done** when the other side sent nothing to compare. The preview's fingerprint section works out a file's fingerprint (shown as 64 letters and numbers, the format `sha256sum` and `Get-FileHash` print), copies it, compares it with one you paste in, and checks it against a copy on the other device.
- **A simpler toolbar.** Each file screen has one main button (Upload on the PC side, Add folder on the phone side), with New folder and Upload folder under More options.

## On the phone

- **A new Files screen.** On a tablet or an unfolded phone, the folder tree stays on the left with the folder and the preview beside it. On a phone, the tree opens from the menu button, a switch at the top flips between This phone and PC, and a file opens its preview full screen; back returns to the list.
- **Tapping a file previews it.** In list view a tap used to do nothing.
- **Send to PC and Save to phone.** Pick a folder on the other device and the files go there through the usual transfer queue, with pause, resume and verification. Long-press to select several files.
- **Phone folders you choose.** **Add folder** puts a folder from the phone in the tree so you can browse and preview it. Your PC can't see it unless you turn on **Share with PC** in its menu, which adds it to the same "Access from your PC" list as Settings.
- **Verified transfers.** Finished transfers show Verified with a check mark, a button copies the fingerprint, and the notification says Transfer verified.

## On the PC

- **A new File Transfer page,** laid out like Explorer: folders on the left, the folder you're in with thumbnails in the middle, and the preview on the right.
- **Right-click menus and keyboard shortcuts** (Enter, Backspace, F2, Delete, F5, Ctrl+C/X/V, Ctrl+A, Ctrl+F, Space for the preview).
- **Drag and drop.** Drag files from one device to another in the tree to send them, or from Windows Explorer onto a folder to upload them. Dragging inside one shared folder moves (or, with Ctrl, copies).
- **A transfer strip** along the bottom counts what's running, verified and failed, and opens into the full list.
- **Scan to get the phone app.** On Home, the Get the phone app card shows a QR code for RemEx on Google Play instead of opening a browser.

## Security

- A folder you add on the phone's Files screen is never visible to the PC until you turn on Share with PC. Everything the phone serves to the PC still comes only from the "Access from your PC" list.
- Previews and fingerprints from a phone use the same checks as browsing it, and the PC checks every path and range before a request leaves for the phone. "Let your PC change files" is not needed for either, because both only read.
- A phone file is fingerprinted on the phone and a PC file on the PC; only the 64-character result crosses the connection.

---

## Install and upgrade

- Install the new PC app over the old one. Your settings, paired phones and layout stay as they are.
- You do not need to pair again. Nothing in this release changes pairing or certificates.
- Update the PC and the phone together. A PC older than 3.1 still works with the new phone app, but the phone says "Update RemEx on your PC to preview its files here" instead of previewing PC files, and can't work out their fingerprints. Transfers keep working.
- The Google Play build is distributed separately through the Play Store.

## Known issues

- Send to PC and Save to phone move files, not folders. Use Upload folder and Download folder for a whole folder.
- On the phone, renaming or deleting the phone's own files is still done in the phone's own file manager.
- If a phone app lists two files with the same name in one folder (some cloud storage apps can), opening either one opens the first. The phone's own storage never does this.
- On the PC, HEIC photos from a phone preview as a thumbnail, not at full size.
- Phone files can't be dragged out of RemEx into Windows Explorer; use Download to… instead.
- The PC window does not remember its size and position. It opens at the default size each time.

---

## Downloads

| Platform | File | Size |
|---|---|---|
| **Windows** | `RemEx-v3.1.0-Setup.exe` | — |
| **Android** | `RemEx-V3.1.0-release.apk` (sideload) | 38.6 MB |
| **Linux (x64)** | `remex-agent-v3.1.0-linux-x64.tar.gz` | 70.1 MB |

Full detail is in [`docs/CHANGELOG.md`](https://github.com/clindsay94/RemEx/blob/main/docs/CHANGELOG.md).
