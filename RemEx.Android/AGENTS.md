# RemEx.Android — Agent Playbook (2.0)

This is the native Android client, built with Kotlin, Jetpack Compose, and a JNI bridge to the Core logic.

## 2.0 Role: Mobile Flagship & Play Store Readiness
Android is the primary target for 2.0's release engineering. It must achieve stable Material 3 styling and full compliance with Play Store security/performance policies.

## Assigned 2.0 Tracks
- **Track 1D**: Android TLS support and `EncryptedSharedPreferences` for pins.
- **Track 2C**: Material 3 stabilization (`targetSdk 37`).
- **Track 2D**: Release Engineering (R8, AAB, network security config).
- **Track 2G**: Battery optimization onboarding.
- **Track 2I**: Quick Settings tile (Lock PC).

## Tactical Anchor Nodes (GitNexus)
Use `gitnexus_context` to manage the Kotlin-to-C# bridge and UI:
- `RemexClientManager`: The Kotlin singleton managing the app state.
- `RemexCoreClient`: The JNI bridge declarations.
- `PairingScreen`: The new Compose screen for entering 6-digit PINs.
- `AndroidManifest.xml`: Critical for predictive back and security flags.

## Verification Checklist
- [ ] `./gradlew assembleDebug` passes with exit code 0.
- [ ] `apktool d` verification: `android:usesCleartextTraffic="false"` in manifest.
- [ ] `EncryptedSharedPreferences` correctly stores host SPKI hashes.
- [ ] Target SDK is locked at `37`.
