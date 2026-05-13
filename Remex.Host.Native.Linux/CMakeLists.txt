# CMakeLists.txt — Remex.Host.Native.Linux
#
# Builds libremex_linux_bridge.so — the native helper library that provides
# PipeWire capture and uinput/libei input injection for RemEx on Linux.
#
# Dependencies (linked at RUNTIME via dlopen — not required at build time):
#   libpipewire-0.3  — PipeWire screen capture
#   libei-1.0        — EIS/libei input injection (Wayland SENDER mode)
#   libevdev         — used by the managed layer's probe, not directly here
#
# The only hard build dependencies are:
#   - A C11 compiler (gcc or clang)
#   - Linux kernel uinput headers (linux/uinput.h, linux/input.h)
#   - pthreads
#
# Output: libremex_linux_bridge.so  (installed to the host project's publish dir)

cmake_minimum_required(VERSION 3.16)
project(remex_linux_bridge C)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

# ── Source files ───────────────────────────────────────────────────────────
set(SOURCES
    src/pipewire_capture.c
    src/libei_sender.c
    src/uinput_tablet.c
)

# ── Shared library ─────────────────────────────────────────────────────────
add_library(remex_linux_bridge SHARED ${SOURCES})

target_include_directories(remex_linux_bridge PRIVATE
    include
)

# pthreads — hard build dependency (for capture thread)
find_package(Threads REQUIRED)
target_link_libraries(remex_linux_bridge PRIVATE Threads::Threads ${CMAKE_DL_LIBS})

# ── Compiler warnings ──────────────────────────────────────────────────────
target_compile_options(remex_linux_bridge PRIVATE
    -Wall
    -Wextra
    -Wno-unused-parameter
    -Wno-missing-field-initializers
    -fvisibility=hidden
)

# Export only the remex_* symbols; keep PipeWire/libei internals hidden.
set_target_properties(remex_linux_bridge PROPERTIES
    C_VISIBILITY_PRESET hidden
    VISIBILITY_INLINES_HIDDEN ON
)

# Annotate exported symbols so they survive -fvisibility=hidden.
# We set REMEX_EXPORT=__attribute__((visibility("default"))) for all public API.
target_compile_definitions(remex_linux_bridge PRIVATE
    REMEX_EXPORT=__attribute__\(\(visibility\(\"default\"\)\)\)
)

# ── Install ─────────────────────────────────────────────────────────────────
install(TARGETS remex_linux_bridge
    LIBRARY DESTINATION lib
    RUNTIME DESTINATION bin
)
