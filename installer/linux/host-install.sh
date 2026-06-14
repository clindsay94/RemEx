#!/usr/bin/env bash
# RemEx Host installer
# Usage: ./install.sh [install|uninstall|status|start|stop|doctor]

set -euo pipefail

ACTION="${1:-install}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

INSTALL_DIR="$HOME/.local/share/remex-host"
SERVICE_DIR="$HOME/.config/systemd/user"
SERVICE_NAME="remex-host"
BRIDGE_PATH="$INSTALL_DIR/runtimes/linux-x64/native/libremex_linux_bridge.so"

# ── Preflight: portal & PipeWire dependency check ─────────────────────────
# Detects the active desktop, lists missing packages (Arch family only), and
# resets the xdg-desktop-portal frontend with the current session env so the
# RemoteDesktop interface is exposed when the host starts.
#
# This MUST run from the user's interactive shell where XDG_CURRENT_DESKTOP,
# WAYLAND_DISPLAY, etc. are set. Running it from inside a stripped-env systemd
# unit would be a no-op.
preflight() {
    local desktop="${XDG_CURRENT_DESKTOP:-}"
    local backend_pkg=""

    case "${desktop,,}" in
        *kde*|*plasma*)              backend_pkg="xdg-desktop-portal-kde" ;;
        *gnome*)                     backend_pkg="xdg-desktop-portal-gnome" ;;
        *sway*|*hyprland*|*wlroots*) backend_pkg="xdg-desktop-portal-wlr" ;;
    esac

    if command -v pacman >/dev/null 2>&1; then
        local required=(xdg-desktop-portal pipewire wireplumber libei libevdev ffmpeg)
        [ -n "$backend_pkg" ] && required+=("$backend_pkg")

        local missing=()
        for p in "${required[@]}"; do
            pacman -Qi "$p" >/dev/null 2>&1 || missing+=("$p")
        done

        if [ ${#missing[@]} -gt 0 ]; then
            echo ""
            echo "WARNING: required packages are missing for full remote-desktop support:"
            printf '  - %s\n' "${missing[@]}"
            echo ""
            echo "Install them with:"
            echo "  sudo pacman -S --needed ${missing[*]}"
            echo ""
        fi
    fi

    # Push the live session env into systemd --user. If the user-systemd manager
    # was started before Plasma/GNOME did this (a known SDDM/GDM race), the
    # portal frontend has a bare env and won't route RemoteDesktop to the KDE
    # or GNOME backend. The next restart picks up the freshly-imported env.
    systemctl --user import-environment \
        XDG_CURRENT_DESKTOP XDG_SESSION_TYPE WAYLAND_DISPLAY \
        DISPLAY DBUS_SESSION_BUS_ADDRESS XDG_DATA_DIRS XDG_RUNTIME_DIR \
        2>/dev/null || true

    systemctl --user restart xdg-desktop-portal.service 2>/dev/null || true
}

case "$ACTION" in
install)
    echo "Installing RemEx Host to $INSTALL_DIR ..."

    # Upgrades can overwrite the currently-running self-contained executable.
    # Stop the user service first so `cp` does not hit ETXTBSY on RemEx.Host.
    systemctl --user stop "$SERVICE_NAME" 2>/dev/null || true
    for _ in {1..50}; do
        if ! systemctl --user --quiet is-active "$SERVICE_NAME" 2>/dev/null; then
            break
        fi
        sleep 0.1
    done

    mkdir -p "$INSTALL_DIR" "$SERVICE_DIR"
    rm -f "$INSTALL_DIR/libremex_linux_bridge.so"

    # Copy all files except install helper and service template
    find "$SCRIPT_DIR" -maxdepth 1 \
        ! -name 'install.sh' \
        ! -name 'remex-host.service' \
        ! -name '.' \
        -exec cp -r {} "$INSTALL_DIR/" \;

    rm -f "$INSTALL_DIR/libremex_linux_bridge.so"

    chmod +x "$INSTALL_DIR/RemEx.Host"

    # systemd user service with real install path substituted
    sed "s|REMEX_INSTALL_DIR|$INSTALL_DIR|g" \
        "$SCRIPT_DIR/remex-host.service" > "$SERVICE_DIR/$SERVICE_NAME.service"

    systemctl --user daemon-reload

    # Preflight portal/PipeWire setup BEFORE starting the host so it doesn't
    # have to attempt in-process portal recovery on first launch.
    preflight

    systemctl --user enable --now "$SERVICE_NAME"

    if [[ ! -f "$BRIDGE_PATH" ]]; then
        echo "WARNING: libremex_linux_bridge.so is missing from the .NET runtime probing path."
        echo "         Expected: $BRIDGE_PATH"
        echo "         PipeWire capture will not be available until the host is rebuilt/reinstalled."
    elif ldd "$BRIDGE_PATH" 2>/dev/null | grep -q "not found"; then
        echo "WARNING: libremex_linux_bridge.so is missing a runtime dependency."
        echo "         Run: ldd \"$BRIDGE_PATH\" to diagnose."
        echo "         PipeWire capture will not be available; the legacy path will be used."
    fi

    # Post-install verification — runs the same Linux prerequisite report the
    # host uses internally. Reports nonzero exit if portal/PipeWire isn't ready
    # but does not abort the install (the user may want to fix things later).
    echo ""
    echo "Running post-install doctor..."
    if "$INSTALL_DIR/RemEx.Host" --doctor; then
        echo "Doctor reports the system is ready."
    else
        echo "WARNING: doctor reported issues above. Run \"$INSTALL_DIR/RemEx.Host --doctor\""
        echo "         again after addressing them."
    fi

    # Check if we should enable user lingering for boot persistence
    if [ -t 0 ]; then
        echo ""
        read -rp "Would you like to enable boot persistence (so RemEx Host runs at boot before you log in)? [y/N] " LINGER_ANS
        if [[ "$LINGER_ANS" =~ ^[Yy]$ ]]; then
            echo "Enabling systemd user lingering for $USER..."
            loginctl enable-linger "$USER" || echo "WARNING: could not enable lingering."
        else
            echo "Boot persistence skipped."
        fi
    else
        echo ""
        echo "Note: To enable boot persistence (run host at boot before login), run:"
        echo "  loginctl enable-linger $USER"
    fi

    # Check if we should configure Tailscale for secure remote access
    if [ -t 0 ]; then
        echo ""
        read -rp "Would you like to configure Tailscale for secure remote access from outside your home network? [y/N] " TS_ANS
        if [[ "$TS_ANS" =~ ^[Yy]$ ]]; then
            if command -v tailscale >/dev/null 2>&1; then
                echo "Tailscale is already installed."
                read -rp "Would you like to start and authenticate Tailscale now? [y/N] " TS_UP_ANS
                if [[ "$TS_UP_ANS" =~ ^[Yy]$ ]]; then
                    sudo tailscale up
                fi
            else
                echo "Tailscale is not installed. Installing via the official one-liner script..."
                if curl -fsSL https://tailscale.com/install.sh | sh; then
                    echo "Tailscale successfully installed!"
                    read -rp "Would you like to start and authenticate Tailscale now? [y/N] " TS_UP_ANS
                    if [[ "$TS_UP_ANS" =~ ^[Yy]$ ]]; then
                        sudo tailscale up
                    fi
                else
                    echo "WARNING: Could not install Tailscale automatically. You can install it manually from https://tailscale.com"
                fi
            fi
        else
            echo "Remote access setup skipped."
        fi
    else
        echo ""
        echo "Note: To access your host securely from outside your home network, install Tailscale:"
        echo "  curl -fsSL https://tailscale.com/install.sh | sh"
    fi

    echo ""
    echo "RemEx Host installed and started."
    echo "  Status:     systemctl --user status $SERVICE_NAME"
    echo "  Logs:       journalctl --user -u $SERVICE_NAME -f"
    echo "  Doctor:     $INSTALL_DIR/RemEx.Host --doctor"
    echo "  Uninstall:  $INSTALL_DIR/install.sh uninstall"
    echo ""
    if loginctl show-user "$USER" --property=Linger 2>/dev/null | grep -q "Linger=yes"; then
        echo "The host is configured to run at boot (lingering is enabled)."
    else
        echo "The host will start automatically when you log in."
        echo "To make it start at boot before login, run: loginctl enable-linger $USER"
    fi
    ;;

uninstall)
    echo "Stopping and removing RemEx Host ..."
    systemctl --user disable --now "$SERVICE_NAME" 2>/dev/null || true
    rm -f  "$SERVICE_DIR/$SERVICE_NAME.service"
    systemctl --user daemon-reload
    rm -rf "$INSTALL_DIR"
    echo "Done."
    ;;

status)
    systemctl --user status "$SERVICE_NAME"
    ;;

start)
    systemctl --user start "$SERVICE_NAME"
    echo "Started."
    ;;

stop)
    systemctl --user stop "$SERVICE_NAME"
    echo "Stopped."
    ;;

doctor)
    "$INSTALL_DIR/RemEx.Host" --doctor
    ;;

*)
    echo "Usage: $0 [install|uninstall|status|start|stop|doctor]"
    exit 1
    ;;
esac
