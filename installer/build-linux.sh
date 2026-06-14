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

# ── Prerequisite check ───────────────────────────────────────────────────────
MISSING=()
command -v dotnet  >/dev/null 2>&1 || MISSING+=("dotnet (install .NET 10 SDK)")
command -v cmake   >/dev/null 2>&1 || MISSING+=("cmake")
command -v pkg-config >/dev/null 2>&1 || MISSING+=("pkg-config")
pkg-config --exists libpipewire-0.3 2>/dev/null || MISSING+=("libpipewire-0.3 dev headers (pacman: pipewire | apt: libpipewire-0.3-dev)")
if [[ ${#MISSING[@]} -gt 0 ]]; then
    echo "Error: missing required tools:" >&2
    for m in "${MISSING[@]}"; do echo "  • $m" >&2; done
    exit 1
fi

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

reset_stale_cmake_cache() {
    local source_dir="$1"
    local build_dir="$2"
    local cache_file="$build_dir/CMakeCache.txt"

    if [[ ! -f "$cache_file" ]]; then
        return
    fi

    local cached_source_dir=""
    local cached_build_dir=""
    cached_source_dir="$(grep '^CMAKE_HOME_DIRECTORY:INTERNAL=' "$cache_file" | cut -d= -f2- || true)"
    cached_build_dir="$(grep '^CMAKE_CACHEFILE_DIR:INTERNAL=' "$cache_file" | cut -d= -f2- || true)"

    if [[ "$cached_source_dir" != "$source_dir" || ( -n "$cached_build_dir" && "$cached_build_dir" != "$build_dir" ) ]]; then
        echo "Detected stale CMake cache for $build_dir; clearing it before rebuild."
        rm -rf "$build_dir"
    fi
}

cleanup_linux_package_artifacts() {
    shopt -s nullglob
    local artifacts=(
        "$OUTPUT_DIR"/remex-client-v*-linux-x64
        "$OUTPUT_DIR"/remex-client-v*-linux-x64.tar.gz
        "$OUTPUT_DIR"/remex-host-v*-linux-x64
        "$OUTPUT_DIR"/remex-host-v*-linux-x64.tar.gz
    )

    if [[ ${#artifacts[@]} -gt 0 ]]; then
        rm -rf "${artifacts[@]}"
    fi

    shopt -u nullglob
}

# ── Version ─────────────────────────────────────────────────────────────────
VERSION_FILE="$REPO_ROOT/RemEx.Android/app/version.properties"
if [[ ! -f "$VERSION_FILE" ]]; then
    echo "Error: version.properties not found at $VERSION_FILE" >&2
    exit 1
fi

VERSION="$(grep -m1 '^versionName=' "$VERSION_FILE" | cut -d= -f2- | tr -d '\r')"
if [[ -z "$VERSION" ]]; then
    echo "Error: could not read versionName from version.properties" >&2
    exit 1
fi

echo "Version: $VERSION"
mkdir -p "$OUTPUT_DIR"
cleanup_linux_package_artifacts

NATIVE_BRIDGE_DIR="$REPO_ROOT/Remex.Host.Native.Linux"
NATIVE_BRIDGE_BUILD_DIR="$NATIVE_BRIDGE_DIR/build"
NATIVE_BRIDGE_SO="$NATIVE_BRIDGE_BUILD_DIR/libremex_linux_bridge.so"

# ── Native bridge (shared by client and host) ────────────────────────────────
echo ""
echo "── Building native Linux bridge (libremex_linux_bridge.so) ─────────────"
reset_stale_cmake_cache "$NATIVE_BRIDGE_DIR" "$NATIVE_BRIDGE_BUILD_DIR"
cmake -B "$NATIVE_BRIDGE_BUILD_DIR" \
      -S "$NATIVE_BRIDGE_DIR" \
      -DCMAKE_BUILD_TYPE=Release
cmake --build "$NATIVE_BRIDGE_BUILD_DIR" --target remex_linux_bridge
if [[ ! -f "$NATIVE_BRIDGE_SO" ]]; then
    echo "Error: libremex_linux_bridge.so missing after cmake build." >&2
    exit 1
fi
if ! nm -D --defined-only "$NATIVE_BRIDGE_SO" | grep -q ' remex_pw_session_create_v2$'; then
    echo "Error: libremex_linux_bridge.so does not export remex_pw_session_create_v2." >&2
    exit 1
fi
echo "Native bridge → $NATIVE_BRIDGE_SO"

# ── Client ───────────────────────────────────────────────────────────────────
if [[ "$SKIP_CLIENT" == false ]]; then
    CLIENT_PROJ="$REPO_ROOT/RemEx.Host"
    CLIENT_PUBLISH="$CLIENT_PROJ/bin/Release/net10.0/linux-x64/publish"
    CLIENT_BRIDGE="$CLIENT_PUBLISH/runtimes/linux-x64/native/libremex_linux_bridge.so"
    CLIENT_STAGE="$OUTPUT_DIR/remex-client-v${VERSION}-linux-x64"

    echo ""
    echo "── Publishing RemEx.Host (linux-x64) ──────────────────────────"
    rm -rf "$CLIENT_PUBLISH"
    dotnet publish "$CLIENT_PROJ" -c Release -r linux-x64 --self-contained

    echo ""
    echo "── Verifying client native bridge publish layout ─────────────────────────"
    if [[ ! -f "$CLIENT_BRIDGE" ]]; then
        echo "Error: libremex_linux_bridge.so missing from client runtime path: $CLIENT_BRIDGE" >&2
        exit 1
    fi
    if [[ -f "$CLIENT_PUBLISH/libremex_linux_bridge.so" ]]; then
        echo "Error: stale client app-root libremex_linux_bridge.so should not be published." >&2
        exit 1
    fi
    echo "Native bridge → $CLIENT_BRIDGE"

    echo ""
    echo "── Packaging client ─────────────────────────────────────────────────────"
    rm -rf "$CLIENT_STAGE"
    mkdir -p "$CLIENT_STAGE"
    cp -r "$CLIENT_PUBLISH/." "$CLIENT_STAGE/"
    chmod +x "$CLIENT_STAGE/RemEx.Host"

    # Desktop entry and install script
    cp "$LINUX_DIR/remex-client.desktop" "$CLIENT_STAGE/"
    cp "$LINUX_DIR/client-install.sh"    "$CLIENT_STAGE/install.sh"
    chmod +x "$CLIENT_STAGE/install.sh"

    # Icon — prefer New-REMEX.png, then icon.png, then icon.ico
    if   [[ -f "$REPO_ROOT/Remex.Client/Assets/New-REMEX.png" ]]; then cp "$REPO_ROOT/Remex.Client/Assets/New-REMEX.png" "$CLIENT_STAGE/remex.png"
    elif [[ -f "$CLIENT_PROJ/icon.png" ]]; then cp "$CLIENT_PROJ/icon.png" "$CLIENT_STAGE/remex.png"
    elif [[ -f "$CLIENT_PROJ/icon.ico" ]]; then cp "$CLIENT_PROJ/icon.ico" "$CLIENT_STAGE/remex.ico"
    fi

    tar -czf "$OUTPUT_DIR/remex-client-v${VERSION}-linux-x64.tar.gz" \
        -C "$OUTPUT_DIR" "remex-client-v${VERSION}-linux-x64"
    rm -rf "$CLIENT_STAGE"
    echo "Client → $OUTPUT_DIR/remex-client-v${VERSION}-linux-x64.tar.gz"
fi

# ── Host ─────────────────────────────────────────────────────────────────────
if [[ "$SKIP_HOST" == false ]]; then
    HOST_PROJ="$REPO_ROOT/RemEx.Host"
    HOST_PUBLISH="$HOST_PROJ/bin/Release/net10.0/linux-x64/publish"
    HOST_BRIDGE="$HOST_PUBLISH/runtimes/linux-x64/native/libremex_linux_bridge.so"
    HOST_STAGE="$OUTPUT_DIR/remex-host-v${VERSION}-linux-x64"

    # The host and the desktop client are now the same consolidated RemEx.Host binary, published to
    # the same path. Reuse the client's publish output when it is already present (built just above);
    # only publish here when the client stage was skipped.
    if [[ ! -f "$HOST_PUBLISH/RemEx.Host" ]]; then
        echo ""
        echo "── Publishing RemEx.Host (linux-x64) ───────────────────────────────────"
        rm -rf "$HOST_PUBLISH"
        dotnet publish "$HOST_PROJ" -c Release -r linux-x64 --self-contained
    else
        echo ""
        echo "── Reusing RemEx.Host publish from the client stage (same consolidated binary) ──"
    fi

    echo ""
    echo "── Verifying host native bridge publish layout ───────────────────────────"
    if [[ ! -f "$HOST_BRIDGE" ]]; then
        echo "Error: libremex_linux_bridge.so missing from host runtime path: $HOST_BRIDGE" >&2
        exit 1
    fi
    if [[ -f "$HOST_PUBLISH/libremex_linux_bridge.so" ]]; then
        echo "Error: stale host app-root libremex_linux_bridge.so should not be published." >&2
        exit 1
    fi
    echo "Native bridge → $HOST_BRIDGE"

    echo ""
    echo "── Packaging host ───────────────────────────────────────────────────────"
    rm -rf "$HOST_STAGE"
    mkdir -p "$HOST_STAGE"
    cp -r "$HOST_PUBLISH/." "$HOST_STAGE/"
    chmod +x "$HOST_STAGE/RemEx.Host"

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
