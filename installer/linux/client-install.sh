#!/usr/bin/env bash
# RemEx Client installer
# Usage: ./install.sh [install|uninstall]

set -euo pipefail

ACTION="${1:-install}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

INSTALL_DIR="$HOME/.local/share/remex-client"
BIN_DIR="$HOME/.local/bin"
APP_DIR="$HOME/.local/share/applications"
ICON_DIR="$HOME/.local/share/icons/hicolor/256x256/apps"
BRIDGE_PATH="$INSTALL_DIR/runtimes/linux-x64/native/libremex_linux_bridge.so"

# ── Preflight (shared with the host installer) ─────────────────────────
# The client itself doesn't open portal sessions, but most users install client
# and host on the same machine. Resetting the portal frontend here means the
# first host launch finds a healthy portal stack — avoids the in-process
# recovery dance on a fresh install.
preflight() {
    local desktop="${XDG_CURRENT_DESKTOP:-}"
    local backend_pkg=""

    case "${desktop,,}" in
        *kde*|*plasma*)              backend_pkg="xdg-desktop-portal-kde" ;;
        *gnome*)                     backend_pkg="xdg-desktop-portal-gnome" ;;
        *sway*|*hyprland*|*wlroots*) backend_pkg="xdg-desktop-portal-wlr" ;;
    esac

    if command -v pacman >/dev/null 2>&1; then
        local required=(xdg-desktop-portal pipewire)
        [ -n "$backend_pkg" ] && required+=("$backend_pkg")

        local missing=()
        for p in "${required[@]}"; do
            pacman -Qi "$p" >/dev/null 2>&1 || missing+=("$p")
        done

        if [ ${#missing[@]} -gt 0 ]; then
            echo ""
            echo "Note: these packages are recommended for remote-desktop functionality on"
            echo "      this machine (only required if you also run the host here):"
            printf '  - %s\n' "${missing[@]}"
            echo ""
            echo "Install them with:"
            echo "  sudo pacman -S --needed ${missing[*]}"
            echo ""
        fi
    fi

    systemctl --user import-environment \
        XDG_CURRENT_DESKTOP XDG_SESSION_TYPE WAYLAND_DISPLAY \
        DISPLAY DBUS_SESSION_BUS_ADDRESS XDG_DATA_DIRS XDG_RUNTIME_DIR \
        2>/dev/null || true

    systemctl --user restart xdg-desktop-portal.service 2>/dev/null || true
}

case "$ACTION" in
install)
    echo "Installing RemEx Client to $INSTALL_DIR ..."
    mkdir -p "$INSTALL_DIR" "$BIN_DIR" "$APP_DIR" "$ICON_DIR"
    rm -f "$INSTALL_DIR/libremex_linux_bridge.so"

    # Copy all files except the install helper and desktop template
    find "$SCRIPT_DIR" -maxdepth 1 \
        ! -name 'install.sh' \
        ! -name 'remex-client.desktop' \
        ! -name '.' \
        -exec cp -r {} "$INSTALL_DIR/" \;

    rm -f "$INSTALL_DIR/libremex_linux_bridge.so"

    chmod +x "$INSTALL_DIR/Remex.Client.Desktop"

    if [[ ! -f "$BRIDGE_PATH" ]]; then
        echo "WARNING: libremex_linux_bridge.so is missing from the .NET runtime probing path."
        echo "         Expected: $BRIDGE_PATH"
        echo "         PipeWire capture will not be available."
    elif ldd "$BRIDGE_PATH" 2>/dev/null | grep -q "not found"; then
        echo "WARNING: libremex_linux_bridge.so is missing a runtime dependency."
        echo "         Run: ldd \"$BRIDGE_PATH\" to diagnose."
        echo "         PipeWire capture will not be available."
    fi

    # Launcher symlink
    ln -sf "$INSTALL_DIR/Remex.Client.Desktop" "$BIN_DIR/remex-client"

    # Icon
    if [[ -f "$SCRIPT_DIR/remex.png" ]]; then
        cp "$SCRIPT_DIR/remex.png" "$ICON_DIR/remex.png"
        gtk-update-icon-cache -f -t "$HOME/.local/share/icons/hicolor" 2>/dev/null || true
    fi

    # .desktop file with real install path substituted
    sed "s|REMEX_INSTALL_DIR|$INSTALL_DIR|g" \
        "$SCRIPT_DIR/remex-client.desktop" > "$APP_DIR/remex-client.desktop"
    update-desktop-database "$APP_DIR" 2>/dev/null || true
    kbuildsycoca6 2>/dev/null || true  # KDE Plasma cache refresh

    preflight

    echo ""
    echo "RemEx Client installed."
    echo "  Launch:     remex-client"
    echo "  Uninstall:  $INSTALL_DIR/install.sh uninstall"
    ;;

uninstall)
    echo "Removing RemEx Client ..."
    rm -f  "$BIN_DIR/remex-client"
    rm -f  "$APP_DIR/remex-client.desktop"
    rm -f  "$ICON_DIR/remex.png"
    rm -rf "$INSTALL_DIR"
    update-desktop-database "$APP_DIR" 2>/dev/null || true
    gtk-update-icon-cache -f -t "$HOME/.local/share/icons/hicolor" 2>/dev/null || true
    echo "Done."
    ;;

*)
    echo "Usage: $0 [install|uninstall]"
    exit 1
    ;;
esac
