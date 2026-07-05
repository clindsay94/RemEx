#!/usr/bin/env bash
# RemEx installer (Linux)
#
# Installs RemEx as a single per-user app — the same model as Windows. There is no
# separate "host" service or "client" app any more: Remex.Agent is the entire PC
# side (desktop UI + connection host in one process). It runs in YOUR login
# session and starts automatically when you sign in, so your phone can connect.
#
# Also cleans up everything the pre-2.0 "client + host" split left behind
# (the remex-host root service, the remex-client install directory, and a
# root-owned pairing certificate under /var/lib/remex).
#
# Usage: ./install.sh [install|uninstall|doctor]

set -euo pipefail

ACTION="${1:-install}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

INSTALL_DIR="$HOME/.local/share/remex-agent"
BIN_DIR="$HOME/.local/bin"
APP_DIR="$HOME/.local/share/applications"
ICON_DIR="$HOME/.local/share/icons/hicolor/256x256/apps"
AUTOSTART_DIR="$HOME/.config/autostart"
AUTOSTART_FILE="$AUTOSTART_DIR/remex-agent.desktop"
BRIDGE_PATH="$INSTALL_DIR/runtimes/linux-x64/native/libremex_linux_bridge.so"

# RemEx keeps its pairing data (certificate + list of paired phones) here.
# Everything security-critical lives in your home directory — nothing runs as root.
STATE_DIR="$HOME/.local/share/Remex"

# Names used by the pre-2.0 "client + host" split, cleaned up on install.
LEGACY_INSTALL_DIR="$HOME/.local/share/remex-client"
LEGACY_SERVICE_NAME="remex-host"
LEGACY_SERVICE_UNIT="/etc/systemd/system/remex-host.service"
LEGACY_OPT_DIR="/opt/remex-host"
LEGACY_CERT_DIR="/var/lib/remex"
LEGACY_CERT="$LEGACY_CERT_DIR/cert.pfx"

# Runs a command as root: directly when already root, via sudo otherwise.
# Returns non-zero (instead of exiting) when neither is possible so callers can
# print manual instructions and continue.
run_root() {
    if [[ ${EUID:-$(id -u)} -eq 0 ]]; then
        "$@"
    elif command -v sudo >/dev/null 2>&1; then
        sudo "$@"
    else
        return 1
    fi
}

# Asks a running RemEx (from the given directory) to exit so its files can be
# replaced. Waits briefly; a stubborn instance is killed so the install never
# fails halfway with "text file busy".
stop_running_instance() {
    local dir="$1"
    pgrep -f "$dir/Remex.Agent" >/dev/null 2>&1 || return 0

    echo "RemEx is currently running — closing it so its files can be updated ..."
    pkill -f "$dir/Remex.Agent" 2>/dev/null || true
    for _ in 1 2 3 4 5 6 7 8 9 10; do
        pgrep -f "$dir/Remex.Agent" >/dev/null 2>&1 || return 0
        sleep 0.5
    done
    pkill -9 -f "$dir/Remex.Agent" 2>/dev/null || true
}

# ── Legacy cleanup: pre-2.0 client/host split ───────────────────────────────

remove_legacy_user_install() {
    if [[ ! -d "$LEGACY_INSTALL_DIR" && ! -e "$BIN_DIR/remex-client" \
          && ! -f "$APP_DIR/remex-client.desktop" \
          && ! -f "$AUTOSTART_DIR/remex-client.desktop" ]]; then
        return 0
    fi

    echo ""
    echo "Cleaning up the previous 'remex-client' install (this version replaces it) ..."
    stop_running_instance "$LEGACY_INSTALL_DIR"
    rm -f  "$BIN_DIR/remex-client"
    rm -f  "$APP_DIR/remex-client.desktop"
    rm -f  "$AUTOSTART_DIR/remex-client.desktop"
    rm -rf "$LEGACY_INSTALL_DIR"
    echo "Old install removed."
}

remove_legacy_root_service() {
    if [[ ! -f "$LEGACY_SERVICE_UNIT" && ! -d "$LEGACY_OPT_DIR" ]]; then
        return 0
    fi

    echo ""
    echo "Found the old '$LEGACY_SERVICE_NAME' background service from a previous version."
    echo "RemEx no longer uses a background service — removing it (you may be asked"
    echo "for your password once)."
    run_root systemctl disable --now "$LEGACY_SERVICE_NAME" 2>/dev/null || true
    if run_root rm -f "$LEGACY_SERVICE_UNIT" && run_root rm -rf "$LEGACY_OPT_DIR"; then
        run_root systemctl daemon-reload 2>/dev/null || true
        echo "Old background service removed."
    else
        echo "Could not remove it automatically. You can remove it yourself later with:"
        echo "  sudo systemctl disable --now $LEGACY_SERVICE_NAME"
        echo "  sudo rm -f $LEGACY_SERVICE_UNIT && sudo systemctl daemon-reload"
        echo "  sudo rm -rf $LEGACY_OPT_DIR"
    fi
}

# Moves the pairing certificate out of the legacy root-owned location into the
# per-user data folder. The certificate FILE is moved unchanged, so phones that
# already paired with this PC keep working. Without this step, a root-owned
# cert.pfx makes RemEx refuse to start its connection host (by design — it will
# never silently replace the certificate your phones trust).
repair_certificate_ownership() {
    [[ -e "$LEGACY_CERT" ]] || return 0

    mkdir -p "$STATE_DIR"

    if [[ -f "$STATE_DIR/cert.pfx" ]]; then
        echo ""
        echo "Found a leftover certificate at $LEGACY_CERT from an old install."
        echo "Your active certificate already lives in $STATE_DIR — removing the leftover."
        if run_root rm -f "$LEGACY_CERT"; then
            run_root rmdir "$LEGACY_CERT_DIR" 2>/dev/null || true
            echo "Leftover removed."
        else
            echo "Could not remove it automatically. It is harmless, but you can remove it with:"
            echo "  sudo rm -f $LEGACY_CERT && sudo rmdir $LEGACY_CERT_DIR"
        fi
        return 0
    fi

    echo ""
    echo "Found your RemEx pairing certificate at $LEGACY_CERT (the old location)."
    echo "Moving it to $STATE_DIR so RemEx can use it from your login session."
    echo "Phones you already paired will keep working — the certificate itself is unchanged."
    if [[ -r "$LEGACY_CERT" ]]; then
        cp "$LEGACY_CERT" "$STATE_DIR/cert.pfx"
        chmod 600 "$STATE_DIR/cert.pfx"
        if run_root rm -f "$LEGACY_CERT"; then
            run_root rmdir "$LEGACY_CERT_DIR" 2>/dev/null || true
        else
            echo "(A harmless copy is left at $LEGACY_CERT — remove it with: sudo rm -f $LEGACY_CERT)"
        fi
        echo "Certificate moved."
    elif run_root mv "$LEGACY_CERT" "$STATE_DIR/cert.pfx" \
         && run_root chown "$USER:" "$STATE_DIR/cert.pfx" \
         && run_root chmod 600 "$STATE_DIR/cert.pfx"; then
        run_root rmdir "$LEGACY_CERT_DIR" 2>/dev/null || true
        echo "Certificate moved."
    else
        echo "Could not move it automatically (this needs your password / sudo)."
        echo "RemEx will show the same fix when it starts. To do it yourself:"
        echo "  sudo mv $LEGACY_CERT $STATE_DIR/cert.pfx"
        echo "  sudo chown $USER: $STATE_DIR/cert.pfx"
        echo "  chmod 600 $STATE_DIR/cert.pfx"
    fi
}

# ── Portal preflight ─────────────────────────────────────────────────────────
# Remote desktop on Linux goes through xdg-desktop-portal + PipeWire. Restarting
# the portal frontend after importing the session environment means the first
# RemEx launch finds a healthy portal stack — avoids the in-process recovery
# dance on a fresh install.
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
            echo "Note: these packages are needed for remote desktop (viewing this PC's"
            echo "      screen from your phone):"
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
    echo "Installing RemEx to $INSTALL_DIR ..."

    # Clean up everything the old client/host split left behind BEFORE laying
    # down the new files, so nothing stale can start at the next login.
    remove_legacy_user_install
    remove_legacy_root_service
    repair_certificate_ownership

    stop_running_instance "$INSTALL_DIR"

    mkdir -p "$INSTALL_DIR" "$BIN_DIR" "$APP_DIR" "$ICON_DIR"
    rm -f "$INSTALL_DIR/libremex_linux_bridge.so"

    # Copy all files except the install helper and desktop template
    find "$SCRIPT_DIR" -maxdepth 1 \
        ! -name 'install.sh' \
        ! -name 'remex-agent.desktop' \
        ! -name '.' \
        -exec cp -r {} "$INSTALL_DIR/" \;

    rm -f "$INSTALL_DIR/libremex_linux_bridge.so"

    chmod +x "$INSTALL_DIR/Remex.Agent"

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
    ln -sf "$INSTALL_DIR/Remex.Agent" "$BIN_DIR/remex-agent"

    # Icon
    if [[ -f "$SCRIPT_DIR/remex.png" ]]; then
        cp "$SCRIPT_DIR/remex.png" "$ICON_DIR/remex.png"
        gtk-update-icon-cache -f -t "$HOME/.local/share/icons/hicolor" 2>/dev/null || true
    fi

    # .desktop file with real install path substituted. Strip any CR first: the template lives on a
    # shared Windows/Linux drive and may carry CRLF, which is invalid in a .desktop file on Linux.
    sed -e "s|\r$||" -e "s|REMEX_INSTALL_DIR|$INSTALL_DIR|g" \
        "$SCRIPT_DIR/remex-agent.desktop" > "$APP_DIR/remex-agent.desktop"
    update-desktop-database "$APP_DIR" 2>/dev/null || true
    kbuildsycoca6 2>/dev/null || true  # KDE Plasma cache refresh

    # Auto-start at login (the Linux equivalent of the Windows logon task). Writes an XDG autostart
    # entry that launches RemEx minimized to the tray when you sign in, so your phone can reach this
    # PC without you opening the app first. It is the SAME file the in-app Settings > "Launch at
    # login" toggle manages, so the two stay in sync; uninstall (below) removes it.
    mkdir -p "$AUTOSTART_DIR"
    # Strip CR first (shared-drive CRLF), then substitute the path and append --minimized to Exec so
    # RemEx starts to the tray. desktop-file-validate must pass on the result.
    sed -e "s|\r$||" \
        -e "s|REMEX_INSTALL_DIR|$INSTALL_DIR|g" \
        -e "s|^Exec=\(.*\)$|Exec=\1 --minimized|" \
        "$SCRIPT_DIR/remex-agent.desktop" > "$AUTOSTART_FILE"
    printf 'X-GNOME-Autostart-enabled=true\n' >> "$AUTOSTART_FILE"

    preflight

    echo ""
    echo "RemEx installed."
    echo "  Launch:          look for \"RemEx\" in your app menu, or run: remex-agent"
    echo "  Starts at login: yes (minimized to the tray, so your phone can connect)."
    echo "                   Turn this off in RemEx under Settings > \"Launch at login\","
    echo "                   or run the uninstall below."
    echo "  Pairing data:    $STATE_DIR (kept across reinstalls)"
    echo "  Health check:    $INSTALL_DIR/install.sh doctor"
    echo "  Uninstall:       $INSTALL_DIR/install.sh uninstall"
    ;;

uninstall)
    echo "Removing RemEx ..."
    stop_running_instance "$INSTALL_DIR"
    rm -f  "$BIN_DIR/remex-agent"
    rm -f  "$APP_DIR/remex-agent.desktop"
    rm -f  "$AUTOSTART_FILE"
    rm -f  "$ICON_DIR/remex.png"
    rm -rf "$INSTALL_DIR"
    # Sweep pre-2.0 names too, in case this uninstall follows an old install.
    rm -f  "$BIN_DIR/remex-client"
    rm -f  "$APP_DIR/remex-client.desktop"
    rm -f  "$AUTOSTART_DIR/remex-client.desktop"
    rm -rf "$LEGACY_INSTALL_DIR"
    update-desktop-database "$APP_DIR" 2>/dev/null || true
    gtk-update-icon-cache -f -t "$HOME/.local/share/icons/hicolor" 2>/dev/null || true
    echo "Done."
    echo ""
    echo "Your pairing data (certificate + paired phones) was kept at:"
    echo "  $STATE_DIR"
    echo "Keep it if you plan to reinstall — your phones will stay paired."
    echo "To wipe it too:  rm -rf \"$STATE_DIR\""
    ;;

doctor)
    if [[ -x "$INSTALL_DIR/Remex.Agent" ]]; then
        "$INSTALL_DIR/Remex.Agent" --doctor
    else
        "$SCRIPT_DIR/Remex.Agent" --doctor
    fi
    ;;

*)
    echo "Usage: $0 [install|uninstall|doctor]"
    exit 1
    ;;
esac
