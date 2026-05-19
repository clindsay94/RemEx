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

case "$ACTION" in
install)
    echo "Installing RemEx Client to $INSTALL_DIR ..."
    mkdir -p "$INSTALL_DIR" "$BIN_DIR" "$APP_DIR" "$ICON_DIR"

    # Copy all files except the install helper and desktop template
    find "$SCRIPT_DIR" -maxdepth 1 \
        ! -name 'install.sh' \
        ! -name 'remex-client.desktop' \
        ! -name '.' \
        -exec cp -r {} "$INSTALL_DIR/" \;

    chmod +x "$INSTALL_DIR/Remex.Client.Desktop"

    if ldd "$INSTALL_DIR/libremex_linux_bridge.so" 2>/dev/null | grep -q "not found"; then
        echo "WARNING: libremex_linux_bridge.so is missing a runtime dependency."
        echo "         Run: ldd \"$INSTALL_DIR/libremex_linux_bridge.so\" to diagnose."
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
