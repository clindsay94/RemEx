#!/usr/bin/env bash
# RemEx Host installer
# Usage: ./install.sh [install|uninstall|status|start|stop]

set -euo pipefail

ACTION="${1:-install}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

INSTALL_DIR="$HOME/.local/share/remex-host"
SERVICE_DIR="$HOME/.config/systemd/user"
SERVICE_NAME="remex-host"

case "$ACTION" in
install)
    echo "Installing RemEx Host to $INSTALL_DIR ..."
    mkdir -p "$INSTALL_DIR" "$SERVICE_DIR"

    # Copy all files except install helper and service template
    find "$SCRIPT_DIR" -maxdepth 1 \
        ! -name 'install.sh' \
        ! -name 'remex-host.service' \
        ! -name '.' \
        -exec cp -r {} "$INSTALL_DIR/" \;

    chmod +x "$INSTALL_DIR/Remex.Host"

    # systemd user service with real install path substituted
    sed "s|REMEX_INSTALL_DIR|$INSTALL_DIR|g" \
        "$SCRIPT_DIR/remex-host.service" > "$SERVICE_DIR/$SERVICE_NAME.service"

    systemctl --user daemon-reload
    systemctl --user enable --now "$SERVICE_NAME"

    if ldd "$INSTALL_DIR/libremex_linux_bridge.so" 2>/dev/null | grep -q "not found"; then
        echo "WARNING: libremex_linux_bridge.so is missing a runtime dependency."
        echo "         Run: ldd \"$INSTALL_DIR/libremex_linux_bridge.so\" to diagnose."
        echo "         PipeWire capture will not be available; the legacy path will be used."
    fi

    echo ""
    echo "RemEx Host installed and started."
    echo "  Status:     systemctl --user status $SERVICE_NAME"
    echo "  Logs:       journalctl --user -u $SERVICE_NAME -f"
    echo "  Uninstall:  $INSTALL_DIR/install.sh uninstall"
    echo ""
    echo "The host will start automatically when you log in."
    echo "Make sure 'Enable lingering' is on if you want it to start at boot"
    echo "before login:  loginctl enable-linger $USER"
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

*)
    echo "Usage: $0 [install|uninstall|status|start|stop]"
    exit 1
    ;;
esac
