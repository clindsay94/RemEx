# Android Development Setup

This is the developer SDK setup. To install the app as a user, see [INSTALL-ANDROID.md](INSTALL-ANDROID.md) (Google Play or APK sideload).

This guide helps you configure the Android SDK for building the RemEx Android app.

## Prerequisites
- .NET SDK 10.0 or higher
- Java Development Kit (JDK) 17 or higher
- Android SDK Command Line Tools

---

## Installation Steps

### Linux

1. **Install JDK:**
   ```bash
   # Ubuntu/Debian
   sudo apt update
   sudo apt install openjdk-17-jdk
   
   # Fedora/RHEL
   sudo dnf install java-17-openjdk-devel
   
   # Arch
   sudo pacman -S jdk17-openjdk
   
   # Verify installation
   java -version
   ```

2. **Download Android Command Line Tools:**
   ```bash
   mkdir -p ~/Android/Sdk/cmdline-tools
   cd ~/Android/Sdk/cmdline-tools
   
   # Download latest cmdline-tools (adjust version as needed)
   wget https://dl.google.com/android/repository/commandlinetools-linux-11076708_latest.zip
   
   unzip commandlinetools-linux-11076708_latest.zip
   mv cmdline-tools latest
   rm commandlinetools-linux-11076708_latest.zip
   ```

3. **Set environment variables:**
   
   Add to `~/.bashrc` or `~/.zshrc`:
   ```bash
   export ANDROID_HOME=$HOME/Android/Sdk
   export ANDROID_SDK_ROOT=$HOME/Android/Sdk
   export PATH=$PATH:$ANDROID_HOME/cmdline-tools/latest/bin
   export PATH=$PATH:$ANDROID_HOME/platform-tools
   ```
   
   Then reload:
   ```bash
   source ~/.bashrc  # or source ~/.zshrc
   ```

4. **Install SDK components:**
   ```bash
   # Accept all licenses
   yes | sdkmanager --licenses
   
   # Install required SDK components
   sdkmanager "platform-tools" "platforms;android-37" "build-tools;37.0.0"
   
   # Optional: Install additional components for emulator
   # sdkmanager "emulator" "system-images;android-37;google_apis;x86_64"
   ```

5. **Create `local.properties`:**
   ```bash
   cd /path/to/RemEx/remex.android
   echo "sdk.dir=$HOME/Android/Sdk" > local.properties
   ```

### Windows

1. **Install JDK:**
   - Download and install from [Adoptium](https://adoptium.net/) (Eclipse Temurin JDK 17+)
   - Or use [Oracle JDK](https://www.oracle.com/java/technologies/downloads/)
   - Verify: Open PowerShell and run `java -version`

2. **Download Android Command Line Tools:**
   - Download from: https://developer.android.com/studio#command-line-tools-only
   - Extract to: `C:\Android\Sdk\cmdline-tools\`
   - Rename the extracted `cmdline-tools` folder to `latest`
   - Final path: `C:\Android\Sdk\cmdline-tools\latest\`

3. **Set environment variables:**
   
   PowerShell (requires admin):
   ```powershell
   [Environment]::SetEnvironmentVariable("ANDROID_HOME", "C:\Android\Sdk", "User")
   [Environment]::SetEnvironmentVariable("ANDROID_SDK_ROOT", "C:\Android\Sdk", "User")
   
   # Add to PATH
   $currentPath = [Environment]::GetEnvironmentVariable("Path", "User")
   $newPath = "$currentPath;C:\Android\Sdk\cmdline-tools\latest\bin;C:\Android\Sdk\platform-tools"
   [Environment]::SetEnvironmentVariable("Path", $newPath, "User")
   ```
   
   **Or** set manually via System Properties:
   - Right-click This PC → Properties → Advanced system settings → Environment Variables
   - Add `ANDROID_HOME` = `C:\Android\Sdk`
   - Add `ANDROID_SDK_ROOT` = `C:\Android\Sdk`
   - Edit `Path` and add:
     - `C:\Android\Sdk\cmdline-tools\latest\bin`
     - `C:\Android\Sdk\platform-tools`

4. **Install SDK components:**
   
   Open new PowerShell/Command Prompt:
   ```powershell
   # Accept all licenses
   sdkmanager --licenses
   
   # Install required components
   sdkmanager "platform-tools" "platforms;android-37" "build-tools;37.0.0"
   ```

5. **Create `local.properties`:**
   ```powershell
   cd \path\to\RemEx\remex.android
   
   # PowerShell
   "sdk.dir=C:\\Android\\Sdk" | Out-File -Encoding ASCII local.properties
   
   # Or create manually with a text editor
   ```

### macOS

1. **Install JDK:**
   ```bash
   # Using Homebrew
   brew install openjdk@17
   
   # Link to system Java
   sudo ln -sfn $(brew --prefix)/opt/openjdk@17/libexec/openjdk.jdk \
       /Library/Java/JavaVirtualMachines/openjdk-17.jdk
   
   # Verify
   java -version
   ```

2. **Download Android Command Line Tools:**
   ```bash
   mkdir -p $HOME/Library/Android/sdk/cmdline-tools
   cd $HOME/Library/Android/sdk/cmdline-tools
   
   # Download latest cmdline-tools
   curl -O https://dl.google.com/android/repository/commandlinetools-mac-11076708_latest.zip
   
   unzip commandlinetools-mac-11076708_latest.zip
   mv cmdline-tools latest
   rm commandlinetools-mac-11076708_latest.zip
   ```

3. **Set environment variables:**
   
   Add to `~/.zshrc` or `~/.bash_profile`:
   ```bash
   export ANDROID_HOME=$HOME/Library/Android/sdk
   export ANDROID_SDK_ROOT=$HOME/Library/Android/sdk
   export PATH=$PATH:$ANDROID_HOME/cmdline-tools/latest/bin
   export PATH=$PATH:$ANDROID_HOME/platform-tools
   ```
   
   Reload:
   ```bash
   source ~/.zshrc
   ```

4. **Install SDK components:**
   ```bash
   # Accept licenses
   yes | sdkmanager --licenses
   
   # Install components
   sdkmanager "platform-tools" "platforms;android-37" "build-tools;37.0.0"
   ```

5. **Create `local.properties`:**
   ```bash
   cd /path/to/RemEx/remex.android
   echo "sdk.dir=$HOME/Library/Android/sdk" > local.properties
   ```

---

## Configuration

The `local.properties` file tells the Android build system where to find the SDK. This file is **gitignored** and must be created by each developer.

### Example `local.properties`:

**Linux/macOS:**
```properties
sdk.dir=/home/username/Android/Sdk
```

**Windows:**
```properties
sdk.dir=C:\\Android\\Sdk
```

> **Note:** Windows paths use double backslashes (`\\`) as escape characters.

### Using the Template

For convenience, copy from the template:
```bash
cd remex.android
cp local.properties.example local.properties
# Edit local.properties and update the sdk.dir path
```

---

## Verification

### Test the build:
```bash
cd remex.android
dotnet build
```

**Expected output:** Build succeeds without SDK errors.

### Common success indicators:
- ✅ `Build succeeded`
- ✅ No "Android SDK directory could not be found" error
- ✅ APK generated in `app/build/outputs/apk/`

---

## Troubleshooting

### Error: "Android SDK directory could not be found"

**Cause:** `local.properties` missing or has incorrect path.

**Solution:**
1. Verify `local.properties` exists in `remex.android/`
2. Check the `sdk.dir` path is correct
3. Ensure path uses correct separators (forward slashes on Linux/macOS, double backslashes on Windows)

### Error: "Failed to find Build Tools revision X.X.X"

**Cause:** Required build tools version not installed.

**Solution:**
```bash
sdkmanager "build-tools;37.0.0"
```

### Error: "License for package Android SDK Platform XX not accepted"

**Cause:** SDK licenses not accepted.

**Solution:**
```bash
sdkmanager --licenses
# Press 'y' and Enter for each license prompt
```

### Error: "ANDROID_HOME is not set"

**Cause:** Environment variable not configured.

**Solution:**
- Verify you added `ANDROID_HOME` to your shell profile
- Close and reopen terminal (or run `source ~/.bashrc`)
- On Windows, restart PowerShell/Command Prompt after setting environment variables

### Error: "Could not find or load main class com.android.sdkmanager.Main"

**Cause:** `cmdline-tools` folder structure incorrect.

**Solution:**
- Ensure path is: `Android/Sdk/cmdline-tools/latest/` (not `Android/Sdk/cmdline-tools/cmdline-tools/`)
- The extracted folder should be renamed to `latest`

### Build works but Android app doesn't run

**Cause:** SDK platform or emulator/device not configured.

**Solution:**
```bash
# Install the target Android platform
sdkmanager "platforms;android-37"

# For emulator testing, also install:
sdkmanager "emulator" "system-images;android-37;google_apis;x86_64"
```

---

## CI/CD Setup

For GitHub Actions or other CI environments, use the automated setup script:

```bash
# From repo root
chmod +x ./scripts/setup-android-sdk.sh
./scripts/setup-android-sdk.sh
```

This script handles:
- SDK download and extraction
- Component installation
- License acceptance
- `local.properties` creation

See [scripts/setup-android-sdk.sh](../scripts/setup-android-sdk.sh) for details.

## Security & Credentials

### Encrypted Storage
RemEx stores its **pinned host SPKI hashes** and **per-host reconnect secrets** in two separate Jetpack **DataStore** files (`remex_pinned_hosts` and `remex_reconnect_secrets`). Every value is encrypted with **Tink AES-256-GCM AEAD** before it is written, and the Tink keyset is itself sealed by an **Android Keystore**–backed key (`android-keystore://remex_pinned_host_key`) — so your paired-device list cannot be read off the device.
- **Dependencies:** `com.google.crypto.tink:tink-android:1.21.0`, `androidx.datastore:datastore-preferences:1.3.0-alpha09`
- **Note:** The deprecated `androidx.security:security-crypto` (`EncryptedSharedPreferences` / `MasterKey`) is intentionally **not** used.

---

## Crash reports: Android vitals, no Firebase

The app ships no analytics or crash-reporting SDK, so the Play data safety form can say "No data collected"
(RemEx-6cci2). Crashes and ANRs show up in Play Console under **Android vitals › Crashes and ANRs**.

- The release AAB carries the R8 mapping and full native debug symbols (`ndk { debugSymbolLevel = "FULL" }`),
  so Play symbolicates both Kotlin and `libRemexCore.so` stack traces. Nothing needs uploading separately.
  This only works because `remex.core.csproj` sets `StripSymbols=false` for Android: AGP strips the library
  for the APK and extracts `libRemexCore.so.dbg` into the bundle. A pre-stripped .so leaves Play with nothing.
- Only Play installs from users who share usage and diagnostics with Google are reported. GitHub APK installs
  are not.
- `app/build.gradle.kts` fails the build if the google-services, Crashlytics or Perf plugin is applied, or if
  Firebase Analytics/Crashlytics/Perf/Sessions, `play-services-measurement*` or `play-services-ads-identifier`
  reaches any classpath, including transitively. `NoTelemetrySdkTest` keeps that ban in place.
- ML Kit's bundled barcode scanner uploads usage diagnostics through datatransport. The manifest removes
  `TransportBackendDiscovery`, so those events have nowhere to go and scanning runs fully on-device.

Adding any of these back means changing the Play data safety form first. That's Connor's call, not a fix.

---

## Extra languages from Play's machine translations

On the phone only English (`values/strings.xml`) is hand-written. Every other language, all 48 of them, is Play
Console's Gemini translation, imported into `res/values-*` (RemEx-fwhqw). That includes es, fr, hi, id, pl, pt-BR, tr
and uk, which were hand-translated until 2026-10-25 and are Play's now. (The PC's `.resx` files are separate: those 9
languages stay reviewed.) Play's `pt` column becomes `values-pt-rBR`, since Play has no pt-BR column.

**Adding a string.** Add it to `values/strings.xml` and run:
```powershell
uv run python scripts/import_play_translations.py --fill-english
```
Release lint's `MissingTranslation` is fatal, so until Play has translated the new string the release build fails
for every language. `--fill-english` needs no Play export: for each language it copies the English text in for every
key that language lacks (a plural gets the language's own plural quantities, each holding English's "other" text, so
`MissingQuantity` stays quiet). It also drops keys English no longer has, and it never touches English. Run it again
and it adds nothing. `verify.ps1` suggests this command when lint reports `MissingTranslation`; it never runs it for
you. The next normal import below replaces each English copy with Play's translation, no `--update` needed.

**Getting Play's translations.** Play only translates what a draft release holds, and they only reach Play installs.
To give the GitHub APK the same languages:

1. Upload the release AAB to a **draft** release in Play Console and turn on automatic translation of app strings.
   Wait until Play shows the translations as ready.
2. On the translations page, set **Show rows** to 500 and paste `scripts/play-translations-exporter.js` into the
   DevTools console. It pages through the grid (about 7 minutes for 1,800 rows) and downloads
   `play-translations.json`.
3. Preview the import, then run it. `--supplement` on its own merges in
   `scripts/translations/plurals-supplement.json` (see below):
   ```powershell
   uv run python scripts/import_play_translations.py play-translations.json --supplement --dry-run
   uv run python scripts/import_play_translations.py play-translations.json --supplement
   ```
   It prints, for each language, how many strings it added, changed, skipped (format arguments that don't match
   English) and how many are missing (those show in English). Add `--update` to overwrite translations imported
   earlier; an English copy from `--fill-english` is replaced without it. English is never touched. Play's grid
   flattens paragraph breaks into spaces, so `--update` keeps an existing translation that still has its line
   breaks (the summary says how many).
4. Run the Android unit tests and the release lint gate
   (`cd remex.android; ./gradlew :app:testReleaseUnitTest :app:lintRelease`), then `scripts/verify.ps1`.

**Plurals.** Play's grid has no plurals, and release lint fails on a `<plurals>` a language is missing
(`MissingTranslation`). `scripts/translations/plurals-supplement.json` holds them for every language but English,
keyed by Play locale code, with the quantities each language uses (Arabic has all six, Russian
one/few/many/other, Japanese only other, and so on; lint's `MissingQuantity` and `UnusedQuantity` check them). It
has its own `source` (`remex-translation-supplement`), wins over the export for any key both have, and stops the
import before anything is written if an entry doesn't fit English (wrong format arguments, an unknown quantity,
a key English lacks). The ones for es, fr, hi, id, pl, pt, tr and uk are the hand-written plurals those apps
shipped before Play took them over; Claude wrote the rest and nobody who speaks those languages has reviewed them.
When English gains or changes a plural, `--fill-english` keeps the build green with English text; add the real
plural to the supplement for every language and re-run with `--update` when you want it translated.
`test_the_checked_in_supplement_covers_every_english_plural` fails until the supplement has it.

What the importer writes and why:
- One `res/values-<qualifier>/strings.xml` per new language, marked as machine-translated, holding only keys that
  exist in English and aren't `translatable="false"`. Re-running it changes nothing unless the export changed.
- Play codes map to the folders Android actually resolves: `he` → `values-iw`, `id` → `values-in` (Android
  rewrites both to the legacy codes before it looks up resources, so `values-he` would never be used),
  `no` → `values-nb` (devices report Norwegian Bokmål as `nb`), `zh-CN` → `values-zh-rCN`, `pt` →
  `values-pt-rBR`. Only Play's `en` is skipped.
- `res/xml/locales_config.xml` is regenerated from the folders, with modern tags (`he`, `id`). That file drives
  the system's per-app language setting and the in-app picker. `build.gradle.kts` builds `localeFilters` from the
  same folders, so nothing else needs editing.
- Some keys always stay English in machine-translated languages (`KEEP_ENGLISH` in the importer). The importer
  writes them as an exact copy of the English entry, replacing any translation the file has, even without
  `--update`. It copies rather than leaves them out because release lint fails a language that is missing a key.
  They are the 12 on-screen keys that
  draw a Latin legend (`cd_key_ctrl`, `cd_key_home`, `cd_key_pageup`, …): their spoken name must start with that
  legend (WCAG 2.5.3, `KeycapAccessibleNameTests`), and Play translates them (`Home` → `الرئيسية`). The glyph-only
  keys (Enter, Backspace, the arrows, Windows) draw no text, so their translated names stay. The font labels
  (`font_*`) also stay English, because a font's name is the same in every language (`AppFontsTest`) and Play
  transliterates them.
- Play sometimes returns an accented letter as `%00` plus its hex code (Catalan `informaci%00f3`). That would
  read as a format argument and be rejected, so the importer puts the letter back.

Hand-fixing a machine translation is fine: without `--update`, a re-import keeps your edit. To drop a bad one,
delete the line and run `--fill-english` (lint fails a language that lacks a key); the key shows English until Play's
version is imported. `KeycapAccessibleNameTests` checks every language, so a keyboard key name added by hand still
has to start with its legend.

---

## Additional Resources

- [Android Command Line Tools](https://developer.android.com/studio/command-line)
- [Android SDK Manager](https://developer.android.com/tools/sdkmanager)
- [Gradle Build Configuration](https://developer.android.com/build)
- [RemEx Contributing Guide](CONTRIBUTING.md)

---

## Quick Reference

| Command | Description |
|:--------|:------------|
| `sdkmanager --list` | List all available packages |
| `sdkmanager --update` | Update all installed packages |
| `sdkmanager --licenses` | Accept all SDK licenses |
| `sdkmanager "package-name"` | Install specific package |
| `sdkmanager --uninstall "package-name"` | Remove package |
| `adb devices` | List connected devices |
| `./gradlew tasks` | List available Gradle tasks |
| `sdkmanager "platforms;android-37"` | Install target SDK 37 |

---

If you encounter issues not covered here, please [open an issue](https://github.com/clindsay94/RemEx/issues) or ask in our community channels.
