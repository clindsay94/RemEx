#!/usr/bin/env bash
# Builds the RemEx Linux package:
#   Output/remex-agent-vX.Y.Z-linux-x64.tar.gz
#
# There is ONE package. Remex.Agent is the entire PC side (host + desktop UI in a
# single process), exactly like the Windows install. The old remex-client /
# remex-host split no longer exists.
#
# Usage:
#   ./installer/build-linux.sh

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

for arg in "$@"; do
    case "$arg" in
        --skip-client|--skip-host)
            echo "Warning: '$arg' is obsolete — there is a single remex-agent package now. Ignoring." >&2
            ;;
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
    # Also sweeps tarballs from the pre-2.0 client/host split so stale packages
    # can never be installed by accident again.
    local artifacts=(
        "$OUTPUT_DIR"/remex-agent-v*-linux-x64
        "$OUTPUT_DIR"/remex-agent-v*-linux-x64.tar.gz
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
VERSION_FILE="$REPO_ROOT/remex.android/app/version.properties"
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

NATIVE_BRIDGE_DIR="$REPO_ROOT/remex.agent.native.linux"
NATIVE_BRIDGE_BUILD_DIR="$NATIVE_BRIDGE_DIR/build"
NATIVE_BRIDGE_SO="$NATIVE_BRIDGE_BUILD_DIR/libremex_linux_bridge.so"

# ── Native bridge ────────────────────────────────────────────────────────────
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

# ── Agent (the entire PC side) ───────────────────────────────────────────────
AGENT_PROJ="$REPO_ROOT/remex.agent"
AGENT_PUBLISH="$REPO_ROOT/artifacts/publish/remex.agent/release_linux-x64"
AGENT_BRIDGE="$AGENT_PUBLISH/runtimes/linux-x64/native/libremex_linux_bridge.so"
AGENT_STAGE="$OUTPUT_DIR/remex-agent-v${VERSION}-linux-x64"

echo ""
echo "── Publishing Remex.Agent (linux-x64) ──────────────────────────────────"
rm -rf "$AGENT_PUBLISH"
dotnet publish "$AGENT_PROJ" -c Release -r linux-x64 --self-contained

echo ""
echo "── Verifying native bridge publish layout ──────────────────────────────"
if [[ ! -f "$AGENT_BRIDGE" ]]; then
    echo "Error: libremex_linux_bridge.so missing from runtime path: $AGENT_BRIDGE" >&2
    exit 1
fi
if [[ -f "$AGENT_PUBLISH/libremex_linux_bridge.so" ]]; then
    echo "Error: stale app-root libremex_linux_bridge.so should not be published." >&2
    exit 1
fi
echo "Native bridge → $AGENT_BRIDGE"

echo ""
echo "── Packaging remex-agent ────────────────────────────────────────────────"
rm -rf "$AGENT_STAGE"
mkdir -p "$AGENT_STAGE"
cp -r "$AGENT_PUBLISH/." "$AGENT_STAGE/"
chmod +x "$AGENT_STAGE/Remex.Agent"

# Desktop entry and install script
cp "$LINUX_DIR/remex-agent.desktop" "$AGENT_STAGE/"
cp "$LINUX_DIR/agent-install.sh"    "$AGENT_STAGE/install.sh"
chmod +x "$AGENT_STAGE/install.sh"

# Icon — use the generated brand mark (PNG preferred for Linux .desktop, ICO fallback)
if   [[ -f "$REPO_ROOT/remex.desktop/Assets/icon.png" ]]; then cp "$REPO_ROOT/remex.desktop/Assets/icon.png" "$AGENT_STAGE/remex.png"
elif [[ -f "$AGENT_PROJ/icon.ico" ]]; then cp "$AGENT_PROJ/icon.ico" "$AGENT_STAGE/remex.ico"
fi

tar -czf "$OUTPUT_DIR/remex-agent-v${VERSION}-linux-x64.tar.gz" \
    -C "$OUTPUT_DIR" "remex-agent-v${VERSION}-linux-x64"
rm -rf "$AGENT_STAGE"
echo "Agent → $OUTPUT_DIR/remex-agent-v${VERSION}-linux-x64.tar.gz"

echo ""
echo "═════════════════════════════════════════════════════"
echo "  Linux package built successfully!"
echo "  Output: $OUTPUT_DIR/"
echo "═════════════════════════════════════════════════════"
