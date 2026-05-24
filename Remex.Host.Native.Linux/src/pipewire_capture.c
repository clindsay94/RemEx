/*
 * pipewire_capture.c
 *
 * PipeWire screen-cast capture session implementation for RemEx.
 *
 * Architecture:
 *   - Opens a PipeWire remote per capture session. When a portal session handle
 *     is provided (via remex_pw_session_create_v2), calls OpenPipeWireRemote via
 *     sd-bus to obtain the portal-scoped fd and connects with pw_context_connect_fd.
 *     This is required on KDE Plasma 6 / GNOME where screencast nodes have explicit
 *     PipeWire ACLs that are only visible through the portal-provided fd.
 *   - Creates a pw_stream in CONSUME mode linked to the node_id from the portal.
 *   - Uses MemFd/MemPtr buffers with PW_STREAM_FLAG_MAP_BUFFERS (no DMA-BUF).
 *   - Latest-frame semantics: each on_process callback overwrites the frame buffer.
 *   - Frame acquisition is synchronous from the C# caller side — PipeWire events
 *     run on a dedicated pw_thread inside this library.
 *
 * Thread model:
 *   pw_thread  ← PipeWire main loop (stream callbacks, buffer events)
 *   caller     ← calls remex_pw_session_acquire_frame / release_frame
 *   frame_mutex + frame_cond synchronize the two sides.
 *
 * Build flags:
 *   REMEX_HAS_PIPEWIRE — defined by CMake when libpipewire-0.3 headers found;
 *                         enables the real PipeWire implementation.
 *   REMEX_HAS_SDBUS    — defined by CMake when libsystemd headers found;
 *                         enables portal fd acquisition via sd-bus.
 */

#include "../include/remex_linux_bridge.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <errno.h>
#include <time.h>
#include <pthread.h>
#include <unistd.h>
#include <dlfcn.h>

#ifdef REMEX_HAS_PIPEWIRE
#include <pipewire/pipewire.h>
#include <pipewire/stream.h>
#include <pipewire/keys.h>
#include <spa/param/video/format-utils.h>
#include <spa/param/buffers.h>
#include <spa/pod/builder.h>
#include <spa/utils/result.h>
#include <spa/utils/defs.h>
#include <spa/buffer/buffer.h>
#endif

#ifdef REMEX_HAS_SDBUS
#include <systemd/sd-bus.h>
#endif

/* ── Session state ────────────────────────────────────────────────────────── */

typedef struct remex_pw_session
{
    /* dlopen handle for remex_probe_capabilities on non-REMEX_HAS_PIPEWIRE builds;
     * NULL when libpipewire is hard-linked. */
    void *pw_lib;

    /* PipeWire main loop pointer. Stored under frame_mutex so destroy can call
     * pw_main_loop_quit thread-safely. Cast to struct pw_main_loop * in code
     * guarded by REMEX_HAS_PIPEWIRE. */
    void *loop;

    /* pw_stream pointer; NULL until stream is created. */
    void *stream;

#ifdef REMEX_HAS_PIPEWIRE
    struct spa_hook stream_listener;
    struct spa_video_info_raw video_info;
#endif

    /* Portal D-Bus session handle (owned copy, freed in destroy). */
    char *portal_session_handle;

    /* Session-owned frame copy buffer (avoids allocating per-frame). */
    void *frame_data_buf;
    size_t frame_data_cap;

    pthread_t pw_thread;
    pthread_mutex_t frame_mutex;
    pthread_cond_t frame_cond;

    remex_frame_descriptor_t current_frame;
    int frame_ready;
    int shutting_down;

    uint32_t node_id;
} remex_pw_session_t;

/* ── Forward declarations ─────────────────────────────────────────────────── */
static void *pw_thread_func(void *arg);

/* ── sd-bus helper: OpenPipeWireRemote ────────────────────────────────────── */
/*
 * Calls org.freedesktop.portal.ScreenCast.OpenPipeWireRemote on the user bus
 * for the given portal session handle. Returns the PipeWire fd on success
 * (caller owns it and must close it), or -1 on failure.
 *
 * This fd is required by pw_context_connect_fd so PipeWire can see the
 * ACL-protected screencast node granted by the portal.
 */
static int open_pipewire_remote_sdbus(const char *session_handle)
{
#ifdef REMEX_HAS_SDBUS
    sd_bus *bus = NULL;
    sd_bus_message *msg = NULL;
    sd_bus_message *reply = NULL;
    sd_bus_error error = SD_BUS_ERROR_NULL;
    int pw_fd = -1;
    int r;

    r = sd_bus_open_user(&bus);
    if (r < 0)
    {
        fprintf(stderr, "[remex] sd_bus_open_user failed: %s\n", strerror(-r));
        goto done;
    }

    r = sd_bus_message_new_method_call(
        bus, &msg,
        "org.freedesktop.portal.Desktop",
        "/org/freedesktop/portal/desktop",
        "org.freedesktop.portal.ScreenCast",
        "OpenPipeWireRemote");
    if (r < 0)
        goto done;

    r = sd_bus_message_append(msg, "o", session_handle);
    if (r < 0)
        goto done;

    /* Empty options dict a{sv} */
    r = sd_bus_message_open_container(msg, SD_BUS_TYPE_ARRAY, "{sv}");
    if (r < 0)
        goto done;
    r = sd_bus_message_close_container(msg);
    if (r < 0)
        goto done;

    r = sd_bus_call(bus, msg, 5000000 /* 5s timeout */, &error, &reply);
    if (r < 0)
    {
        /* Non-fatal on unsandboxed hosts: fall back to a direct PipeWire daemon
         * connection and let the caller decide whether capture still succeeds. */
        goto done;
    }

    /* 'h' reads a Unix fd; sd-bus dups it so we own the result. */
    r = sd_bus_message_read(reply, "h", &pw_fd);
    if (r < 0)
    {
        pw_fd = -1;
        goto done;
    }

done:
    sd_bus_error_free(&error);
    if (reply)
        sd_bus_message_unref(reply);
    if (msg)
        sd_bus_message_unref(msg);
    if (bus)
        sd_bus_unref(bus);

    return pw_fd;
#else
    (void)session_handle;
    return -1;
#endif
}

/* ── PipeWire stream callbacks ───────────────────────────────────────────── */

#ifdef REMEX_HAS_PIPEWIRE

static void on_stream_param_changed(void *userdata, uint32_t id,
                                    const struct spa_pod *param)
{
    remex_pw_session_t *sess = (remex_pw_session_t *)userdata;
    if (param == NULL || id != SPA_PARAM_Format)
        return;

    if (spa_format_video_raw_parse(param, &sess->video_info) < 0)
        return;

    int width  = (int)sess->video_info.size.width;
    int height = (int)sess->video_info.size.height;
    int stride = SPA_ROUND_UP_N(width * 4, 4);

    uint8_t buf[1024];
    struct spa_pod_builder pb = SPA_POD_BUILDER_INIT(buf, sizeof(buf));
    const struct spa_pod *params[1];

    params[0] = spa_pod_builder_add_object(
        &pb,
        SPA_TYPE_OBJECT_ParamBuffers, SPA_PARAM_Buffers,
        SPA_PARAM_BUFFERS_buffers,    SPA_POD_Int(2),
        SPA_PARAM_BUFFERS_blocks,     SPA_POD_Int(1),
        SPA_PARAM_BUFFERS_size,       SPA_POD_Int(stride * height),
        SPA_PARAM_BUFFERS_stride,     SPA_POD_Int(stride),
        SPA_PARAM_BUFFERS_dataType,   SPA_POD_CHOICE_FLAGS_Int(
            (1 << SPA_DATA_MemPtr) | (1 << SPA_DATA_MemFd)));

    pw_stream_update_params((struct pw_stream *)sess->stream, params, 1);
}

static void on_process(void *userdata)
{
    remex_pw_session_t *sess = (remex_pw_session_t *)userdata;
    struct pw_buffer *pw_buf;

    while ((pw_buf = pw_stream_dequeue_buffer(
                (struct pw_stream *)sess->stream)) != NULL)
    {
        struct spa_buffer *sbuf  = pw_buf->buffer;
        struct spa_data   *sdata = &sbuf->datas[0];

        if (sdata->data != NULL && sdata->chunk->size > 0 &&
            (sdata->type == SPA_DATA_MemPtr ||
             sdata->type == SPA_DATA_MemFd))
        {
            size_t bytes = sdata->chunk->size;

            pthread_mutex_lock(&sess->frame_mutex);

            if (bytes > sess->frame_data_cap)
            {
                void *nb = realloc(sess->frame_data_buf, bytes);
                if (nb)
                {
                    sess->frame_data_buf = nb;
                    sess->frame_data_cap = bytes;
                }
            }

            if (sess->frame_data_buf && bytes <= sess->frame_data_cap)
            {
                memcpy(sess->frame_data_buf, sdata->data, bytes);

                sess->current_frame.buffer_kind = REMEX_BUFFER_KIND_MEMFD;
                sess->current_frame.fd          = -1;
                sess->current_frame.data        = sess->frame_data_buf;
                sess->current_frame.size        = bytes;
                sess->current_frame.width       = (int)sess->video_info.size.width;
                sess->current_frame.height      = (int)sess->video_info.size.height;
                sess->current_frame.stride      = sdata->chunk->stride;
                sess->current_frame.format      = (uint32_t)sess->video_info.format;
                /* pw_buf->time is the monotonic capture timestamp in nanoseconds
                 * (uint64_t, added in PipeWire 1.0.5). Cast to int64_t for the ABI. */
                sess->current_frame.timestamp_ns = (int64_t)pw_buf->time;
                sess->current_frame.seq++;

                sess->frame_ready = 1;
                pthread_cond_signal(&sess->frame_cond);
            }

            pthread_mutex_unlock(&sess->frame_mutex);
        }

        /* Return the buffer to PipeWire immediately — latest-frame semantics. */
        pw_stream_queue_buffer((struct pw_stream *)sess->stream, pw_buf);
    }
}

static const struct pw_stream_events stream_events = {
    PW_VERSION_STREAM_EVENTS,
    .param_changed = on_stream_param_changed,
    .process       = on_process,
};

#endif /* REMEX_HAS_PIPEWIRE */

/* ── Public API implementation ────────────────────────────────────────────── */

int remex_pw_session_create_v2(
    const char *portal_session_handle,
    uint32_t node_id,
    void **out_handle)
{
    if (!out_handle)
        return REMEX_ERR_GENERIC;

    remex_pw_session_t *sess = calloc(1, sizeof(remex_pw_session_t));
    if (!sess)
        return REMEX_ERR_GENERIC;

    sess->node_id = node_id;

    if (portal_session_handle && *portal_session_handle)
    {
        sess->portal_session_handle = strdup(portal_session_handle);
        if (!sess->portal_session_handle)
        {
            free(sess);
            return REMEX_ERR_GENERIC;
        }
    }

#ifndef REMEX_HAS_PIPEWIRE
    /* Not built with PipeWire headers — try dlopen so probe_capabilities works. */
    const char *pw_libs[] = {"libpipewire-0.3.so.0", "libpipewire-0.3.so", NULL};
    for (int i = 0; pw_libs[i]; i++)
    {
        sess->pw_lib = dlopen(pw_libs[i], RTLD_NOW | RTLD_LOCAL);
        if (sess->pw_lib)
            break;
    }
    if (!sess->pw_lib)
    {
        fprintf(stderr, "[remex] libpipewire not found: %s\n", dlerror());
        free(sess->portal_session_handle);
        free(sess);
        return REMEX_ERR_PIPEWIRE_UNAVAILABLE;
    }
#endif

    if (pthread_mutex_init(&sess->frame_mutex, NULL) != 0)
        goto fail_mutex;

    if (pthread_cond_init(&sess->frame_cond, NULL) != 0)
        goto fail_cond;

    memset(&sess->current_frame, 0, sizeof(sess->current_frame));
    sess->current_frame.fd = -1;

    if (pthread_create(&sess->pw_thread, NULL, pw_thread_func, sess) != 0)
        goto fail_thread;

    *out_handle = sess;
    return REMEX_OK;

fail_thread:
    pthread_cond_destroy(&sess->frame_cond);
fail_cond:
    pthread_mutex_destroy(&sess->frame_mutex);
fail_mutex:
    if (sess->pw_lib)
        dlclose(sess->pw_lib);
    free(sess->portal_session_handle);
    free(sess->frame_data_buf);
    free(sess);
    return REMEX_ERR_GENERIC;
}

int remex_pw_session_create(uint32_t node_id, void **out_handle)
{
    return remex_pw_session_create_v2(NULL, node_id, out_handle);
}

int remex_pw_session_acquire_frame(
    void *handle,
    remex_frame_descriptor_t *out_descriptor,
    int timeout_ms)
{
    if (!handle || !out_descriptor)
        return REMEX_ERR_GENERIC;
    remex_pw_session_t *sess = (remex_pw_session_t *)handle;

    struct timespec deadline;
    clock_gettime(CLOCK_REALTIME, &deadline);
    long ns = (long)timeout_ms * 1000000L;
    deadline.tv_sec  += ns / 1000000000L;
    deadline.tv_nsec += ns % 1000000000L;
    if (deadline.tv_nsec >= 1000000000L)
    {
        deadline.tv_sec++;
        deadline.tv_nsec -= 1000000000L;
    }

    pthread_mutex_lock(&sess->frame_mutex);
    while (!sess->frame_ready && !sess->shutting_down)
    {
        int rc = pthread_cond_timedwait(&sess->frame_cond, &sess->frame_mutex, &deadline);
        if (rc == ETIMEDOUT)
        {
            pthread_mutex_unlock(&sess->frame_mutex);
            return REMEX_ERR_NO_FRAME;
        }
    }

    if (sess->shutting_down)
    {
        pthread_mutex_unlock(&sess->frame_mutex);
        return REMEX_ERR_NOT_INITIALIZED;
    }

    *out_descriptor = sess->current_frame;
    sess->frame_ready = 0;
    pthread_mutex_unlock(&sess->frame_mutex);
    return REMEX_OK;
}

void remex_pw_session_release_frame(void *handle)
{
    /* Latest-frame model: frame data is copied into session-owned memory in
     * on_process and the PipeWire buffer is immediately returned. The caller
     * does not hold any PipeWire resources between acquire and release. */
    (void)handle;
}

void remex_pw_session_destroy(void *handle)
{
    if (!handle)
        return;
    remex_pw_session_t *sess = (remex_pw_session_t *)handle;

    pthread_mutex_lock(&sess->frame_mutex);
    sess->shutting_down = 1;
    pthread_cond_broadcast(&sess->frame_cond);

#ifdef REMEX_HAS_PIPEWIRE
    if (sess->loop)
        pw_main_loop_quit((struct pw_main_loop *)sess->loop);
#endif

    pthread_mutex_unlock(&sess->frame_mutex);

    pthread_join(sess->pw_thread, NULL);

    pthread_cond_destroy(&sess->frame_cond);
    pthread_mutex_destroy(&sess->frame_mutex);

    free(sess->portal_session_handle);
    free(sess->frame_data_buf);

    if (sess->pw_lib)
        dlclose(sess->pw_lib);

    free(sess);
}

/* ── PipeWire thread ──────────────────────────────────────────────────────── */

static void *pw_thread_func(void *arg)
{
    remex_pw_session_t *sess = (remex_pw_session_t *)arg;

#ifdef REMEX_HAS_PIPEWIRE

    /* Obtain the portal-scoped PipeWire fd if a session handle was provided.
     * Without this fd, pw_context_connect will succeed but the ACL-protected
     * screencast node created by the portal will not be visible on KDE/GNOME. */
    int pw_fd = -1;
    if (sess->portal_session_handle)
        pw_fd = open_pipewire_remote_sdbus(sess->portal_session_handle);

    pw_init(NULL, NULL);

    struct pw_main_loop *loop = pw_main_loop_new(NULL);
    if (!loop)
    {
        fprintf(stderr, "[remex] pw_main_loop_new failed\n");
        if (pw_fd >= 0)
            close(pw_fd);
        return NULL;
    }

    /* Store loop pointer under the mutex so destroy can call pw_main_loop_quit
     * thread-safely, then check if destroy already ran. */
    pthread_mutex_lock(&sess->frame_mutex);
    sess->loop = loop;
    int already_done = sess->shutting_down;
    pthread_mutex_unlock(&sess->frame_mutex);

    if (already_done)
    {
        pw_main_loop_destroy(loop);
        if (pw_fd >= 0)
            close(pw_fd);
        return NULL;
    }

    struct pw_context *ctx = pw_context_new(
        pw_main_loop_get_loop(loop), NULL, 0);
    if (!ctx)
    {
        fprintf(stderr, "[remex] pw_context_new failed\n");
        pw_main_loop_destroy(loop);
        if (pw_fd >= 0)
            close(pw_fd);
        return NULL;
    }

    struct pw_core *core;
    if (pw_fd >= 0)
    {
        core = pw_context_connect_fd(ctx, pw_fd, NULL, 0);
        /* pw_context_connect_fd takes ownership on success. On failure, ownership
         * is not guaranteed, so close defensively to prevent any leak path. */
        if (!core)
            close(pw_fd);
        pw_fd = -1;
    }
    else
    {
        core = pw_context_connect(ctx, NULL, 0);
    }

    if (!core)
    {
        fprintf(stderr, "[remex] pw_context_connect failed\n");
        pw_context_destroy(ctx);
        pw_main_loop_destroy(loop);
        return NULL;
    }

    struct pw_stream *stream = pw_stream_new(
        core, "remex-capture",
        pw_properties_new(
            PW_KEY_MEDIA_TYPE,     "Video",
            PW_KEY_MEDIA_CATEGORY, "Capture",
            PW_KEY_MEDIA_ROLE,     "Screen",
            NULL));

    if (!stream)
    {
        fprintf(stderr, "[remex] pw_stream_new failed\n");
        pw_core_disconnect(core);
        pw_context_destroy(ctx);
        pw_main_loop_destroy(loop);
        return NULL;
    }

    sess->stream = stream;
    pw_stream_add_listener(stream, &sess->stream_listener, &stream_events, sess);

    /* Build format enum pod: prefer BGRA, accept BGRx/RGBA/RGBx. */
    uint8_t fmt_buf[1024];
    struct spa_pod_builder fb = SPA_POD_BUILDER_INIT(fmt_buf, sizeof(fmt_buf));
    const struct spa_pod *format_param = spa_pod_builder_add_object(
        &fb,
        SPA_TYPE_OBJECT_Format, SPA_PARAM_EnumFormat,
        SPA_FORMAT_mediaType,    SPA_POD_Id(SPA_MEDIA_TYPE_video),
        SPA_FORMAT_mediaSubtype, SPA_POD_Id(SPA_MEDIA_SUBTYPE_raw),
        SPA_FORMAT_VIDEO_format, SPA_POD_CHOICE_ENUM_Id(5,
            SPA_VIDEO_FORMAT_BGRA,   /* default — most common on KDE Plasma 6 */
            SPA_VIDEO_FORMAT_BGRA,
            SPA_VIDEO_FORMAT_BGRx,
            SPA_VIDEO_FORMAT_RGBA,
            SPA_VIDEO_FORMAT_RGBx));

    /* node_id == 0 means the portal found no streams; use PW_ID_ANY so PipeWire
     * auto-selects the first suitable consumer node. */
    uint32_t target_id = (sess->node_id == 0) ? PW_ID_ANY : sess->node_id;

    int rc = pw_stream_connect(
        stream,
        PW_DIRECTION_INPUT, target_id,
        PW_STREAM_FLAG_AUTOCONNECT | PW_STREAM_FLAG_MAP_BUFFERS,
        &format_param, 1);

    if (rc < 0)
    {
        fprintf(stderr, "[remex] pw_stream_connect failed: %s\n", spa_strerror(rc));
        pw_stream_destroy(stream);
        sess->stream = NULL;
        pw_core_disconnect(core);
        pw_context_destroy(ctx);
        pw_main_loop_destroy(loop);
        return NULL;
    }

    /* Block here until remex_pw_session_destroy calls pw_main_loop_quit. */
    pw_main_loop_run(loop);

    /* ── Teardown ── */
    pw_stream_destroy(stream);
    sess->stream = NULL;

    pw_core_disconnect(core);
    pw_context_destroy(ctx);

    pthread_mutex_lock(&sess->frame_mutex);
    sess->loop = NULL;
    pthread_mutex_unlock(&sess->frame_mutex);

    pw_main_loop_destroy(loop);
    /* Never call pw_deinit() — it is process-global and would break other sessions. */

#else /* !REMEX_HAS_PIPEWIRE */

    /* Built without PipeWire headers: park the thread until destroy. */
    pthread_mutex_lock(&sess->frame_mutex);
    while (!sess->shutting_down)
        pthread_cond_wait(&sess->frame_cond, &sess->frame_mutex);
    pthread_mutex_unlock(&sess->frame_mutex);

#endif /* REMEX_HAS_PIPEWIRE */

    return NULL;
}

/* ── Capability probe ─────────────────────────────────────────────────────── */

int remex_probe_capabilities(char *out_buf, size_t buf_size)
{
    if (!out_buf || buf_size == 0)
        return REMEX_ERR_BUFFER_TOO_SMALL;

#ifdef REMEX_HAS_PIPEWIRE
    int pw = 1;
#else
    /* Runtime probe when not hard-linked. */
    void *pw_handle = NULL;
    const char *pw_libs[] = {"libpipewire-0.3.so.0", "libpipewire-0.3.so", NULL};
    for (int i = 0; pw_libs[i]; i++)
    {
        pw_handle = dlopen(pw_libs[i], RTLD_NOW | RTLD_LOCAL);
        if (pw_handle)
        {
            dlclose(pw_handle);
            break;
        }
    }
    int pw = (pw_handle != NULL) ? 1 : 0;
#endif

    void *ei = NULL;
    const char *ei_libs[] = {"libei-1.0.so.0", "libei.so", NULL};
    for (int i = 0; ei_libs[i]; i++)
    {
        ei = dlopen(ei_libs[i], RTLD_NOW | RTLD_LOCAL);
        if (ei)
        {
            dlclose(ei);
            break;
        }
    }

    void *evdev = NULL;
    const char *evdev_libs[] = {"libevdev.so.2", "libevdev.so", NULL};
    for (int i = 0; evdev_libs[i]; i++)
    {
        evdev = dlopen(evdev_libs[i], RTLD_NOW | RTLD_LOCAL);
        if (evdev)
        {
            dlclose(evdev);
            break;
        }
    }

    int uinput = (access("/dev/uinput", W_OK) == 0) ? 1 : 0;

    int written = snprintf(out_buf, buf_size,
                           "{\"pipewire\":%s,\"libei\":%s,\"libevdev\":%s,\"uinput\":%s}",
                           pw ? "true" : "false",
                           ei ? "true" : "false",
                           evdev ? "true" : "false",
                           uinput ? "true" : "false");

    return (written > 0 && (size_t)written < buf_size)
               ? written
               : REMEX_ERR_BUFFER_TOO_SMALL;
}
