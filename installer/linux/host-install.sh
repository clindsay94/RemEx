#!/usr/bin/env bash
# RemEx Host — command-agent installer (Linux, SYSTEM service)
#
# Installs the consolidated RemEx.Host binary and registers it as a SYSTEM systemd service running in
# headless command-agent mode (--agent), so it answers remote power commands (shutdown / restart /
# sleep) even while the machine is logged out. It runs as root (system service) so those commands
# succeed with no active login session.
#
# Desktop streaming is NOT provided by this agent — that is the RemEx desktop app, which runs while
# you are logged in and automatically takes over the canonical port from the agent (and hands it back
# on exit). Portal/PipeWire prerequisites therefore belong to the desktop-app install, not here.
#
# Requires root for the system install paths and service registration (uses sudo when not already root).
# Usage: ./install.sh [install|uninstall|status|start|stop|doctor]

set -euo pipefail

ACTION="${1:-install}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

INSTALL_DIR="/opt/remex-host"
SERVICE_DIR="/etc/systemd/system"
SERVICE_NAME="remex-host"

SUDO=""
if [[ ${EUID:-$(id -u)} -ne 0 ]]; then
    SUDO="sudo"
fi

case "$ACTION" in
install)
    echo "Installing RemEx command agent to $INSTALL_DIR (system service) ..."

    # Stop a running service first so `cp` does not hit ETXTBSY on the self-contained executable.
    $SUDO systemctl stop "$SERVICE_NAME" 2>/dev/null || true

    $SUDO mkdir -p "$INSTALL_DIR"
    $SUDO rm -f "$INSTALL_DIR/libremex_linux_bridge.so"

    # Copy everything except the installer and the service template.
    $SUDO find "$SCRIPT_DIR" -maxdepth 1 \
        ! -name 'install.sh' \
        ! -name 'remex-host.service' \
        ! -name '.' \
        -exec cp -r {} "$INSTALL_DIR/" \;

    $SUDO chmod +x "$INSTALL_DIR/RemEx.Host"

    # Install the system unit with the real install path substituted.
    sed "s|REMEX_INSTALL_DIR|$INSTALL_DIR|g" "$SCRIPT_DIR/remex-host.service" \
        | $SUDO tee "$SERVICE_DIR/$SERVICE_NAME.service" >/dev/null

    $SUDO systemctl daemon-reload
    $SUDO systemctl enable --now "$SERVICE_NAME"

    echo ""
    echo "RemEx command agent installed and started (runs at boot, before login)."
    echo "  Status:    systemctl status $SERVICE_NAME"
    echo "  Logs:      journalctl -u $SERVICE_NAME -f"
    echo "  Uninstall: $INSTALL_DIR/install.sh uninstall"
    echo ""
    echo "To reach the agent from outside your home network, install Tailscale:"
    echo "  curl -fsSL https://tailscale.com/install.sh | sh && sudo tailscale up"
    ;;

uninstall)
    echo "Stopping and removing the RemEx command agent ..."
    $SUDO systemctl disable --now "$SERVICE_NAME" 2>/dev/null || true
    $SUDO rm -f "$SERVICE_DIR/$SERVICE_NAME.service"
    $SUDO systemctl daemon-reload
    $SUDO rm -rf "$INSTALL_DIR"
    echo "Done."
    ;;

status)
    $SUDO systemctl status "$SERVICE_NAME"
    ;;

start)
    $SUDO systemctl start "$SERVICE_NAME"
    echo "Started."
    ;;

stop)
    $SUDO systemctl stop "$SERVICE_NAME"
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
