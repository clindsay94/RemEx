/*
 * remex_linux_bridge.h
 *
 * C ABI header for the RemEx Linux native helper library.
 * Consumed from C# via P/Invoke / NativeLibrary.GetExport.
 *
 * All functions use a flat C ABI so the P/Invoke surface stays simple.
 * Ownership rules:
 *   - byte arrays returned through out-pointers are valid until the next call
 *     to the same session's acquire function or until the session is destroyed.
 *   - Strings are null-terminated UTF-8 and must NOT be freed by the caller.
 *   - Callers must be prepared for any function to return a failure code.
 *
 * Error codes
 *   0  = REMEX_OK
 *  -1  = REMEX_ERR_GENERIC
 *  -2  = REMEX_ERR_NOT_INITIALIZED
 *  -3  = REMEX_ERR_PIPEWIRE_UNAVAILABLE
 *  -4  = REMEX_ERR_NO_FRAME
 *  -5  = REMEX_ERR_BUFFER_TOO_SMALL
 *  -6  = REMEX_ERR_DMABUF_UNAVAILABLE
 *  -7  = REMEX_ERR_EIS_UNAVAILABLE
 *  -8  = REMEX_ERR_UINPUT_UNAVAILABLE
 *  -9  = REMEX_ERR_PERMISSION_DENIED
 */

#pragma once

#include <stdint.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C"
{
#endif

/* ── Error constants ───────────────────────────────────────────────────── */
#define REMEX_OK 0
#define REMEX_ERR_GENERIC -1
#define REMEX_ERR_NOT_INITIALIZED -2
#define REMEX_ERR_PIPEWIRE_UNAVAILABLE -3
#define REMEX_ERR_NO_FRAME -4
#define REMEX_ERR_BUFFER_TOO_SMALL -5
#define REMEX_ERR_DMABUF_UNAVAILABLE -6
#define REMEX_ERR_EIS_UNAVAILABLE -7
#define REMEX_ERR_UINPUT_UNAVAILABLE -8
#define REMEX_ERR_PERMISSION_DENIED -9

/* ── Buffer kind ───────────────────────────────────────────────────────── */
#define REMEX_BUFFER_KIND_MEMFD 0
#define REMEX_BUFFER_KIND_DMABUF 1

    /* ── Frame descriptor ──────────────────────────────────────────────────── */
    typedef struct remex_frame_descriptor
    {
        int buffer_kind;      /* REMEX_BUFFER_KIND_* */
        int fd;               /* memfd or DMA-BUF fd; -1 if not applicable */
        void *data;           /* mapped pointer for memfd path; NULL for DMA-BUF */
        size_t size;          /* byte length of the mapped region */
        int width;            /* pixel width */
        int height;           /* pixel height */
        int stride;           /* bytes per row */
        uint32_t format;      /* DRM fourcc or SPA_VIDEO_FORMAT_* */
        int64_t timestamp_ns; /* monotonic capture time, nanoseconds */
        uint64_t seq;         /* monotonically increasing frame counter */
    } remex_frame_descriptor_t;

    /* ── PipeWire capture session ──────────────────────────────────────────── */

    /*
     * remex_pw_session_create
     * Opens a PipeWire remote (connecting to the running PipeWire daemon) and
     * creates a ScreenCast consumer stream linked to `node_id`.
     *
     * node_id: PipeWire node ID returned by the portal ScreenCast session start.
     *          Pass 0 to auto-select the first available monitor node.
     * out_handle: receives an opaque session handle on success.
     *
     * Returns REMEX_OK on success, negative error code on failure.
     */
    int remex_pw_session_create(uint32_t node_id, void **out_handle);

    /*
     * remex_pw_session_create_v2
     * Like remex_pw_session_create but accepts the portal D-Bus session handle
     * so the native bridge can call OpenPipeWireRemote to get the PipeWire fd.
     *
     * portal_session_handle: D-Bus object path returned by the portal CreateSession
     *   call (e.g. "/org/freedesktop/portal/desktop/session/...").
     *   Pass NULL to skip the portal fd call and fall back to a plain daemon connect.
     * node_id: PipeWire node ID from the portal Start result; 0 = auto-select.
     * out_handle: receives an opaque session handle on success.
     *
     * Returns REMEX_OK on success, negative error code on failure.
     */
    int remex_pw_session_create_v2(
        const char *portal_session_handle,
        uint32_t node_id,
        void **out_handle);

    /*
     * remex_pw_session_acquire_frame
     * Acquires the latest available frame from the PipeWire stream.
     * Blocks up to timeout_ms milliseconds if no frame is ready.
     *
     * The descriptor is valid until the next call to this function or
     * remex_pw_session_destroy on the same handle.
     *
     * Returns REMEX_OK on success, REMEX_ERR_NO_FRAME on timeout,
     *         or a negative error code on failure.
     */
    int remex_pw_session_acquire_frame(
        void *handle,
        remex_frame_descriptor_t *out_descriptor,
        int timeout_ms);

    /*
     * remex_pw_session_release_frame
     * Returns the current frame buffer to PipeWire so it can be reused.
     * Must be called after processing each acquired frame.
     */
    void remex_pw_session_release_frame(void *handle);

    /*
     * remex_pw_session_destroy
     * Destroys the PipeWire session and frees all associated resources.
     * Safe to call with a NULL handle.
     */
    void remex_pw_session_destroy(void *handle);

    /* ── EIS / libei sender ─────────────────────────────────────────────────── */

    /*
     * remex_eis_sender_create
     * Opens an EIS sender connection through the portal remote desktop session.
     * eis_socket_path: path to the EIS socket provided by the portal.
     * out_handle: receives an opaque EIS sender handle on success.
     */
    int remex_eis_sender_create(const char *eis_socket_path, void **out_handle);

    /*
     * remex_eis_send_pointer_motion
     * Sends a relative pointer motion event.
     */
    int remex_eis_send_pointer_motion(void *handle, double dx, double dy);

    /*
     * remex_eis_send_pointer_motion_absolute
     * Sends an absolute pointer position event (normalized 0.0-1.0 coordinates).
     */
    int remex_eis_send_pointer_motion_absolute(void *handle, double x, double y);

    /*
     * remex_eis_send_button
     * Sends a pointer button event. button follows Linux BTN_* constants.
     * pressed: 1 for press, 0 for release.
     */
    int remex_eis_send_button(void *handle, uint32_t button, int pressed);

    /*
     * remex_eis_send_scroll
     * Sends a scroll (axis) event. Values are in pixels.
     */
    int remex_eis_send_scroll(void *handle, double dx, double dy);

    /*
     * remex_eis_send_key
     * Sends a keyboard key event. keycode follows Linux KEY_* constants.
     * pressed: 1 for press, 0 for release.
     */
    int remex_eis_send_key(void *handle, uint32_t keycode, int pressed);

    /*
     * remex_eis_sender_destroy
     * Closes and frees the EIS sender. Safe to call with NULL.
     */
    void remex_eis_sender_destroy(void *handle);

    /* ── uinput virtual tablet ──────────────────────────────────────────────── */

    /*
     * remex_uinput_tablet_create
     * Creates a virtual evdev tablet device via /dev/uinput.
     * device_name: displayed in /proc/bus/input/devices.
     * supports_pressure, supports_tilt, supports_distance: capability flags.
     * out_handle: receives an opaque tablet handle on success.
     */
    int remex_uinput_tablet_create(
        const char *device_name,
        int supports_pressure,
        int supports_tilt,
        int supports_distance,
        void **out_handle);

    /*
     * remex_uinput_tablet_send_stylus_event
     * Sends a complete stylus frame: position, pressure, tilt, buttons, phase.
     * All values are raw integers matching the evdev axis ranges configured at device creation.
     */
    int remex_uinput_tablet_send_stylus_event(
        void *handle,
        int32_t abs_x,
        int32_t abs_y,
        int32_t pressure, /* 0-65535 */
        int32_t tilt_x,   /* -64 to 63, degrees */
        int32_t tilt_y,
        int32_t distance,     /* 0-65535, hover distance */
        uint32_t button_mask, /* bit0=BTN_TOUCH, bit1=BTN_STYLUS, bit2=BTN_STYLUS2 */
        int tool_pen,         /* 1 when pen tool is active */
        int tool_rubber);     /* 1 when eraser tool is active */

    /*
     * remex_uinput_tablet_reset
     * Clears all button and tool state. Call on disconnect or session teardown
     * to prevent stuck keys or stuck tool state.
     */
    int remex_uinput_tablet_reset(void *handle);

    /*
     * remex_uinput_tablet_destroy
     * Destroys the virtual device. Automatically calls reset first.
     * Safe to call with NULL.
     */
    void remex_uinput_tablet_destroy(void *handle);

    /* ── uinput keyboard/pointer fallback ──────────────────────────────────── */

    /*
     * remex_uinput_kbptr_create
     * Creates a virtual keyboard + relative pointer device for the X11 fallback path.
     */
    int remex_uinput_kbptr_create(const char *device_name, void **out_handle);

    int remex_uinput_kbptr_send_key(void *handle, uint32_t keycode, int pressed);
    int remex_uinput_kbptr_send_rel(void *handle, int32_t dx, int32_t dy);
    int remex_uinput_kbptr_send_button(void *handle, uint32_t button, int pressed);
    int remex_uinput_kbptr_reset(void *handle);
    void remex_uinput_kbptr_destroy(void *handle);

    /* ── Capability probe ───────────────────────────────────────────────────── */

    /*
     * remex_probe_capabilities
     * Probes the native library's runtime capability set.
     * Writes JSON into out_buf (null-terminated UTF-8).
     * Returns the number of bytes written (excluding null) or negative on error.
     */
    int remex_probe_capabilities(char *out_buf, size_t buf_size);

#ifdef __cplusplus
}
#endif
