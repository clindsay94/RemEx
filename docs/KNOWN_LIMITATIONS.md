# RemEx — Known Limitations

## Canvas Snapshot — "Copy Path" copies file path, not bitmap

The canvas snapshot "Copy Path" button saves the canvas to a temporary PNG file and copies the file path to the clipboard rather than placing the bitmap directly on the clipboard.

**Reason:** Placing a bitmap object directly on the system clipboard requires platform-conditional code (Windows `DataObject`, X11 clipboard atoms, Wayland `wl-data-device`). A cross-platform implementation is deferred to 2.x.

**Workaround:** Open the pasted path in any image viewer to view the snapshot.
