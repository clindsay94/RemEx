# File browser redesign: tree, live preview, and SHA-256 on both platforms

Status: **proposed** (2026-10-08). Branch `claude/clever-cerf-lkvh1u`. No beads for this work (Connor, 2026-10-08:
the cloud clone has no `.beads/`, and this work is tracked by this spec and its commits instead).

## 1. What Connor asked for, and the decisions behind it

The File Transfer screen on both the PC and the phone is replaced outright, not reskinned. It needs to be
modern and streamlined, with a folder tree for moving between levels. The phone gains the SHA-256
verification the PC already does silently, and both platforms get a live preview of files.

Every choice below was made by Connor in the planning Q&A. Don't re-litigate them; change this table first.

| Topic | Decision |
|---|---|
| PC layout | Explorer three-pane: **tree | contents | preview**, transfer strip along the bottom |
| PC tree | One tree. Top-level nodes are **This PC** and **each connected phone** |
| Android layout | Adaptive. Expanded width: permanent tree + contents + preview. Compact: tree in a drawer, breadcrumbs, full-screen preview |
| Android scope | Two-sided: the tree holds **This phone** and **PC** |
| "This phone" contents | The folders already shared under *Access from your PC*, plus **phone-only bookmarks** added with the system folder picker. A bookmark is never visible to the PC until its own **Share with PC** toggle is turned on |
| SHA-256 features | All four: a **Verified** badge on finished transfers (both platforms), **view and copy** a file's hash, **compare against a pasted hash**, and **verify the PC copy against the phone copy** |
| Hash display | Lowercase hex (`sha256sum` / `Get-FileHash` style). The wire stays Base64. A pasted hash may be hex in either case, or Base64 |
| Preview | Follows the selection. **Images at full quality** with zoom and pan. **Text, code and logs** with live tail. No video, audio or PDF in this round |
| Preview transport | One new message pair: `file_read_range_request` / `file_read_range_response` |
| Preview limits | Text: first 2 MiB, or the last 256 KiB when tailing. Live tail polls every 2 s while visible. Images up to 40 MiB, fetched in 1 MiB reads with progress |
| Phone hashing from the PC | `file_hash_request` is relayed to phones: read-only, behind the same gate as browse, re-checked per request |
| PC extras | Thumbnails in list and grid, right-click menus, keyboard shortcuts, drag between devices |
| Old UI | Replaced outright. Tests that pinned the old structure are rewritten to keep their intent |

## 2. Protocol changes (both sides change together)

### 2.1 `file_read_range_request` / `file_read_range_response` (new)

```jsonc
// requester → host
{ "requestId": "…", "rootId": "…", "relativePath": "Logs/app.log",
  "offset": 0,          // bytes from the start; ignored when fromEnd is true
  "length": 1048576,    // 1 … FileTransferLimits.ReadRangeMaxBytes (1 MiB)
  "fromEnd": false }    // true: read the LAST `length` bytes (tail mode)

// host → requester
{ "requestId": "…",
  "offset": 0,            // where the returned bytes actually start
  "dataBase64": "…",      // ≤ 1 MiB raw (≈1.37 MB encoded; MessageSerializer caps a message at 4 MB)
  "fileSize": 52344,      // size at read time, so a tailing reader sees growth or truncation
  "modifiedUtc": 1759900000000,
  "eof": true,
  "errorMessage": null }
```

- **Host rules (PC `FileTransferService`, phone `FileHostHandler`).** The path resolves exactly as a download's
  source does (PC: `SharedRootReadResolver`; phone: `SharedPathPolicy` plus the current *Access from your PC*
  roots, re-read per request). Directories are refused. `offset` past the end returns zero bytes with
  `eof: true` rather than an error. The PC opens with `FileShare.ReadWrite | FileShare.Delete` so a log
  another process is still writing can be tailed.
- **Validation** lives in `remex.core/Validation/` (offset ≥ 0, 1 ≤ length ≤ 1 MiB, the usual
  `FilePathValidation` root id and path rules). It is NativeAOT-safe, and the records get `[JsonSerializable]` entries.
- **Capability.** `FileCapabilities.readRange` (bool, additive, default false). Peers that don't send it get
  the fallback in §2.4.

### 2.2 `file_hash_request` relayed to phones (widened)

- The phone's `FileHostHandler` answers `file_hash_request` (new handler), with the same read gate as §2.1.
  It streams through `MessageDigest` and is cancellable, and it never requires *Let your PC change files*.
- `FileCapabilities.hash` (bool, additive, default false) says the host answers it. The PC already did.
- `PhoneFileRelay.RequestForReply` gains `file_hash_response → file_hash_request` and
  `file_read_range_response → file_read_range_request`. **This is the product decision** the relay guard in
  `docs/REGRESSION-GUARDS.md` says a widening needs. That guard entry is updated by hand, and
  `TheAllowlist_HoldsTheSevenReadOnlyTypesAndManage_AndNotRootManagement` is rewritten to pin nine read-only
  types plus manage. Root management stays out, and its refusal test is unchanged.
- The relay timeout for hashing a large phone file is longer, like copy/move's.

### 2.3 Hash display

`HashFormat` in `remex.core` (C#) and a Kotlin twin: `ToHex(base64)`, and `TryNormalize(input)`, which accepts
64 hex chars (any case, spaces and colons ignored) or 44-char Base64 and yields the canonical bytes. Compare
is a constant-time byte compare. Both platforms run one shared set of test vectors.

### 2.4 Mixed versions

| Peer lacks | What the screen does |
|---|---|
| `readRange` | Preview falls back to the existing thumbnail (images) or details only, plus a plain line: *"Update RemEx on your phone (or PC) to preview this file."* |
| `hash` | **Compute SHA-256** is hidden for that device. A transfer's Verified badge still works, because that hash comes from the transfer itself |

## 3. PC screen (`remex.desktop` + `remex.agent`)

```
┌──────────────┬─────────────────────────────────────────┬──────────────────┐
│ ▾ This PC    │ [This PC › Documents › 2026]  🔍  ⟳  ⋯  │  IMG_0142.jpg    │
│   ▸ Documents│ [⬆ Upload] [⬇ Download] [+ Folder]  ☰ ▦ │  ┌────────────┐  │
│   ▾ Photos   │ ─────────────────────────────────────── │  │  (image,   │  │
│     ▸ 2026   │ 🖼 IMG_0142.jpg     2.1 MB   Oct 6      │  │ zoom/pan)  │  │
│ ▾ Pixel 9    │ 📁 Taxes            —        Sep 30     │  └────────────┘  │
│   ▸ DCIM     │ 📄 notes.txt        4 KB     Oct 1      │  2.1 MB · JPEG   │
│   ▸ Download │                                         │  SHA-256 [Compute]│
├──────────────┴─────────────────────────────────────────┴──────────────────┤
│ ⇅ IMG_0142.jpg → Pixel 9/DCIM  ███████░░ 72%   ✓ 3 verified   [Show all ▴]│
└───────────────────────────────────────────────────────────────────────────┘
```

- **Tree (left, resizable with a `GridSplitter`, default 240 px).** Top-level nodes are **This PC** and each
  paired phone that's connected right now (`IPhoneFileAccess`). A disconnected phone disappears, and if it was
  open, the contents pane says so in plain words. Under each device are its shared roots (and, for a phone with
  whole-device browsing on, its volumes). Children load lazily on expand through `file_browse_request`, folders
  only. Selecting a node navigates the contents pane, and navigating the contents pane (double-click,
  breadcrumb, Up) expands and selects the matching node. **Pin** and **Remove root** move to the tree node's
  context menu.
- **Contents (centre).**
  - One command bar replaces the 13-button wrap panel. It holds Upload, Download, and New folder, with the
    selection-dependent actions in an overflow menu. It keeps exactly one primary button
    (`ButtonVocabularyTests`).
  - Breadcrumbs include the device name, and search sits in the bar.
  - The details list and the icon grid both show **real thumbnails** for image files. They load lazily for
    visible items only, go through an LRU cache keyed by device+root+path+modified time, and reuse the existing
    `file_thumbnail_request`.
  - Multi-select works in **both** views. This fixes today's bug where grid selection never reached the
    view model.
- **Right-click menus** on items, empty space and tree nodes: Open/Preview, Download to…, Send to *device*…,
  Rename, Copy, Cut, Paste, Delete, Copy path, Compute SHA-256, Properties. Items the current device can't do
  are hidden, not greyed (`CanChangeFiles`, `CanManageFiles`).
- **Keyboard:** Space toggles the preview pane, Enter opens a folder, Backspace / Alt+↑ goes up, F2 renames, Del
  deletes (with confirmation), Ctrl+C/X/V, Ctrl+A, F5 refreshes, Ctrl+F searches. They're bound with
  `KeyBinding`s on the view, never injected from scripts (UI-verification rule).
- **Drag and drop.**
  - Dropping OS files onto the contents pane or a tree folder uploads them (this exists today).
  - Dragging items onto a tree folder **of another device** queues a transfer into that folder.
  - On the **same device**, dragging moves the items, or copies them with Ctrl held (same-root rule as Paste
    today).
  - Dragging **out** to Explorer is supported for **This PC** items only, because their shared root resolves to
    a real local path. Phone items offer *Download to…* instead, because Avalonia has no deferred
    (virtual-file) drag source.
- **Preview pane (right, resizable, collapsible with Space or a toggle, default 320 px).**
  - **It follows the selection.** It is debounced 150 ms, any in-flight read is cancelled when the selection
    moves, and reads start only while the pane is open.
  - **Image** (jpg, jpeg, png, gif, bmp, webp): range reads assemble the file, up to 40 MiB with a progress
    bar, and it's decoded with `Bitmap`. You can zoom with the wheel or pinch, pan by dragging, and fit with
    double-click. HEIC/HEIF and formats Skia can't decode show the host thumbnail plus *"Download to view
    full size"*.
  - **Text** (by extension plus a sniff: NUL bytes in the first 8 KiB mean binary): it's decoded with BOM
    detection, then UTF-8 with replacement. It shows in a monospace viewer with line numbers and light
    highlighting: log levels, JSON/XML strings, keys and comments, from a small pure tokenizer with tests.
    **Live** is a toggle that tails the last 256 KiB, polls `fromEnd` every 2 s while the pane is visible,
    appends growth, and resets when `fileSize` shrinks.
  - **Folder or other file:** a large icon and the details only.
  - **Details** are always shown: size, modified, created, type, read-only.
  - **Integrity section:**
    - **Compute SHA-256** shows the hex hash with a **Copy** button.
    - **Compare…** takes a pasted hash and shows *✓ Matches* or *✗ Doesn't match*.
    - **Verify against…** lets you pick the counterpart file on the other device in a tree picker. It defaults
      to the same name in that device's current folder. Both sides are hashed and it says *Identical* or
      *Different* in plain words.
- **Transfer strip (bottom).** A one-line summary expands into the full queue. The queue keeps its
  `VirtualizingStackPanel`, capped `ScrollViewer`, and Cancel all / Clear finished commands
  (`FileTransferQueueVirtualizationTests` keeps its intent). Finished rows say **✓ Verified** (tooltip: the hex
  hash, plus a Copy SHA-256 menu item) instead of "Done". `TransferState.Verifying` is actually set while the
  final hash compare runs.
- **Structure.** `FileTransferViewModel` (2,638 lines) is split rather than grown: `FileTreeViewModel`,
  `FolderContentsViewModel`, `FilePreviewViewModel` (with `ImagePreviewLoader` / `TextPreviewLoader`), and
  `IntegrityViewModel`, coordinated by a slimmer `FileTransferViewModel`. Transfer, conflict and queue logic is
  reused, not rewritten.
- **Localization.** Every new string is in all 9 `.resx` files, added with a Python script using explicit UTF-8.
  There's no interpolation in UI-bound properties.

## 4. Android screen (`remex.android`)

- **Adaptive layout (`currentWindowAdaptiveInfoV2()`, as `AppNavigation.kt` already does).**
  - *Expanded* (tablet, unfolded): a permanent tree pane (280 dp) next to a `ListDetailPaneScaffold`, with
    contents as the list and preview as the detail.
  - *Medium*: the tree goes in a `ModalNavigationDrawer`, and contents and preview sit side by side.
  - *Compact* (phone): the tree is in a drawer opened from the top-bar icon. Tappable breadcrumbs sit under the
    top bar. Tapping a file opens the preview full-screen, and predictive back returns to the list.
- **Tree.**
  - **This phone** lists the shared folders (*Access from your PC*, marked with a "Shared with PC" chip) and the
    **phone-only bookmarks**.
  - Bookmarks are added with **+ Add folder** (SAF `OpenDocumentTree` + `takePersistableUriPermission`) and
    stored in their own DataStore key. They're never read by `AndroidFileTransferHost`/`SharedPathPolicy`.
  - Each bookmark's menu has a **Share with PC** toggle, which adds it to (or removes it from) the existing
    *Access from your PC* list, the one consent surface. Removing a bookmark releases its URI grant unless it is
    still shared.
  - **PC** lists the PC's shared roots and, after *Browse device*, its volumes.
  - Local phone folders are listed with `DocumentsContract` child-document queries (not
    `DocumentFile.listFiles()`, which does one IPC per child).
- **Contents.** One `LazyColumn` (list) or `LazyVerticalGrid`, with thumbnails as today. Phone-local images use
  `ContentResolver.loadThumbnail`. Long-press starts multi-select. **Tapping a file opens the preview**, which
  fixes list view doing nothing today. The toolbar is reduced to search, sort/view menu and one primary action.
- **Cross-device actions.** *Send to PC…* (from a phone item) and *Save to phone…* (from a PC item) open a
  destination picker that is the same tree, limited to writable folders of the other device. They use the
  existing transfer engine with a document URI as source or target. The system-picker uploads stay as a
  secondary *Upload from…* action.
- **Preview.** It is the same model as the PC: debounced, cancellable, and range reads for PC files or direct
  `ContentResolver` streams for phone files.
  - Images are decoded with `ImageDecoder` (HEIC included), downsampled to the screen size, and support
    pinch-zoom and pan (`transformable`).
  - Text uses the same sniff, decode, tokenizer rules and live tail as the PC, in a `LazyColumn` of lines with
    line numbers.
  - The integrity card works as on the PC. Phone-local files are hashed on-device with `MessageDigest`, and PC
    files through `file_hash_request`. *Verify against…* opens the other device's tree picker.
- **Verified badge.** Finished queue rows and the finish notification say **Verified**. A row's menu offers
  *Copy SHA-256* (from `QueuedTransfer.sha256`, shown as hex). This is already computed today and only needs
  surfacing.
- **M3 Expressive**, all three theme paths (seed, dynamic, static fallback) × light/dark × contrast. 48 dp
  targets stay (`FileManagerGridItemTest`). Strings go in all 9 locale files via a Python UTF-8 script.
- **Structure.** `FileTransferViewModel.kt` (2,266 lines) is split the same way as the PC's: tree, contents,
  preview and integrity holders, plus a `PhoneBookmarksRepository`. Engine, queue and conflict code is reused.

## 5. Delivery: phases, each its own commit on this branch

| # | Phase | Main files | Proof |
|---|---|---|---|
| 1 | **Protocol + hosts.** Range-read messages, validation, capabilities, the PC host handler, the phone `handleReadRange` + `handleHash`, the relay allowlist, `HashFormat` (C# + Kotlin) | `remex.core/Models/FileTransferMessages.cs`, `remex.core/Validation/*`, `remex.agent/Handlers/FileTransferHandler.cs`, `FileTransferService.cs`, `PhoneFileRelay.cs`, `FileHostHandler.kt` | Serialization round-trip, validation, handler, relay allowlist and phone host unit tests. Each new guard is defect-injected per hard rule 5 |
| 2 | **PC client plumbing.** `FileTransferClient.ReadRangeAsync`, a Verified state and hash on queue items, preview loaders, text sniff/decoder/tokenizer | `remex.desktop/Services/FileTransfer/*`, `FileTransferQueue.cs` | VM/service unit tests |
| 3 | **PC screen.** Tree, contents, preview, integrity, context menus, shortcuts, drag and drop, transfer strip, 9 locales | `remex.desktop/Views/FileTransferView.axaml(.cs)`, new view models | Rewritten view tests, render tests where `remex.desktop.render.tests` can host them |
| 4 | **Android plumbing.** Bookmarks repository, local `DocumentsContract` lister, range-read and hash client calls, preview loaders, Kotlin tokenizer | `service/*`, new `ui/files/*` | JVM unit tests |
| 5 | **Android screen.** Adaptive scaffold, tree, contents, preview, integrity, cross-device actions, 9 locales | `ui/screens/FileTransferScreen.kt` → `ui/files/*` | Unit tests, release lint |
| 6 | **Docs.** `CHANGELOG.md`, `API_CONTRACTS.md` §3, `FILE_SHARING.md`, `REGRESSION-GUARDS.md` (hand edit), XML/KDoc | — | `InstructionFileTests`, translations check |

The gate is `pwsh ./scripts/verify.ps1` after each phase, and `-Scope all` once the Android SDK in this container
builds. A commit lands only on a passing gate (standing authorization). Pushes to this branch are explicitly
requested for this work.

## 6. Known limits of this round

- **No UI screenshot verification in this cloud session.** The `ui-verify` skill drives a live Windows window.
  The PC screen should go through the 13-cell palette sweep (`scripts/ui-palette-sweep.ps1`) on Connor's
  machine, and the Android screen on the AVD, before release.
- **PC preview of HEIC/HEIF** uses the host thumbnail. Full-size needs a decoder Skia lacks on Windows.
- **Dragging phone files out to Explorer** isn't possible without a virtual-file drag source. They get
  *Download to…* instead.
- **Video, audio and PDF preview** were deliberately left out. The range-read message is the transport they'd
  need later.
- **Linux parity.** Everything here is cross-platform Avalonia, with no Windows-only APIs. The Windows-specific
  check is the palette sweep above.

## 7. As delivered (2026-10-08)

Phases 1–6 landed on `claude/clever-cerf-lkvh1u`, one commit each. Where the build differs from the plan above:

- **Medium-width Android windows** use the list-detail scaffold's own default, one pane at a time (the preview opens
  over the list), rather than contents and preview side by side. With the tree also on screen, a 600–840 dp window
  left each pane too narrow to read. Expanded windows get all three panes as planned.
- **"Verify against…"** on the PC takes the other device, folder and path in fields (defaulting to the same name in
  the last folder looked at there); on Android it opens the other device's folder picker.
- **"Send to PC…" and "Save to phone…" move files, not folders.** A folder in the selection is skipped and the status
  line says so; whole folders still go through *Upload folder* and *Download folder*.
- **Phone-side management** (rename, delete, new folder on the phone's own folders) was not part of this round; the
  phone's own file manager does that. The PC still manages its own folders, and the phone's when the person allows it.
- The toolbar's write actions became one primary button plus **More** on both platforms, as planned.
- **Mixed versions (§2.4)** on the phone: a PC without `readRange` gets details plus "Update RemEx on your PC to preview
  its files here" instead of the thumbnail, and nothing is sent to it; without `hash`, the fingerprint and cross-check
  buttons give way to a line saying the same.
- **Phone files are addressed by name.** A provider that lists two siblings with one name (some cloud providers can)
  shows both rows, keyed by document id so the list never crashes, but opening, previewing or sending either one acts
  on the first. Built-in storage never does this.

Verification in the cloud session: `verify.ps1 -Scope all` on Linux (Android unit tests and `lintRelease` included),
headless render tests with PNG snapshots of the PC screen, and defect injection for every new guard. Still to do on
Connor's machines: the 13-cell `ui-palette-sweep.ps1` run on Windows, and the Android screen on the AVD across
seed/dynamic/static colour, light/dark and contrast.
