#!/usr/bin/env bash
# Builds the RemEx Linux packages:
#   Output/remex-client-vX.Y.Z-linux-x64.tar.gz
#   Output/remex-host-vX.Y.Z-linux-x64.tar.gz
#
# Usage:
#   ./installer/build-linux.sh
#   ./installer/build-linux.sh --skip-client
#   ./installer/build-linux.sh --skip-host

set -euo pipefail

SKIP_CLIENT=false
SKIP_HOST=false
for arg in "$@"; do
    case "$arg" in
        --skip-client) SKIP_CLIENT=true ;;
        --skip-host)   SKIP_HOST=true   ;;
        *) echo "Unknown argument: $arg"; exit 1 ;;
    esac
done

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
LINUX_DIR="$SCRIPT_DIR/linux"
OUTPUT_DIR="$SCRIPT_DIR/Output"

# ── Version ─────────────────────────────────────────────────────────────────
VERSION_FILE="$REPO_ROOT/RemEx.Android/app/version.properties"
if [[ ! -f "$VERSION_FILE" ]]; then
    echo "Error: version.properties not found at $VERSION_FILE" >&2
    exit 1
fi

VERSION=$(grep '^versionName=' "$VERSION_FILE" | cut -d= -f2)
if [[ -z "$VERSION" ]]; then
    echo "Error: could not read versionName from version.properties" >&2
    exit 1
fi

echo "Version: $VERSION"
mkdir -p "$OUTPUT_DIR"

# ── Client ───────────────────────────────────────────────────────────────────
if [[ "$SKIP_CLIENT" == false ]]; then
    CLIENT_PROJ="$REPO_ROOT/Remex.Client.Desktop"
    CLIENT_PUBLISH="$CLIENT_PROJ/bin/Release/net10.0/linux-x64/publish"
    CLIENT_STAGE="$OUTPUT_DIR/remex-client-v${VERSION}-linux-x64"

    echo ""
    echo "── Publishing Remex.Client.Desktop (linux-x64) ──────────────────────────"
    dotnet publish "$CLIENT_PROJ" -c Release -r linux-x64 --self-contained

    echo ""
    echo "── Packaging client ─────────────────────────────────────────────────────"
    rm -rf "$CLIENT_STAGE"
    mkdir -p "$CLIENT_STAGE"
    cp -r "$CLIENT_PUBLISH/." "$CLIENT_STAGE/"
    chmod +x "$CLIENT_STAGE/Remex.Client.Desktop"

    # Desktop entry and install script
    cp "$LINUX_DIR/remex-client.desktop" "$CLIENT_STAGE/"
    cp "$LINUX_DIR/client-install.sh"    "$CLIENT_STAGE/install.sh"
    chmod +x "$CLIENT_STAGE/install.sh"

    # Icon — prefer PNG, fall back to ico (install script handles the rest)
    if   [[ -f "$CLIENT_PROJ/icon.png" ]]; then cp "$CLIENT_PROJ/icon.png" "$CLIENT_STAGE/remex.png"
    elif [[ -f "$CLIENT_PROJ/icon.ico" ]]; then cp "$CLIENT_PROJ/icon.ico" "$CLIENT_STAGE/remex.ico"
    fi

    tar -czf "$OUTPUT_DIR/remex-client-v${VERSION}-linux-x64.tar.gz" \
        -C "$OUTPUT_DIR" "remex-client-v${VERSION}-linux-x64"
    rm -rf "$CLIENT_STAGE"
    echo "Client → $OUTPUT_DIR/remex-client-v${VERSION}-linux-x64.tar.gz"
fi

# ── Host ─────────────────────────────────────────────────────────────────────
if [[ "$SKIP_HOST" == false ]]; then
    HOST_PROJ="$REPO_ROOT/Remex.Host"
    HOST_PUBLISH="$HOST_PROJ/bin/Release/net10.0/linux-x64/publish"
    HOST_STAGE="$OUTPUT_DIR/remex-host-v${VERSION}-linux-x64"

    echo ""
    echo "── Publishing Remex.Host (linux-x64) ───────────────────────────────────"
    dotnet publish "$HOST_PROJ" -c Release -r linux-x64 --self-contained

    echo ""
    echo "── Packaging host ───────────────────────────────────────────────────────"
    rm -rf "$HOST_STAGE"
    mkdir -p "$HOST_STAGE"
    cp -r "$HOST_PUBLISH/." "$HOST_STAGE/"
    chmod +x "$HOST_STAGE/Remex.Host"

    cp "$LINUX_DIR/remex-host.service" "$HOST_STAGE/"
    cp "$LINUX_DIR/host-install.sh"    "$HOST_STAGE/install.sh"
    chmod +x "$HOST_STAGE/install.sh"

    tar -czf "$OUTPUT_DIR/remex-host-v${VERSION}-linux-x64.tar.gz" \
        -C "$OUTPUT_DIR" "remex-host-v${VERSION}-linux-x64"
    rm -rf "$HOST_STAGE"
    echo "Host   → $OUTPUT_DIR/remex-host-v${VERSION}-linux-x64.tar.gz"
fi

echo ""
echo "═════════════════════════════════════════════════════"
echo "  Linux packages built successfully!"
echo "  Output: $OUTPUT_DIR/"
echo "═════════════════════════════════════════════════════"
