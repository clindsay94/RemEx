# File Sharing

RemEx lets you move files between your phone and your PC, both ways. This page
explains, in plain English, the three things people ask about most:

1. **Consent** — when RemEx asks before one device touches the other's files.
2. **Full‑device browse** — the opt‑in setting that lets your phone see the PC's
   whole file system instead of just a few shared folders.
3. **Share to PC** — how to send a file to your PC straight from any app on your phone.

You do **not** need to read this to use file sharing. The app guides you through
everything. This is here if you want to understand exactly what's happening and
why it's safe.

---

## The short version

- By default, your phone can only reach a small list of **shared folders** on the
  PC that you chose. Nothing else is visible.
- Your phone can only **send files to** a shared folder you have marked as
  writable. Making a folder writable *is* your permission, so the PC does not ask
  again for each upload.
- Anything more than that **asks you first**: the phone browsing the PC's whole
  file system asks on the PC, and the PC sending files to your phone asks on the
  phone. You tap **Allow** or **Deny**.
- If you don't answer within **60 seconds**, RemEx treats that as **Deny** and
  nothing happens.
- You can tick **"Remember this device"** so a device you trust doesn't have to
  ask every time. You can undo that at any point (see *Taking access back*).
- Your files never leave your own network — the phone and the PC talk directly to
  each other over an encrypted connection.

---

## 1. Consent — RemEx asks before anything sensitive

RemEx pops up a request and waits for your answer in two cases:

- **On the PC: full‑device browse.** Your phone wants to look at more than the
  PC's shared folders (see section 2). The PC shows a dialog box with **Allow**
  and **Deny** buttons and a *"Remember this device"* checkbox.
- **On the phone: files from the PC.** The PC wants to *send* files to your phone.
  The phone asks two ways at once so you never miss it:
  - a **notification** with **Allow** / **Deny** buttons, and
  - a **pop‑up in the app** with the same choice, a *"Remember"* checkbox, and a
    live countdown.

  Whichever one you tap first wins. The prompt tells you **which files** are
  being sent and **how big** they are in total, so you know what you're agreeing
  to before you say yes.

If you ignore a request, it quietly expires after 60 seconds and is treated as
**Deny**.

**Files from your phone to the PC do not prompt.** They can only land in a shared
folder you marked as writable, and marking it writable is the permission. To stop
uploads into a folder, make it read‑only (or stop sharing it) in **Settings →
Shared folders** on the PC.

---

## 2. Full‑device browse — off until you turn it on

Normally your phone only sees the **shared folders** you picked on the PC. If you
want to give it access to *everything* — for example, to grab a file from anywhere
on your PC — you turn on **full‑device browse**. This is **off by default** and
only turns on when you explicitly enable it.

- **On Windows**, this lets your phone see all the PC's **drives** (C:, D:, and
  so on).
- **On Linux**, it lists the PC's mounted **volumes**.

**Browsing the phone from the PC isn't available yet.** The phone already has an
*"Access from your PC"* setting, where you would pick a folder tree with Android's
own folder picker (that picker *is* the permission — Android itself asks you), but
the PC's Files page can't browse a phone in this version. It is planned for a
later update.

**Some places are always off‑limits, even with full browse on.** RemEx permanently
blocks the internal system folders that keep a computer running (on Linux:
`/proc`, `/sys`, `/dev`, `/run`, and `/boot/efi`). These are never shown and can
never be written to, because touching them could damage the machine. This block
can't be switched off.

A quick‑access row of your drives/volumes only appears **after** you've granted
full browse — until then, there's nothing extra to see.

---

## 3. Share to PC — send files from any app on your phone

RemEx registers as a **share target** on Android. That means in any app — Photos,
your browser, a file manager — you can tap **Share** and pick **RemEx**.

Here's what happens:

1. RemEx opens a small **Send to PC** screen showing your paired PC.
2. You pick which **shared folder** on the PC the files should land in.
3. RemEx sends the files. Because sharing hands over the files only for a moment,
   RemEx first makes its own copy so the transfer can't fail halfway if the share
   screen closes.
4. The transfer runs in the background (a foreground service keeps it alive), so
   you can leave the screen and it still finishes.

The PC does not ask before accepting these files: you can only pick a shared
folder you have marked as writable, and that choice is the permission (section 1).

### Open a file right after downloading it

When a download to your phone finishes, RemEx shows a notification with an
**Open** button. Tapping it opens the file in whatever app normally handles that
kind of file — a photo in your gallery, a PDF in your reader, and so on.

---

## Taking access back

You stay in control. You can change your mind at any time:

- **On the PC**, open **Settings → File-sharing trust**. Each paired phone has an
  *Allow full-device browsing* switch and a **Revoke** button. To stop uploads,
  make the folder read‑only in **Settings → Shared folders**.
- **On the phone**, open **Settings → "Access from your PC"** to turn off
  auto‑accept for files the PC sends (the full‑device‑browse switch there is
  waiting for the later update mentioned in section 2).
- **Unpairing a device removes all of its file‑sharing permissions automatically.**

---

## What about older versions?

File sharing was rebuilt in **RemEx 2.1** on a new, faster transfer system
(protocol version 3). If your phone app and your PC app aren't both on 2.1 or
newer, RemEx automatically falls back to the older, simpler transfer method so
things still work — you just won't see the new file‑manager features, resume, or
the queue until both sides are updated. Nothing you do can break an older device.

---

## Is it safe?

Yes. In summary:

- The phone and PC talk **directly**, over the same **encrypted, pinned**
  connection used for everything else in RemEx.
- **You approve** anything beyond your chosen shared folders, and approvals time
  out to *deny* if you don't respond.
- **System‑critical paths are permanently blocked.**
- Every transferred file is **verified with SHA‑256**, so a corrupted or tampered
  file is rejected rather than saved.
- Your **pairing secrets are never included** in savefile backups (see the export
  note in the changelog), so a backup can never leak the keys that authorize a
  device.

For the deeper security picture across all of RemEx, see
[**How RemEx keeps you safe**](SECURITY_EXPLAINED.md).
