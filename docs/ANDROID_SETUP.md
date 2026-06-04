# Android Development Setup

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
   sdkmanager "platform-tools" "platforms;android-34" "build-tools;34.0.0"
   
   # Optional: Install additional components for emulator
   # sdkmanager "emulator" "system-images;android-34;google_apis;x86_64"
   ```

5. **Create `local.properties`:**
   ```bash
   cd /path/to/RemEx/RemEx.Android
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
   sdkmanager "platform-tools" "platforms;android-34" "build-tools;34.0.0"
   ```

5. **Create `local.properties`:**
   ```powershell
   cd \path\to\RemEx\RemEx.Android
   
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
   sdkmanager "platform-tools" "platforms;android-34" "build-tools;34.0.0"
   ```

5. **Create `local.properties`:**
   ```bash
   cd /path/to/RemEx/RemEx.Android
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
cd RemEx.Android
cp local.properties.example local.properties
# Edit local.properties and update the sdk.dir path
```

---

## Verification

### Test the build:
```bash
cd RemEx.Android
dotnet build
```

**Expected output:** Build succeeds without SDK errors.

### Common success indicators:
- ✅ `Build succeeded`
- ✅ No "Android SDK directory could not be found" error
- ✅ APK generated in `app/build/outputs/apk/`

---

##Troubleshooting

### Error: "Android SDK directory could not be found"

**Cause:** `local.properties` missing or has incorrect path.

**Solution:**
1. Verify `local.properties` exists in `RemEx.Android/`
2. Check the `sdk.dir` path is correct
3. Ensure path uses correct separators (forward slashes on Linux/macOS, double backslashes on Windows)

### Error: "Failed to find Build Tools revision X.X.X"

**Cause:** Required build tools version not installed.

**Solution:**
```bash
sdkmanager "build-tools;34.0.0"
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
sdkmanager "platforms;android-34"

# For emulator testing, also install:
sdkmanager "emulator" "system-images;android-34;google_apis;x86_64"
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

## 🛡️ Security & Credentials

### Encrypted Storage
RemEx 2.0 uses `EncryptedSharedPreferences` to securely store pinned host certificate hashes. This prevents unauthorized access to your paired device list.
- **Dependency:** `androidx.security:security-crypto:1.1.0-alpha06`
- **Location:** `remex_pinned_hosts.xml` (excluded from Android Auto Backup)

---

## 📈 Firebase & Crashlytics

For production builds, Firebase Crashlytics is used for crash reporting and NDK symbol analysis.

### Prerequisites
1. Create a Firebase project in the [Firebase Console](https://console.firebase.google.com/).
2. Add an Android app with package name `com.clindsay94.remex`.
3. Download `google-services.json` and place it in `RemEx.Android/app/`.

### NDK Symbol Upload
The build system is configured to automatically upload unstripped native symbols (`libRemexCore.so`) to Firebase. This enables full C# stack traces for native crashes.

---

## Additional Resources

- [Android Command Line Tools](https://developer.android.com/studio/command-line)
- [Android SDK Manager](https://developer.android.com/tools/sdkmanager)
- [Gradle Build Configuration](https://developer.android.com/build)
- [RemEx Contributing Guide](../CONTRIBUTING.md)

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
| `sdkmanager "platforms;android-35"` | Install target SDK 35 |

---

If you encounter issues not covered here, please [open an issue](https://github.com/clindsay94/RemEx/issues) or ask in our community channels.
