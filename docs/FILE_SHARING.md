# File Sharing

RemEx lets you move files between your phone and your PC, both ways. This page
explains, in plain English, the three things people ask about most:

1. **Consent** — how RemEx asks before letting one device touch the other's files.
2. **Full‑device browse** — the opt‑in setting that lets one device see the whole
   file system instead of just a few shared folders.
3. **Share to PC** — how to send a file to your PC straight from any app on your phone.

You do **not** need to read this to use file sharing. The app guides you through
everything. This is here if you want to understand exactly what's happening and
why it's safe.

---

## The short version

- By default, each device can only reach a small list of **shared folders** that
  you chose. Nothing else is visible.
- Anything more than that — browsing the whole computer, or letting the other
  device *push* files to you — **always asks you first**. You tap **Allow** or
  **Deny**.
- If you don't answer within **60 seconds**, RemEx treats that as **Deny** and
  nothing happens.
- You can tick **"Remember this device"** so a device you trust doesn't have to
  ask every time. You can undo that at any point (see *Taking access back*).
- Your files never leave your own network — the phone and the PC talk directly to
  each other over an encrypted connection.

---

## 1. Consent — RemEx asks before anything sensitive

RemEx will pop up a request and wait for your answer before it does either of
these things:

- **Full‑device browse** — the other device wants to look at more than your
  shared folders (see section 2).
- **Incoming push** — the other device wants to *send* you files, dropping them
  onto your device rather than you pulling them.

**On the PC**, this appears as a dialog box with **Allow** and **Deny** buttons and
a *"Remember this device"* checkbox — the same style as the pairing PIN prompt.

**On the phone**, it appears two ways at once so you never miss it:

- a **notification** with **Allow** / **Deny** buttons, and
- a **pop‑up in the app** with the same choice, a *"Remember"* checkbox, and a
  live countdown.

Whichever one you tap first wins. If you ignore both, the request quietly expires
after 60 seconds and is treated as **Deny**.

When a push is requested, the prompt tells you **which files** are being sent and
**how big** they are in total, so you know what you're agreeing to before you say
yes.

---

## 2. Full‑device browse — off until you turn it on

Normally the other device only sees the **shared folders** you picked. If you want
to give it access to *everything* — for example, to grab a file from anywhere on
your PC from your phone — you turn on **full‑device browse**. This is **off by
default** and only turns on when you explicitly enable it.

- **On Windows**, this lets the other device see all your **drives** (C:, D:, and
  so on).
- **On Linux**, it lists your mounted **volumes**.
- **On Android**, you pick a folder tree with the system's own folder picker
  (that picker *is* the permission — Android itself is asking you), and only that
  tree becomes visible.

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

Sending files this way is an **incoming push** to the PC, so the PC will ask for
consent (section 1) unless you've already chosen to auto‑accept from that device.

### Open a file right after downloading it

When a download to your phone finishes, RemEx shows a notification with an
**Open** button. Tapping it opens the file in whatever app normally handles that
kind of file — a photo in your gallery, a PDF in your reader, and so on.

---

## Taking access back

You stay in control. You can change your mind at any time:

- **On the PC**, open **Settings → file‑sharing trust**. Each paired device has
  toggles for *allow full browse* and *auto‑accept incoming*, plus a **revoke**
  option.
- **On the phone**, open **Settings → "Access from your PC"** for the same
  full‑device‑browse and auto‑accept toggles.
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
