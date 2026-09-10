<!-- Bead ID goes in the PR title, e.g. fix(pairing): ... (RemEx-xxxx) -->

## Checklist

- [ ] Title includes the bead ID
- [ ] `scripts/verify.ps1` was run and `-Check` reports a valid receipt
- [ ] If Kotlin changed: built/verified with the release variant only, not debug
- [ ] If any user-facing string changed: all 9 locale files (English plus 8 translations) were updated on each affected platform
- [ ] If touching capture, the stream, the decoder, SurfaceView, pairing, or the session guard: read `docs/REGRESSION-GUARDS.md` first
- [ ] If any `.axaml` changed: looked at the result via the `ui-verify` skill

## What changed and why

## Testing
