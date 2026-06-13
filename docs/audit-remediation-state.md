# Task State: Android / Compose / JNI Audit Remediation

## Goal
Remediate a list of JNI, Android/Compose build system, memory, input, and Avalonia UI issues detailed in `docs/AUDIT-2026-06-09-REMEDIATION-PRD.md`.

## Success criteria
- Code compiles for both C# (.NET 10) and Kotlin/Jetpack Compose Android platforms.
- JNI helper string and method invocation APIs are correct and run without CheckJNI aborts.
- Thread attachment is thread-static with appropriate daemon mode and TLS destructor cleanup.
- Compose lifecycle state collection is lifecycle-aware (STARTED boundary).
- Splash screen animations and layout elements are optimized and adaptive.
- Keyboard routing and Evdev translation maps keys correctly on Linux host.
- Avalonia UI uses the style system for nav rails, geometry icons, and theme colors.
- Verification tests pass on host and client.

## Status
completed

## Current step
None

## Completed
- [x] Preliminary: Claimed beads tasks `RemEx-dnj` and `RemEx-bxg`.
- [x] Preliminary: Created state tracking file at [docs/audit-remediation-state.md](file:///Z:/RemEx/docs/audit-remediation-state.md).
- [x] Step 1: Implemented JNI-1 (UTF-16 strings in JniHelper) and JNI-2 (JNI ExceptionCheck/ExceptionClear guards in callbacks).
- [x] Step 2: Implemented BLD-1 (dynamic signingConfig keystore fallback) and BLD-2 (restricting abiFilters to arm64-v8a) in build.gradle.kts.
- [x] Step 3: Implemented JNI-3 (CallVoidMethodA with JValue), JNI-4 (lazy thread daemon attachment cache with pthread TLS destructor), and JNI-5 (source-serialized native operation errors) in JniHelper.cs and AndroidNativeExports.cs.
- [x] Step 4: Configured default multiline Enter actions in RemoteDesktopScreen.kt (REM-1), translated raw protocol keycodes in LinuxInputBackendRouter.cs and LinuxInputSimulationService.cs, and added unit tests in LinuxInputEventTranslatorTests.cs (REM-2).
- [x] Step 5: Configured dual-plane host ports and port-qualified mDNS instance names (ARCH-1), and implemented autostart launch-at-login on Windows and Linux (ARCH-2) with a settings toggle in SettingsView.axaml and Inno Setup configuration in RemEx.iss.
- [x] Step 6: modernised C# dependency versions under CPM (BLD-C2), removed dead build/utility files (BLD-C1), extended targets in unified build script, updated API platforms to 37 (BLD-C3), and verified builds and tests.
- [x] Step 7: Implemented Compose State & Lifecycles (CMP-1 to CMP-6) and splash screen animations (SPL-1 to SPL-7).
- [x] Step 8: Cleaned up ProGuard rules (PG-1), deleted dead JNI exports and triggerHaptic (KT-1), aligned version catalog references and removed unused versions (VC-1), and documented restricted API usages (THM-1).
- [x] Step 9: Removed ConfigureAwait(false) from host (CS-1), replaced unsafe GetService calls with GetRequiredService (CS-2), and assessed/documented CS-3 risk decision.

## Active owners
- Coordinator: Antigravity (AI Assistant)
- Worker: Antigravity (AI Assistant)

## Blockers
- None

## Next action
None

## Next checkpoint
None

## Notes
- Do not commit or push changes. Leave working tree dirty and report changed files at the end.
- Verify JNI function-table offsets carefully.
- Ensure all tests pass.
