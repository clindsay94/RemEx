/*
 * uinput_tablet.c
 *
 * Virtual evdev tablet device for stylus/pen input forwarding on Linux.
 * Creates a uinput device with full ABS_X/Y/PRESSURE/DISTANCE/TILT axes
 * and BTN_TOOL_PEN/RUBBER/TOUCH/STYLUS/STYLUS2 buttons.
 *
 * This is the pen input path for WaylandNative tier when /dev/uinput is
 * writable. For basic mouse/keyboard on Wayland the EIS path is preferred.
 *
 * Axis ranges mirror typical Wacom tablet values and are remapped by the
 * managed layer (LinuxUinputTabletService) from the Android S-Pen coordinate
 * space before calling remex_uinput_tablet_send_stylus_event.
 */

#include "../include/remex_linux_bridge.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <errno.h>
#include <fcntl.h>
#include <unistd.h>
#include <sys/ioctl.h>

/* Include uinput kernel header */
#include <linux/uinput.h>
#include <linux/input.h>

/* ── Axis range constants ─────────────────────────────────────────────── */
#define REMEX_ABS_MAX_XY       65535
#define REMEX_ABS_MAX_PRESSURE 65535
#define REMEX_ABS_MAX_DISTANCE 65535
#define REMEX_ABS_TILT_MIN     (-64)
#define REMEX_ABS_TILT_MAX      63

/* ── Tablet handle ────────────────────────────────────────────────────── */
typedef struct remex_uinput_tablet {
    int  fd;
    int  has_pressure;
    int  has_tilt;
    int  has_distance;
} remex_uinput_tablet_t;

/* ── Helpers ─────────────────────────────────────────────────────────── */
static int set_abs_axis(int fd, uint16_t axis, int32_t minimum, int32_t maximum)
{
    struct uinput_abs_setup abs = {
        .code = axis,
        .absinfo = {
            .value      = 0,
            .minimum    = minimum,
            .maximum    = maximum,
            .fuzz       = 0,
            .flat       = 0,
            .resolution = 0,
        },
    };
    return ioctl(fd, UI_ABS_SETUP, &abs);
}

static int emit(int fd, uint16_t type, uint16_t code, int32_t value)
{
    struct input_event ev;
    memset(&ev, 0, sizeof(ev));
    ev.type  = type;
    ev.code  = code;
    ev.value = value;
    ssize_t n = write(fd, &ev, sizeof(ev));
    return (n == (ssize_t)sizeof(ev)) ? 0 : -1;
}

/* ── Public API ─────────────────────────────────────────────────────────── */

int remex_uinput_tablet_create(
    const char *device_name,
    int         supports_pressure,
    int         supports_tilt,
    int         supports_distance,
    void      **out_handle)
{
    if (!device_name || !out_handle) return REMEX_ERR_GENERIC;

    int fd = open("/dev/uinput", O_WRONLY | O_NONBLOCK);
    if (fd < 0) {
        if (errno == EACCES || errno == EPERM) return REMEX_ERR_PERMISSION_DENIED;
        return REMEX_ERR_UINPUT_UNAVAILABLE;
    }

    /* Enable event types */
    if (ioctl(fd, UI_SET_EVBIT, EV_SYN) < 0 ||
        ioctl(fd, UI_SET_EVBIT, EV_KEY) < 0 ||
        ioctl(fd, UI_SET_EVBIT, EV_ABS) < 0)
    {
        close(fd);
        return REMEX_ERR_GENERIC;
    }

    /* Pen tool buttons */
    ioctl(fd, UI_SET_KEYBIT, BTN_TOOL_PEN);
    ioctl(fd, UI_SET_KEYBIT, BTN_TOOL_RUBBER);
    ioctl(fd, UI_SET_KEYBIT, BTN_TOUCH);
    ioctl(fd, UI_SET_KEYBIT, BTN_STYLUS);
    ioctl(fd, UI_SET_KEYBIT, BTN_STYLUS2);

    /* ABS axes — position is always required */
    ioctl(fd, UI_SET_ABSBIT, ABS_X);
    ioctl(fd, UI_SET_ABSBIT, ABS_Y);
    set_abs_axis(fd, ABS_X, 0, REMEX_ABS_MAX_XY);
    set_abs_axis(fd, ABS_Y, 0, REMEX_ABS_MAX_XY);

    if (supports_pressure) {
        ioctl(fd, UI_SET_ABSBIT, ABS_PRESSURE);
        set_abs_axis(fd, ABS_PRESSURE, 0, REMEX_ABS_MAX_PRESSURE);
    }
    if (supports_distance) {
        ioctl(fd, UI_SET_ABSBIT, ABS_DISTANCE);
        set_abs_axis(fd, ABS_DISTANCE, 0, REMEX_ABS_MAX_DISTANCE);
    }
    if (supports_tilt) {
        ioctl(fd, UI_SET_ABSBIT, ABS_TILT_X);
        ioctl(fd, UI_SET_ABSBIT, ABS_TILT_Y);
        set_abs_axis(fd, ABS_TILT_X, REMEX_ABS_TILT_MIN, REMEX_ABS_TILT_MAX);
        set_abs_axis(fd, ABS_TILT_Y, REMEX_ABS_TILT_MIN, REMEX_ABS_TILT_MAX);
    }

    /* Device info */
    struct uinput_setup setup;
    memset(&setup, 0, sizeof(setup));
    setup.id.bustype = BUS_VIRTUAL;
    setup.id.vendor  = 0x056A; /* Wacom VID — widely recognized as tablet */
    setup.id.product = 0x0001;
    setup.id.version = 1;
    strncpy(setup.name, device_name, UINPUT_MAX_NAME_SIZE - 1);

    if (ioctl(fd, UI_DEV_SETUP, &setup) < 0 ||
        ioctl(fd, UI_DEV_CREATE) < 0)
    {
        close(fd);
        return REMEX_ERR_GENERIC;
    }

    remex_uinput_tablet_t *tab = calloc(1, sizeof(remex_uinput_tablet_t));
    if (!tab) { close(fd); return REMEX_ERR_GENERIC; }

    tab->fd           = fd;
    tab->has_pressure = supports_pressure;
    tab->has_tilt     = supports_tilt;
    tab->has_distance = supports_distance;

    *out_handle = tab;
    return REMEX_OK;
}

int remex_uinput_tablet_send_stylus_event(
    void    *handle,
    int32_t  abs_x,
    int32_t  abs_y,
    int32_t  pressure,
    int32_t  tilt_x,
    int32_t  tilt_y,
    int32_t  distance,
    uint32_t button_mask,
    int      tool_pen,
    int      tool_rubber)
{
    if (!handle) return REMEX_ERR_NOT_INITIALIZED;
    remex_uinput_tablet_t *tab = (remex_uinput_tablet_t *)handle;

    /* Tool activation */
    emit(tab->fd, EV_KEY, BTN_TOOL_PEN,    tool_pen    ? 1 : 0);
    emit(tab->fd, EV_KEY, BTN_TOOL_RUBBER, tool_rubber ? 1 : 0);

    /* Position */
    emit(tab->fd, EV_ABS, ABS_X, abs_x);
    emit(tab->fd, EV_ABS, ABS_Y, abs_y);

    /* Optional axes */
    if (tab->has_pressure)
        emit(tab->fd, EV_ABS, ABS_PRESSURE, pressure);
    if (tab->has_distance)
        emit(tab->fd, EV_ABS, ABS_DISTANCE, distance);
    if (tab->has_tilt) {
        emit(tab->fd, EV_ABS, ABS_TILT_X, tilt_x);
        emit(tab->fd, EV_ABS, ABS_TILT_Y, tilt_y);
    }

    /* Stylus buttons */
    emit(tab->fd, EV_KEY, BTN_TOUCH,   (button_mask & 0x01) ? 1 : 0);
    emit(tab->fd, EV_KEY, BTN_STYLUS,  (button_mask & 0x02) ? 1 : 0);
    emit(tab->fd, EV_KEY, BTN_STYLUS2, (button_mask & 0x04) ? 1 : 0);

    /* Sync */
    return emit(tab->fd, EV_SYN, SYN_REPORT, 0);
}

int remex_uinput_tablet_reset(void *handle)
{
    if (!handle) return REMEX_ERR_NOT_INITIALIZED;
    remex_uinput_tablet_t *tab = (remex_uinput_tablet_t *)handle;

    /* Release all buttons and tools to prevent stuck state */
    emit(tab->fd, EV_KEY, BTN_TOOL_PEN,    0);
    emit(tab->fd, EV_KEY, BTN_TOOL_RUBBER, 0);
    emit(tab->fd, EV_KEY, BTN_TOUCH,   0);
    emit(tab->fd, EV_KEY, BTN_STYLUS,  0);
    emit(tab->fd, EV_KEY, BTN_STYLUS2, 0);
    if (tab->has_pressure)
        emit(tab->fd, EV_ABS, ABS_PRESSURE, 0);
    if (tab->has_distance)
        emit(tab->fd, EV_ABS, ABS_DISTANCE, 0);
    return emit(tab->fd, EV_SYN, SYN_REPORT, 0);
}

void remex_uinput_tablet_destroy(void *handle)
{
    if (!handle) return;
    remex_uinput_tablet_t *tab = (remex_uinput_tablet_t *)handle;
    remex_uinput_tablet_reset(tab);
    ioctl(tab->fd, UI_DEV_DESTROY);
    close(tab->fd);
    free(tab);
}

/* ── uinput keyboard/pointer fallback ─────────────────────────────────── */

typedef struct remex_uinput_kbptr {
    int fd;
} remex_uinput_kbptr_t;

int remex_uinput_kbptr_create(const char *device_name, void **out_handle)
{
    if (!device_name || !out_handle) return REMEX_ERR_GENERIC;

    int fd = open("/dev/uinput", O_WRONLY | O_NONBLOCK);
    if (fd < 0) {
        if (errno == EACCES || errno == EPERM) return REMEX_ERR_PERMISSION_DENIED;
        return REMEX_ERR_UINPUT_UNAVAILABLE;
    }

    ioctl(fd, UI_SET_EVBIT, EV_SYN);
    ioctl(fd, UI_SET_EVBIT, EV_KEY);
    ioctl(fd, UI_SET_EVBIT, EV_REL);

    /* Enable all keyboard keys */
    for (int k = 0; k < KEY_MAX; k++)
        ioctl(fd, UI_SET_KEYBIT, k);

    /* Relative axes */
    ioctl(fd, UI_SET_RELBIT, REL_X);
    ioctl(fd, UI_SET_RELBIT, REL_Y);
    ioctl(fd, UI_SET_RELBIT, REL_WHEEL);
    ioctl(fd, UI_SET_RELBIT, REL_HWHEEL);

    struct uinput_setup setup;
    memset(&setup, 0, sizeof(setup));
    setup.id.bustype = BUS_VIRTUAL;
    setup.id.vendor  = 0x1234;
    setup.id.product = 0x5678;
    setup.id.version = 1;
    strncpy(setup.name, device_name, UINPUT_MAX_NAME_SIZE - 1);

    if (ioctl(fd, UI_DEV_SETUP, &setup) < 0 ||
        ioctl(fd, UI_DEV_CREATE) < 0)
    {
        close(fd);
        return REMEX_ERR_GENERIC;
    }

    remex_uinput_kbptr_t *dev = calloc(1, sizeof(remex_uinput_kbptr_t));
    if (!dev) { close(fd); return REMEX_ERR_GENERIC; }
    dev->fd = fd;
    *out_handle = dev;
    return REMEX_OK;
}

int remex_uinput_kbptr_send_key(void *handle, uint32_t keycode, int pressed)
{
    if (!handle) return REMEX_ERR_NOT_INITIALIZED;
    remex_uinput_kbptr_t *dev = (remex_uinput_kbptr_t *)handle;
    emit(dev->fd, EV_KEY, (uint16_t)keycode, pressed ? 1 : 0);
    return emit(dev->fd, EV_SYN, SYN_REPORT, 0);
}

int remex_uinput_kbptr_send_rel(void *handle, int32_t dx, int32_t dy)
{
    if (!handle) return REMEX_ERR_NOT_INITIALIZED;
    remex_uinput_kbptr_t *dev = (remex_uinput_kbptr_t *)handle;
    if (dx) emit(dev->fd, EV_REL, REL_X, dx);
    if (dy) emit(dev->fd, EV_REL, REL_Y, dy);
    return emit(dev->fd, EV_SYN, SYN_REPORT, 0);
}

int remex_uinput_kbptr_send_button(void *handle, uint32_t button, int pressed)
{
    if (!handle) return REMEX_ERR_NOT_INITIALIZED;
    remex_uinput_kbptr_t *dev = (remex_uinput_kbptr_t *)handle;
    emit(dev->fd, EV_KEY, (uint16_t)button, pressed ? 1 : 0);
    return emit(dev->fd, EV_SYN, SYN_REPORT, 0);
}

int remex_uinput_kbptr_reset(void *handle)
{
    if (!handle) return REMEX_ERR_NOT_INITIALIZED;
    remex_uinput_kbptr_t *dev = (remex_uinput_kbptr_t *)handle;
    /* Release all modifier keys */
    static const uint16_t modifiers[] = {
        KEY_LEFTSHIFT, KEY_RIGHTSHIFT, KEY_LEFTCTRL, KEY_RIGHTCTRL,
        KEY_LEFTALT, KEY_RIGHTALT, KEY_LEFTMETA, KEY_RIGHTMETA,
    };
    for (size_t i = 0; i < sizeof(modifiers) / sizeof(modifiers[0]); i++)
        emit(dev->fd, EV_KEY, modifiers[i], 0);
    /* Release mouse buttons */
    emit(dev->fd, EV_KEY, BTN_LEFT, 0);
    emit(dev->fd, EV_KEY, BTN_RIGHT, 0);
    emit(dev->fd, EV_KEY, BTN_MIDDLE, 0);
    return emit(dev->fd, EV_SYN, SYN_REPORT, 0);
}

void remex_uinput_kbptr_destroy(void *handle)
{
    if (!handle) return;
    remex_uinput_kbptr_t *dev = (remex_uinput_kbptr_t *)handle;
    remex_uinput_kbptr_reset(dev);
    ioctl(dev->fd, UI_DEV_DESTROY);
    close(dev->fd);
    free(dev);
}
