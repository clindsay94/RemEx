# RemEx Remediation PRD — Android / Compose / JNI Audit (2026-06-09)

**Audience:** an autonomous coding agent (LM Studio, local). Follow each issue's *Fix* section literally. Line numbers are anchors as of commit `b352a24` (branch `2.0`) — always locate code by the **function/symbol name and quoted snippet**, not by line number alone, since lines drift.

---

## 0. Agent Ground Rules (read first)

1. **Never delete** any class registered in `RemEx.Android/app/src/main/AndroidManifest.xml` (all `tile/*TileService`, `widget/*Widget*`, `widget/*ConfigActivity`, `service/*`). A reference-count scan flags them as "unused" — they are instantiated by the OS, not by Kotlin code.
2. Do not run `git commit`, `git push`, or `bd dolt push`. Leave the working tree dirty and report changed files at the end.
3. If GitNexus MCP tools are available, run `gitnexus_impact({target: "<symbol>", direction: "upstream"})` before editing any C# symbol, per `CLAUDE.md`.
4. Do **NOT** add `ConfigureAwait(false)` anywhere — repo convention forbids it (`docs/ASYNC_GUIDELINES.md`).
5. After completing each phase, run the verification commands in §6 before moving to the next phase.
6. Work the issues **in order**: P0 → P1 → P2 → P3. Each issue is independent unless a dependency is noted.

---

## 1. P0 — Critical (crash / data-corruption / release-integrity)

### JNI-1: `NewStringUTF`/`GetStringUTFChars` violate Modified-UTF-8 — corrupts or aborts on non-BMP text
**File:** `Remex.Core/Native/JniHelper.cs` (functions `CreateJString` lines ~46–58, `ReadJString` lines ~23–44)

**Problem:** `CreateJString` passes **standard UTF-8** bytes (`Encoding.UTF8.GetBytes`) to JNI `NewStringUTF` (fn index 167), which requires **Modified UTF-8** (CESU-8: supplementary chars as 6-byte surrogate pairs, `\0` as `0xC0 0x80`). Any emoji / CJK-extension char in a hostname, process name, app label, or file name (these all flow through `NotifyJavaData` JSON) produces malformed Modified-UTF-8 → ART aborts the process under CheckJNI, or silently corrupts the string in release. Symmetrically, `ReadJString` decodes `GetStringUTFChars` output (Modified UTF-8) with `Marshal.PtrToStringUTF8` (standard UTF-8) → supplementary chars from Kotlin (e.g. a file path with emoji sent to `SendMessageNative`) decode as garbage replacement chars.

**Fix:** switch both helpers to the UTF-16 JNI string APIs, which need no transcoding. Replace the two functions with:

```csharp
public static string? ReadJString(IntPtr envPtr, IntPtr jstring)
{
    if (envPtr == IntPtr.Zero || jstring == IntPtr.Zero) return null;

    var env = (JNIEnv*)envPtr;
    // GetStringLength is at index 164
    var getStringLength = (delegate* unmanaged<IntPtr, IntPtr, int>)env->Functions[164];
    // GetStringChars is at index 165
    var getStringChars = (delegate* unmanaged<IntPtr, IntPtr, byte*, char*>)env->Functions[165];
    // ReleaseStringChars is at index 166
    var releaseStringChars = (delegate* unmanaged<IntPtr, IntPtr, char*, void>)env->Functions[166];

    int length = getStringLength(envPtr, jstring);
    if (length < 0) return null;
    if (length == 0) return string.Empty;

    char* chars = getStringChars(envPtr, jstring, null);
    if (chars == null) return null;

    try
    {
        return new string(chars, 0, length);
    }
    finally
    {
        releaseStringChars(envPtr, jstring, chars);
    }
}

public static IntPtr CreateJString(IntPtr envPtr, string? value)
{
    if (envPtr == IntPtr.Zero || value == null) return IntPtr.Zero;

    var env = (JNIEnv*)envPtr;
    // NewString (UTF-16) is at index 163
    var newString = (delegate* unmanaged<IntPtr, char*, int, IntPtr>)env->Functions[163];

    fixed (char* pValue = value)
    {
        return newString(envPtr, pValue, value.Length);
    }
}
```

**Acceptance:** `dotnet build Remex.Core` succeeds. Grep confirms no remaining references to `Functions[167]`, `Functions[169]`, `Functions[170]` in `JniHelper.cs`. (JNI function-table indices: `NewString`=163, `GetStringLength`=164, `GetStringChars`=165, `ReleaseStringChars`=166 — these are the standard JNIEnv slots; do not renumber.)

---

### JNI-2: No `ExceptionCheck`/`ExceptionClear` after calling into Kotlin callbacks — pending-exception UB
**File:** `Remex.Core/Native/AndroidNativeExports.cs` — `NotifyJavaData` (~line 953), `NotifyJavaFrame` (~line 884), `NotifyJavaConnectionState` (~line 987)

**Problem:** After `JniHelper.CallVoidMethod(...)` invokes a Kotlin `RemexCallback` method, the Kotlin code may throw. JNI leaves that exception **pending on the attached thread**; the code then calls `DeleteLocalRef` / `DetachCurrentThread` and, worse, the *next* callback on a reused .NET thread-pool thread makes JNI calls with a pending exception — undefined behavior, typically a runtime abort ("JNI called with pending exception").

**Fix:** in all three `NotifyJava*` methods, immediately after **every** `JniHelper.CallVoidMethod(...)` call, add:

```csharp
if (JniHelper.ExceptionCheck(env))
{
    JniHelper.ExceptionClear(env);
    JniHelper.AndroidLogE("RemexNative", "Java callback threw an exception; cleared to protect the JNI bridge.");
}
```

Place it inside the existing `try` block, directly after the `CallVoidMethod` line (before the `finally` that deletes local refs). There are exactly **3** call sites: one in `NotifyJavaFrame` (line ~909), one in `NotifyJavaData` (line ~974), one in `NotifyJavaConnectionState` (line ~1006).

**Acceptance:** grep shows each of the 3 `CallVoidMethod(` call sites in `NotifyJava*` is followed within 5 lines by `ExceptionCheck(env)`.

---

### BLD-1: Release APK is signed with the **debug** keystore
**File:** `RemEx.Android/app/build.gradle.kts`, `buildTypes { release { ... } }` block (~line 90)

**Problem:** A `release` signing config is created from `local.properties` (`remex.signing.*`) but the release build type sets `signingConfig = signingConfigs.getByName("debug")`. Every "release" APK is debug-signed: not installable as an upgrade over a properly-signed build, not uploadable to Play.

**Fix:** replace the line
```kotlin
signingConfig = signingConfigs.getByName("debug")
```
with:
```kotlin
// Use the real release keystore when configured in local.properties;
// fall back to debug signing for local test builds without secrets.
signingConfig = if (androidLocalProperties.getProperty("remex.signing.storeFile") != null)
    signingConfigs.getByName("release")
else
    signingConfigs.getByName("debug")
```

**Acceptance:** `gradlew :app:assembleRelease` still succeeds on a machine without `remex.signing.*` set (falls back to debug), and uses the release keystore when the properties exist.

---

### BLD-2: `abiFilters` declares `x86_64` but only an `arm64-v8a` `libRemexCore.so` is produced
**File:** `RemEx.Android/app/build.gradle.kts`, `defaultConfig { ndk { abiFilters += listOf("arm64-v8a", "x86_64") } }` (~line 73)

**Problem:** The NativeAOT pipeline generates only `generated/remexJniLibs/<variant>/arm64-v8a/libRemexCore.so` (see the `remexGenerated*Arm64So` vals later in the same file). On an x86_64 emulator the APK installs, `System.loadLibrary("RemexCore")` throws `UnsatisfiedLinkError`, `RemexCoreClient.isLibraryLoaded` stays `false`, and every feature silently degrades to "Library not loaded" failures.

**Fix (choose the simple option):** remove `"x86_64"`:
```kotlin
ndk {
    abiFilters += listOf("arm64-v8a")
}
```
Do **not** attempt to add a linux-bionic-x64 NativeAOT build in this pass.

**Acceptance:** release APK contains only `lib/arm64-v8a/`; gradle sync passes.

---

## 2. P1 — High (glitches users will hit)

### JNI-3: `CallVoidMethod` (variadic) invoked through a fixed-arg function pointer
**Files:** `Remex.Core/Native/JniHelper.cs` (both `CallVoidMethod` overloads, lines ~132–146)

**Problem:** JNI's `CallVoidMethod` is a **varargs** C function. Calling it through `delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, void>` works on Android arm64 by ABI coincidence (AAPCS64 passes anonymous args like named ones) but is undefined behavior in general and breaks on x86-64 System-V (the `AL` register protocol for varargs) — i.e. exactly the emulator scenario from BLD-2, and any future ABI. The correct fixed-signature API is **`CallVoidMethodA`** (fn index 63), which takes a `jvalue[]`.

**Fix:** replace both `CallVoidMethod` overloads in `JniHelper.cs` with `CallVoidMethodA`-based versions:

```csharp
[StructLayout(LayoutKind.Explicit)]
public struct JValue
{
    [FieldOffset(0)] public byte Z;      // jboolean
    [FieldOffset(0)] public int I;       // jint
    [FieldOffset(0)] public long J;      // jlong
    [FieldOffset(0)] public IntPtr L;    // jobject
}

public static void CallVoidMethod(IntPtr envPtr, IntPtr obj, IntPtr methodId, IntPtr arg)
{
    var env = (JNIEnv*)envPtr;
    // CallVoidMethodA is at index 63 (fixed-signature jvalue[] variant of CallVoidMethod)
    var callVoidMethodA = (delegate* unmanaged<IntPtr, IntPtr, IntPtr, JValue*, void>)env->Functions[63];
    var args = stackalloc JValue[1];
    args[0].L = arg;
    callVoidMethodA(envPtr, obj, methodId, args);
}

public static void CallVoidMethod(IntPtr envPtr, IntPtr obj, IntPtr methodId, bool arg)
{
    var env = (JNIEnv*)envPtr;
    var callVoidMethodA = (delegate* unmanaged<IntPtr, IntPtr, IntPtr, JValue*, void>)env->Functions[63];
    var args = stackalloc JValue[1];
    args[0].Z = arg ? (byte)1 : (byte)0;
    callVoidMethodA(envPtr, obj, methodId, args);
}
```

Method signatures and call sites stay identical, so `AndroidNativeExports.cs` needs no changes. The containing methods must be marked `unsafe` already (they are — class is `unsafe`); `stackalloc` requires no extra using.

**Acceptance:** `dotnet build Remex.Core` passes; grep confirms `Functions[61]` no longer appears in `JniHelper.cs`.

---

### JNI-4: AttachCurrentThread/DetachCurrentThread per callback — expensive on the 30–60 fps frame path
**File:** `Remex.Core/Native/AndroidNativeExports.cs` — `NotifyJavaFrame`, `NotifyJavaData`, `NotifyJavaConnectionState`

**Problem:** Every telemetry tick and **every video frame** attaches the calling .NET thread to the JVM and detaches it again. Attach/detach allocates a `JNIEnv`, registers the thread with ART, and tears it down — measurable per-frame overhead and lock churn during remote-desktop streaming.

**Fix:** attach lazily once per thread using `AttachCurrentThreadAsDaemon` (JavaVM fn index **7** — same signature as `AttachCurrentThread` at index 4) and detach only at thread exit via a pthread TLS destructor (step 4). Do **not** simply never-detach: .NET thread-pool threads are retired after idling, and ART aborts the process (`LOG(FATAL) "Native thread exited without calling DetachCurrentThread"` — second pass of `Thread::ThreadExitCallback`) when an attached native thread exits undetached.

**⚠️ SUPERSEDED BY ON-DEVICE TESTING (2026-06-12, SM-S948U1 / Android 17):** the pthread-TLS-destructor design below crashes under NativeAOT. Observed: SIGABRT on ART-named `Thread-N` threads ~44 s after launch (the .NET pool-thread retirement window), abort raised from inside `libRemexCore.so` — i.e. NativeAOT fail-fast, not ART. Root cause: bionic runs TLS destructors in unspecified order; NativeAOT's own thread-teardown destructor can run *before* ours, so `DetachOnThreadExit` (`[UnmanagedCallersOnly]`) re-enters managed code on a thread whose runtime state is already destroyed → fail-fast. **Implemented fix instead:** a single dedicated, process-lifetime dispatcher thread (`RemexJniDispatch` in `AndroidNativeExports.cs`) that attaches once via `AttachCurrentThreadAsDaemon` and never detaches; all three `NotifyJava*` methods enqueue onto it via a `BlockingCollection`. Pool threads never attach, so neither ART's exit check nor the destructor problem can fire, and the frame path still pays zero per-callback attach cost. Verified on device: no SIGABRT through foreground + >2 min background/resume cycles.

**Design note (reviewed 2026-06-12):** `[ThreadStatic]` is correct here and `AsyncLocal<IntPtr>` would be a bug, not an improvement — a `JNIEnv*` is only valid on the thread that attached it (JNI Invocation API), while `AsyncLocal` flows its value across `await` continuations onto *different* threads, handing thread A's env to thread B. No async boundary exists inside the usage window anyway: all three `NotifyJava*` methods are synchronous `private static void` methods, so attach and use always happen on the same thread within one stack frame. A continuation that lands on a fresh pool thread just misses the cache and re-attaches — correct and cheap.

1. Add to `JniHelper.cs`:
```csharp
public static int AttachCurrentThreadAsDaemon(IntPtr vmPtr, out IntPtr envPtr, IntPtr args)
{
    var vm = (JavaVM*)vmPtr;
    // AttachCurrentThreadAsDaemon is at index 7
    var attach = (delegate* unmanaged<IntPtr, IntPtr*, IntPtr, int>)vm->Functions[7];
    fixed (IntPtr* pEnv = &envPtr)
    {
        return attach(vmPtr, pEnv, args);
    }
}
```
2. Add to `AndroidNativeExports.cs` (class scope):
```csharp
[ThreadStatic] private static IntPtr _threadEnv;

private static bool TryGetEnv(IntPtr vm, out IntPtr env)
{
    env = _threadEnv;
    if (env != IntPtr.Zero) return true;
    if (JniHelper.AttachCurrentThreadAsDaemon(vm, out env, IntPtr.Zero) != 0) return false;
    _threadEnv = env;
    return true;
}
```
3. In each of the three `NotifyJava*` methods, replace
```csharp
if (JniHelper.AttachCurrentThread(vm, out env, IntPtr.Zero) != 0) return;
try { ... } finally { JniHelper.DetachCurrentThread(vm); }
```
with
```csharp
if (!TryGetEnv(vm, out env)) return;
... // body unchanged, but REMOVE the outer try/finally wrapper that calls DetachCurrentThread(vm)
```
Keep the inner `try/finally` blocks that delete local refs — those are still required.

4. Arm a per-thread detach destructor so retired pool threads detach cleanly instead of tripping ART's exit check. In `AndroidNativeExports.cs` (or a new `JniThreadGuard.cs` beside it):
```csharp
// bionic: pthread_key_t is int; pthread_* live in libc.so
[DllImport("libc", EntryPoint = "pthread_key_create")]
private static extern unsafe int pthread_key_create(int* key, delegate* unmanaged<IntPtr, void> destructor);
[DllImport("libc", EntryPoint = "pthread_setspecific")]
private static extern int pthread_setspecific(int key, IntPtr value);

private static int _detachKey;
private static int _detachKeyCreated; // Interlocked guard, 0 = not yet

[UnmanagedCallersOnly]
private static void DetachOnThreadExit(IntPtr _)
{
    _threadEnv = IntPtr.Zero;
    JniHelper.DetachCurrentThread(/* stored JavaVM* used by the NotifyJava* methods */);
}
```
In `TryGetEnv`, after a successful attach: create the key once (`Interlocked.CompareExchange` on `_detachKeyCreated`, destructor = `&DetachOnThreadExit`), then `pthread_setspecific(_detachKey, (IntPtr)1)` — the non-null TLS value is what makes the destructor fire at thread exit. Ordering relative to ART's own TLS destructor is safe either way: ART's first pass only warns and re-arms; ours detaches, which clears ART's key, so its fatal second pass never runs.

**Acceptance:** grep shows `DetachCurrentThread` is no longer called from the three `NotifyJava*` methods — only from `DetachOnThreadExit` (the helper stays in `JniHelper.cs`). Desktop streaming still works (manual test, §6.4), including after letting the app idle >2 minutes mid-session (pool-thread retirement window) and resuming streaming.

---

### CMP-1: All 89 `collectAsState()` call sites are lifecycle-unaware — flows keep collecting while the app is backgrounded
**Files (Android):** every screen file containing `collectAsState(` — `AppNavigation.kt`, `RemoteMouseScreen.kt`, `SettingsScreen.kt`, `DashboardScreen.kt`, `AppLauncherScreen.kt`, `FileTransferScreen.kt`, `AboutScreen.kt`, `HardwareInfoConfigActivity.kt`, `MainActivity.kt`, `PairingScreen.kt`, `PersonalizationScreen.kt`, `RemoteControlScreen.kt`, and the rest (89 sites total; 0 sites currently use the lifecycle variant)

**Problem:** `collectAsState()` collects forever while the composition exists — including when the Activity is STOPPED. Telemetry/state flows backed by the JNI bridge keep pumping JSON into UI state in the background → battery drain and wasted JNI traffic. `collectAsStateWithLifecycle()` suspends collection below STARTED.

**Fix:**
1. Add to `RemEx.Android/gradle/libs.versions.toml` `[libraries]`:
```toml
androidx-lifecycle-runtime-compose = { group = "androidx.lifecycle", name = "lifecycle-runtime-compose", version = "2.11.0-beta02" }
```
(same version as the existing `lifecycle-runtime-ktx` entry).
2. Add to `RemEx.Android/app/build.gradle.kts` dependencies block:
```kotlin
implementation(libs.androidx.lifecycle.runtime.compose)
```
3. In every `.kt` file under `RemEx.Android/app/src/main` that contains `collectAsState(`:
   - replace import `androidx.compose.runtime.collectAsState` with `androidx.lifecycle.compose.collectAsStateWithLifecycle`
   - mechanically replace `.collectAsState()` → `.collectAsStateWithLifecycle()`
   - replace `.collectAsState(initial = X)` / `.collectAsState(X)` → `.collectAsStateWithLifecycle(initialValue = X)` (note: parameter is named `initialValue`, not `initial`)
4. **Exception:** `data/SettingsManager.kt` (~line 187) has a comment describing `collectAsState(initial = null)` semantics — update the comment wording to match.
5. **Exception:** Glance widget composables (`widget/*`) do not run in a lifecycle-owner composition — if any `collectAsState` lives in Glance `provideContent` code, leave those untouched. Check each `widget/` file before converting.

**Acceptance:** `grep -rn "androidx.compose.runtime.collectAsState" RemEx.Android/app/src/main` returns 0 hits outside `widget/`; app builds; Dashboard still updates live.

---

### CMP-2: Splash screen replays on every rotation / config change
**File:** `RemEx.Android/app/src/main/java/com/clindsay94/remex/MainActivity.kt` (~line 28)

**Problem:** `var splashShown by remember { mutableStateOf(false) }` — `remember` does not survive configuration changes, so rotating the phone replays the ~1,900-line animated splash.

**Fix:**
```kotlin
import androidx.compose.runtime.saveable.rememberSaveable
...
var splashShown by rememberSaveable { mutableStateOf(false) }
```

**Acceptance:** rotate device after splash completes → no splash replay.

---

### CMP-3: Theme rebuilds `Typography` (and seeded `ColorScheme`) on every recomposition
**File:** `RemEx.Android/.../ui/theme/Theme.kt`, `RemExTheme` (~lines 336–391)

**Problem:** `MaterialTheme(typography = typographyForFontFamily(fontFamilyKey, fontScale), ...)` constructs 15 `TextStyle`s and potentially Google-Font `FontFamily` objects **on every recomposition of the theme**, and `colorSchemeFromSeed(...)` runs full HCT palette math per recomposition when `themePalette == "custom"`.

**Fix:** memoize inside `RemExTheme`:
```kotlin
val typography = remember(fontFamilyKey, fontScale) {
    typographyForFontFamily(fontFamilyKey, fontScale)
}

val seedColor = remember(themeSeedColor, themePalette, themeSeedChroma) {
    try {
        val baseColor = android.graphics.Color.parseColor(themeSeedColor)
        if (themePalette.equals("custom", ignoreCase = true)) {
            val baseHct = Hct.fromInt(baseColor)
            Color(Hct.from(baseHct.hue, themeSeedChroma.toDouble(), baseHct.tone).toInt())
        } else {
            Color(baseColor)
        }
    } catch (_: Exception) {
        Color(0xFF6750A4)
    }
}

val colorScheme = when {
    themePalette.equals("custom", ignoreCase = true) ->
        remember(seedColor, darkTheme, themeStyle, themeContrast) {
            colorSchemeFromSeed(seedColor, darkTheme, themeStyle, themeContrast.toDouble())
        }
    dynamicColor && Build.VERSION.SDK_INT >= Build.VERSION_CODES.S -> {
        val context = LocalContext.current
        if (darkTheme) dynamicDarkColorScheme(context) else dynamicLightColorScheme(context)
    }
    darkTheme -> DarkColorScheme
    else -> LightColorScheme
}

MaterialTheme(
    colorScheme = colorScheme,
    typography = typography,
    shapes = remexShapes,
    motionScheme = MotionScheme.expressive(),
    content = content
)
```
Note: `remember` inside a `when` branch is safe here because the branch condition (`themePalette`) is itself a remember key — but to be conservative, hoist the custom-scheme `remember` above the `when` if the Compose compiler complains.

**Acceptance:** app builds; changing font/seed color in Personalization still applies live.

---

### JNI-5: Native error path emits hand-concatenated JSON with unescaped exception text
**File:** `Remex.Core/Native/AndroidNativeExports.cs`, `Export` (~line 1014) and `SerializeTelemetryFailure` (~line 1035)

**Problem:** `"{\"success\":false,...,\"error\":\"" + ex.Message + "\"}"` — any quote/backslash/newline in `ex.Message` (socket exceptions frequently quote host strings) produces invalid JSON, breaking Kotlin-side parsing of the failure response exactly when error reporting matters.

**Fix:** use the source-generated serializer already in the same file:
```csharp
private static IntPtr Export(IntPtr env, Func<string> action)
{
    try
    {
        return JniHelper.CreateJString(env, action());
    }
    catch (Exception ex)
    {
        return JniHelper.CreateJString(env,
            SerializeOperationFailure("Unhandled native export failure.", ex.Message));
    }
}

private static string SerializeTelemetryFailure(string message, string? error = null)
    => SerializeOperationFailure(message, error);
```

**Acceptance:** `dotnet build Remex.Core` passes; no string-concatenated `\"success\":false` JSON literals remain in `AndroidNativeExports.cs`.

---

## 3. P2 — Medium (correctness debt, dead code, conventions)

### PG-1: ProGuard rules reference classes/methods that don't exist; cargo-cult keeps
**File:** `RemEx.Android/app/proguard-rules.pro` — apply all four:

1. `-keepclassmembers class com.clindsay94.remex.security.PairingViewModel { *; }` — `PairingViewModel` actually lives in `com.clindsay94.remex.ui.screens` (declared in `PairingScreen.kt`); the rule is a silent no-op and ViewModels need no keep rule. **Delete the line.**
2. The block
   ```
   -keepclassmembers class com.clindsay94.remex.RemexCoreClient$RemexCallback {
       void onDesktopStreamDescriptor(java.lang.String);
       void onFileTransferProgress(java.lang.String);
       void onFileTransferComplete(java.lang.String);
   }
   ```
   names two methods that **do not exist** on the interface (the real method is `onFileTransferMessage`). All 13 interface methods are resolved at runtime via JNI `GetMethodID` and must survive R8. Replace the block with:
   ```
   # All RemexCallback methods are resolved from native code via JNI GetMethodID.
   -keepclassmembers class com.clindsay94.remex.RemexCoreClient$RemexCallback {
       <methods>;
   }
   ```
3. `-keep class com.nsec.** { *; }` — NSec is a .NET library; no such Java package exists. **Delete.**
4. `-keep class java.lang.** { *; }`, `-keep class java.net.** { *; }`, `-keep class javax.net.** { *; }` — platform classes are never in R8's input. **Delete all three.**

**Acceptance:** `gradlew :app:assembleRelease` succeeds; pairing + file transfer work in a release build (§6.4).

### KT-1: Dead code removal (verified candidates only)
1. **`FreeMemory` JNI export (dead on both sides):** delete `@JvmStatic internal external fun FreeMemory(pointer: Long)` from `RemexCoreClient.kt` (~line 319) AND the `Java_com_clindsay94_remex_RemexCoreClient_FreeMemory` export method in `AndroidNativeExports.cs`. It is never called from Kotlin (all native returns are jstrings whose refs JNI manages).
2. `ui/components/HapticModifier.kt` → `triggerHaptic` (only reference is its own declaration). Delete the function; delete the file if it becomes empty.
3. `ui/screens/RemoteMouseScreen.kt` → `FloatingMouseIsland` composable (only reference is its own declaration). Delete it.
4. `RemEx.Android/gradle/libs.versions.toml`: after VC-1 below, delete every `[versions]` key with zero remaining `version.ref` references (currently: `glance`, `junitVersion`, `espressoCore`, `lifecycleVersion`, `materialIconsExtended`, `androidxGraphicsPath`, `androidxGraphicsShapes`, `navigationCompose`, `androidxDatastorePreferences`; `activityCompose` becomes USED by VC-1 — keep it). Verify each with grep before deleting.

**Guardrail reminder:** do not touch `tile/`, `widget/`, or `service/` classes (manifest-registered, OS-instantiated).

**Acceptance:** `grep -rn "FreeMemory\|triggerHaptic\|FloatingMouseIsland" RemEx.Android/app/src Remex.Core` → 0 hits; both builds pass.

### VC-1: Version-catalog integrity
**File:** `RemEx.Android/gradle/libs.versions.toml`

1. **Bug:** `androidx-activity-compose = { ..., version.ref = "material" }` — activity-compose's version is accidentally tied to the Material version ref (works only because both happen to be `1.13.0` today; bumping Material would silently change activity-compose). Change to `version.ref = "activityCompose"` (key exists, value `1.13.0`).
2. **Drift:** `[versions] material = "1.13.0"` (with a justifying comment) vs the library entry's inline override `version = "1.14.0"`. Keep **1.14.0** (currently shipping): set the version key to `1.14.0`, change the library to `version.ref = "material"`, update the comment. The restricted `color.utilities` classes used by `Theme.kt`/`DynamicSchemes.kt` must still resolve — the build fails fast if not.
3. **BOM bypass:** `androidx-compose-bom = "2026.05.01"` is declared but every Compose artifact pins an explicit version (`1.12.0-alpha03`, material3 `1.5.0-alpha20`), making the BOM dead weight. **Delete the BOM library entry** and its `implementation(platform(libs.androidx.compose.bom))` / `androidTestImplementation(platform(...))` usages in `app/build.gradle.kts`. ⚠️ `androidx-compose-ui-test-junit4` has **no version** and relies on the BOM — give it `version = "1.12.0-alpha03"` when removing the BOM. Do **not** downgrade any pinned alpha (expressive `MaterialShapes`/`MotionScheme` APIs depend on them).

**Coherency note (reviewed 2026-06-12):** deleting the BOM does not weaken version coherency, because the BOM currently governs **zero** runtime artifacts — every Compose dependency pins an explicit version, and explicit versions override a `platform()` BOM. The only artifact actually resolved by the BOM is `androidx-compose-ui-test-junit4`, which gets an explicit pin as part of this change. The alternative (adopt the BOM, drop the pins) would silently *downgrade* the deliberately-pinned alphas — BOM `2026.05.01` maps to stable lines — and break the expressive `MaterialShapes`/`MotionScheme` APIs. After this change, coherency across the pinned alphas is owned by `libs.versions.toml` review, exactly as it is today.

**Acceptance:** gradle sync passes; `gradlew :app:dependencies --configuration releaseRuntimeClasspath` still shows material3 `1.5.0-alpha20`.

### CMP-4: `H264StreamDecoder` — `decoder!!` races with `release()`
**File:** `ui/screens/H264StreamDecoder.kt`, `decodeFrame` (~lines 51–79)

**Problem:** `decodeFrame` null-checks `decoder` then dereferences `decoder!!` three times. `release()` (called from another thread on stream stop) nulls `decoder` mid-frame → NPE (swallowed by the broad catch, but produces an error-spam burst and dropped frames at every stream stop).

**Fix:** capture once, exactly like `drainOutputBuffers` already does:
```kotlin
fun decodeFrame(bytes: ByteArray) {
    val decoder = this.decoder ?: return
    if (!isConfigured) return
    try {
        val inputBufferIndex = decoder.dequeueInputBuffer(10000) // 10ms timeout
        ...
```
Replace each `decoder!!.` in the body with the captured local `decoder.`.

**Acceptance:** no `!!` remains in `H264StreamDecoder.kt`.

### CMP-5: `Thread.sleep(400)` in pairing retry loop
**File:** `ui/screens/PairingScreen.kt`, `tryFetchPinFromHost` (~line 158)

**Fix:** make the function `suspend` and replace `try { Thread.sleep(400) } catch (_: InterruptedException) {}` with `kotlinx.coroutines.delay(400)`. The only caller (`startPairing`, ~line 128) already invokes it inside `withContext(Dispatchers.IO) { ... }`, so adding `suspend` compiles cleanly.

### CS-1: `ConfigureAwait` violations in `Remex.Host` (repo convention forbids it)
**Files & counts:** `LinuxPortalInputInjector.cs` ×12, `LinuxPortalRemoteDesktopSessionService.cs` ×15, `PortalDbusHelper.cs` ×3, `PortalRecoveryHelper.cs` ×4, `HostDoctor.cs` ×3, `Program.cs` ×1

**Fix:** delete every `.ConfigureAwait(false)` suffix — mechanical removal, the awaited expression stays. See `docs/ASYNC_GUIDELINES.md`.

**Acceptance:** `grep -rn "ConfigureAwait" Remex.Host` → 0 hits; `dotnet build` + `dotnet test Remex.Host.Tests` pass.

### CS-2: `GetService<T>()` → `GetRequiredService<T>()` (null-safety convention)
**Files:** `Remex.Client/.../ConnectionViewModel.cs` ×5, `Remex.Client/App.axaml.cs` ×3, `Remex.Client/MainWindow.axaml.cs` ×3, `Remex.Host/HostBootstrapper.cs` ×1

**Fix:** replace `GetService<T>()` with `GetRequiredService<T>()` **only where the result is dereferenced unconditionally or null-forgiven**. Where code legitimately branches on null (optional service), leave it and add `// optional service`. Inspect each of the 12 sites individually.

**Acceptance:** `dotnet build Remex.sln` and `dotnet test Remex.sln` pass.

### CS-3: `IpcClientCommandService` blocks with `.Wait()` ×10
**File:** `Remex.Client/Services/IpcClientCommandService.cs` (locate by name)

**Problem:** ten sync-over-async `.Wait()` calls; any reached from the Avalonia UI thread freeze rendering for a named-pipe round trip.

**Fix:** convert the containing methods to `async Task` end-to-end and `await`. Update callers up the chain (Avalonia command handlers can be async). **Riskiest item in P2** — if the caller graph fans out beyond ~6 call sites, STOP, leave the code unchanged, and record the call graph in the final report instead.

---

## 4. P3 — Low (polish / hardening / docs)

### THM-1: Restricted-API dependency `com.google.android.material.color.utilities.*`
`Theme.kt` + `DynamicSchemes.kt` import `Hct`, `SchemeExpressive`, `MaterialDynamicColors`, etc. — `@RestrictTo(LIBRARY_GROUP)` internals of the Material library that can break on any `material` bump (see VC-1 drift). **Action:** add a comment block at the top of both files documenting the pinned `material` version and breakage risk, plus `@SuppressLint("RestrictedApi")` where lint flags it. (Vendoring color-utilities is the long-term fix; out of scope.)

### CMP-6: Redundant per-frame copy
`RemexClientManager.kt`, `onFrameReceived` (~line 368): `_frames.tryEmit(frame.copyOf())`. The JNI side allocates a fresh `byte[]` per frame (`NewByteArray` in `NotifyJavaFrame`), so `copyOf()` doubles per-frame allocation at up to 60 fps. Replace with `_frames.tryEmit(frame)` and add: `// JNI delivers a freshly allocated array per frame; no defensive copy needed.`

### CMP-7: `SplashScreen.kt` is 1,908 lines with 2 infinite transitions
Not a bug; a maintainability hazard. **Action (document only):** add a `// TODO(refactor): split into SplashScreen + OnboardingFlow + drawing helpers` header. Do not refactor in this pass.

### KT-2: Remaining `!!` cleanups (mechanical, guarded-but-fragile)
- `MainActivity.kt` (~line 31): replace `if (personalization != null) { val prefs = personalization!! ... }` with `val prefs = personalization; if (prefs != null) { ... }` (smart-cast on local).
- `PersonalizationScreen.kt` (~line 78) `settings = settingsState!!` — same local-capture pattern.
- `FileTransferScreen.kt` (~lines 194/201/216) `renameTarget!!` / `contextMenuEntry!!` — capture into a local `val` at the top of the dialog/closure.
- `RemoteDesktopViewModel.kt` (~line 611) `displaysArray!!` — restructure to `val arr = displaysArray ?: return` (or `?: continue` host loop equivalent) before the dereference.

### THM-2: Consider `MaterialExpressiveTheme`
`RemExTheme` uses `MaterialTheme(motionScheme = MotionScheme.expressive())` — expressive **motion** but not the expressive baseline defaults. Since the app supplies its own colorScheme/typography/shapes, `MaterialExpressiveTheme(...)` is a near-drop-in. **Action:** attempt the swap; if it does not compile cleanly against material3 `1.5.0-alpha20` with identical arguments, revert and skip (current setup is acceptable).

### DOC-1: Doc drift
`CLAUDE.md` says "Android SDK API Level 36 platform required"; gradle uses `compileSdk = 37`, `targetSdk = 37`, `buildToolsVersion = "37.0.0"`. Update `CLAUDE.md` to 37 (do not downgrade gradle).

### SEC-1 (accepted-risk — document only, no code change)
- `PairingScreen.httpFetchPin` trust-all TLS: gated behind `TransportTrust.canAutoFetchPin` (loopback/WireGuard) and documented in-code. No change.
- `PinnedHostStore.aead()` `runBlocking` inside `synchronized` (~lines 60–64): exceptional Keystore-corruption recovery path, documented. Verify all callers of `aead()` are `suspend` functions running off the main thread (`setPin`/`getPin` are); if so, no change.

---

## 5. Explicit non-findings (do NOT "fix" these)

- The 102 `androidx.compose.material.*` imports are all `material.icons.*` — **not** Material 2 component usage. The app is M3-pure. Leave them.
- `MotionScheme.expressive()` is correctly wired in the theme; expressive usage (`ExperimentalMaterial3ExpressiveApi` opt-ins ×28, `MaterialShapes` morphing card shapes with an LRU `Morph` cache, `motionScheme`-driven animations ×34) is legitimate and intentional.
- ProGuard keep rules for `RemexCoreClient` and its native methods are correct and required (JNI linkage).
- Tile services with `exported="true"` and Glance widget receivers: required platform contracts.
- `AndroidNativeExports` pairing methods using `GetAwaiter().GetResult()` (lines ~417/438/501): bounded by `CancellationTokenSource` timeouts and always invoked from `Dispatchers.IO` in Kotlin — acceptable for JNI entry points.
- `foregroundServiceType="connectedDevice"` + `FOREGROUND_SERVICE_CONNECTED_DEVICE` permission: correctly declared.

---

## 6. Verification protocol (run after each phase)

```powershell
# 6.1 .NET — build + full test suite (JNI-*, CS-*)
dotnet build Remex.sln -c Release
dotnet test Remex.sln

# 6.2 Android — compile + lint (CMP-*, PG-*, VC-*, KT-*)
cd RemEx.Android
.\gradlew :app:assembleDebug :app:lintDebug
cd ..

# 6.3 Android — hardened release build (BLD-*, PG-1 validation)
.\scripts\android-fresh.ps1 -Configuration Release

# 6.4 Manual smoke (requires a host on the LAN) — report SKIPPED if no device:
#   pair device → dashboard telemetry updates → start remote desktop stream
#   → rotate device (no splash replay, CMP-2) → background app 1 min →
#   foreground (stream/telemetry resume, CMP-1) → release-build pairing + file transfer (PG-1)
```

## 7. Final report format

For each issue ID: `FIXED | SKIPPED (reason) | BLOCKED (error text)`, list of files changed, and the verbatim output tail of 6.1–6.3. Do not commit.

---

# Addendum A — Splash Screen Animation Audit (SPL-1 … SPL-7)

**File under audit:** `RemEx.Android/app/src/main/java/com/clindsay94/remex/ui/screens/SplashScreen.kt` (1,908 lines, single `SplashScreen` composable + `drawStylizedRLogo` DrawScope helper). Two styles exist and **both are reachable** — `"RemexCommand"` (default) and `"CosmicZoom"`, selectable in Personalization (`PersonalizationScreen.kt` ~line 491, persisted via `SettingsManager.SPLASH_STYLE_KEY`). **Do not delete the CosmicZoom branch.**

These issues supersede the "document-only" stance of CMP-7 for the specific items below (CMP-7's full-file split remains out of scope).

---

### SPL-1 (P1): Hand-rolled `delay(16L)` frame loop — fixed timestep, cross-thread mutation, choreography desync
**Location:** `LaunchedEffect(Unit)` at ~line 289 → `scope.launch(Dispatchers.Default) { while (!isSkipping) { ... delay(16L) } }` (lines ~289–380)

**Problems (all in one loop):**
1. **Fixed `dt = 0.016f`** regardless of actual frame time: on a loaded device frames take longer but the simulation still advances 16 ms per iteration → the whole animation runs *slower than wall-clock*. Meanwhile the finish is wall-clock (`delay(3000L)` for CosmicZoom, chained `delay(800)`/tween durations for RemexCommand) → the `elapsed`-driven beats (lightning strike at `elapsed >= 1.8f`, its haptic at line ~283, the shockwave at line ~538) **desync from the finish/fade choreography** under load.
2. **Capped at ~60 fps** on 120 Hz displays (`delay(16L)`), so the splash animates at half the display rate.
3. **Data race:** `Particle`/`FloatingShape`/`StreamParticle` objects have plain `var` fields mutated on `Dispatchers.Default` (lines ~295–369) while the `Canvas` lambda reads them on the UI thread during draw → torn reads (particles visibly jumping/flickering).
4. The loop is launched via the outer `scope` (from `rememberCoroutineScope()`) *inside* a `LaunchedEffect` — it survives only because both cancel at the same disposal point; it's structurally confusing and fragile.

**Fix:** replace the `scope.launch(Dispatchers.Default) { while ... delay(16L) }` block with a main-thread frame-clock loop in the same `LaunchedEffect`, computing real `dt`:

```kotlin
// Frame-clock-driven update: real dt, main thread (no races), native display rate.
launch {
    var lastNanos = 0L
    while (!isSkipping && !completed) {
        withFrameNanos { now ->
            val dt = if (lastNanos == 0L) 0.016f
                     else ((now - lastNanos) / 1_000_000_000f).coerceAtMost(0.05f)
            lastNanos = now
            // ── existing particle/shape/stream update code goes here, verbatim,
            //    but with `dt` replacing the hardcoded 0.016f ──
            elapsed += dt
            particleFrame++
        }
    }
}
```
Mechanical steps for the agent:
1. Inside the `LaunchedEffect(Unit)` at ~line 289, replace `scope.launch(kotlinx.coroutines.Dispatchers.Default) {` with `launch {` (the `LaunchedEffect` scope; add `import kotlinx.coroutines.launch` if missing).
2. Replace the `while (!isSkipping) { ... delay(16L) }` skeleton with the `withFrameNanos` pattern above, moving the existing particle-update bodies inside the `withFrameNanos` lambda unchanged except: every occurrence of the literal `0.016f`/`dt = 0.016f` inside the loop uses the computed `dt`, and delete the `withContext(Dispatchers.Main) { particleFrame++; elapsed += dt }` hop (no longer needed — already on main).
3. The per-frame update math is cheap (≤ 55 simple objects); main-thread execution is fine.
4. Note: physics constants like `p.speed`, `sp.speed` were tuned for dt=0.016; the code already multiplies by `dt * 60f` in most places (e.g. line ~303) — verify each velocity application is dt-scaled; where a bare `sp.t += sp.speed` exists (line ~367), change to `sp.t += sp.speed * (dt * 60f)` to preserve tuned speed.

**Acceptance:** splash plays at the same perceived speed; no `Dispatchers.Default` reference remains in `SplashScreen.kt`; `grep "delay(16" SplashScreen.kt` → 0 hits.

---

### SPL-2 (P1): 13 `textMeasurer.measure()` calls per recomposition — and the splash recomposes every frame
**Location:** lines ~152–208 (`remMeasured` … `commandCenterCosmicMeasured`), `rememberTextMeasurer()` at line ~152

**Problem:** the 13 `textMeasurer.measure(...)` results and 6 `TextStyle` objects are plain `val`s in the composable body, so they re-execute on **every recomposition** — and because `elapsed`/`particleFrame` are state written ~60×/s, the splash recomposes every frame. `rememberTextMeasurer()`'s internal layout cache holds **8** entries by default; 13 distinct measurements thrash the cache, forcing real text re-layout work every frame for the duration of the splash.

**Fix (two lines of leverage):**
1. `val textMeasurer = rememberTextMeasurer(cacheSize = 16)`
2. Wrap each style + measurement in `remember`. The styles depend on theme colors, so key on them:
```kotlin
val remStyle = remember {
    TextStyle(color = Color.White, fontSize = 54.sp, fontWeight = FontWeight.Black,
              fontFamily = FontFamily.Monospace, letterSpacing = 4.sp)
}
val exStyle = remember(primary) { remStyle.copy(color = primary) }
...
val remMeasured = remember(remStyle) { textMeasurer.measure("REM", remStyle) }
val exMeasured = remember(exStyle) { textMeasurer.measure("EX", exStyle) }
// ... same pattern for all 13 measure() calls, keying each on the style it uses
```

**Acceptance:** every `textMeasurer.measure(` call site in `SplashScreen.kt` is inside a `remember(...)` block.

---

### SPL-3 (P2): `Modifier.alpha(skipAlpha.value)` reads animated state during composition
**Location:** root `Box` modifier, line ~463

**Problem:** `.alpha(skipAlpha.value)` makes the *composition* a reader of `skipAlpha` — during the 200 ms skip fade, every animation frame recomposes the entire splash tree. The zoom block just below (line ~468) already does this correctly with deferred reads inside `graphicsLayer { }`.

**Fix:** replace `.alpha(skipAlpha.value)` with:
```kotlin
.graphicsLayer { alpha = skipAlpha.value }
```

**Acceptance:** `grep "\.alpha(skipAlpha" SplashScreen.kt` → 0 hits.

---

### SPL-4 (P2): `onFinished()` can fire twice (skip-vs-completion race)
**Location:** `skipSplash()` (~line 271), CosmicZoom finish (~line 386), RemexCommand finish (~line 444)

**Problem:** the choreography checks `if (!isSkipping)` only at phase boundaries. A tap landing **after** the last check but before/during the final `fadeOverlay.animateTo` runs both paths: the completion path calls `onFinished()` (line ~444) and `skipSplash`'s coroutine calls `onFinished()` again (line ~277). Depending on what `onFinished` does in `AppNavigation` (~line 734), that can double-fire navigation/state writes.

**Fix:** make finishing idempotent and let skip respect completion:
```kotlin
fun finishOnce() {
    if (completed) return
    completed = true
    onFinished()
}

fun skipSplash() {
    if (isSkipping || completed) return
    isSkipping = true
    view.performHapticFeedback(HapticFeedbackConstants.CONFIRM)
    scope.launch {
        skipAlpha.animateTo(0f, tween(200, easing = FastOutSlowInEasing))
        finishOnce()
    }
}
```
Then replace both choreography endpoints (`completed = true; onFinished()` at ~386 and ~443–444) with `finishOnce()`.

**Acceptance:** exactly one code path can invoke `onFinished()` per splash lifetime; `grep -c "onFinished()" SplashScreen.kt` shows it called only inside `finishOnce()` (plus the preview lambda).

---

### SPL-5 (P2): Hero text and effect sizes don't adapt to screen size or font scale
**Locations:** `remStyle` 54 sp + 4 sp letterSpacing (~line 156), `cosmicTitleStyle` 38 sp (~line 192), monitor beam `thickness = 90.dp.toPx()` (~line 911), `winSize = 64.dp` (~935), `tuxSize = 56.dp` (~1021), `andSize = 72.dp` (~1095), bloom `40.dp + t*360.dp` (~651)

**Problem (two distinct defects):**
1. **Font scale:** the splash text is *decorative canvas art*, but `.sp` units respect the user's system font scale. At 1.5–2× font scale, `"EXECUTION"` at 54 sp Black monospace + 4 sp tracking measures wider than a 360 dp screen → the drawn title overflows / collides with the monitor glyph. Decorative drawn text should scale with **density only**, not user font preference.
2. **Screen adaptivity:** hero element sizes are fixed dp while positions are fractional (`monCx = w*0.22f`). On a small phone the 90 dp beam + 64/56/72 dp glyphs crowd; on a tablet they look tiny relative to the fractional layout. The file already has the right pattern in one place: `maxRad = min(width, height) * 0.45f` (~line 513).

**Fix:**
1. In `SplashScreen`, build the canvas text styles with density-based (non-font-scaled) sizes:
```kotlin
val density = LocalDensity.current
val remFontSize = with(density) { 54.dp.toSp() }   // density-scaled, fontScale-independent
val remTracking = with(density) { 4.dp.toSp() }
val remStyle = remember(remFontSize) {
    TextStyle(color = Color.White, fontSize = remFontSize, fontWeight = FontWeight.Black,
              fontFamily = FontFamily.Monospace, letterSpacing = remTracking)
}
```
Apply the same `X.dp.toSp()` conversion to all six canvas `TextStyle`s (54 sp, 38 sp, 24 sp, 14 sp, 11 sp values and their letterSpacing). **Do not** convert styles used by real `Text(...)` composables elsewhere in the app — this applies only to `textMeasurer.measure`-drawn text in `SplashScreen.kt`.
2. Inside the Canvas draw code, derive hero sizes from the viewport instead of fixed dp, preserving current proportions at a 411 dp-wide reference: replace
   - `val thickness = 90.dp.toPx()` → `val thickness = kotlin.math.min(size.width, size.height) * 0.24f`
   - `val winSize = 64.dp.toPx()` → `* 0.17f`
   - `val tuxSize = 56.dp.toPx()` → `* 0.15f`
   - `val andSize = 72.dp.toPx()` → `* 0.19f`
   (Each factor ≈ old-dp ÷ 375; round to 2 decimals.) Leave small stroke widths (1–4 dp) and the bloom radii as dp — strokes *should* be density-constant, and the bloom intentionally over-covers the screen.

**Acceptance:** with system font scale set to 2.0, the splash title stays within screen bounds (manual check or screenshot test); `gradlew :app:assembleDebug` passes.

---

### SPL-6 (P3): Motion-spec consistency with M3 Expressive (optional polish)
The scan-line springs (`DampingRatioMediumBouncy` + `StiffnessLow`, ~line 401) with 180 ms stagger are a good expressive choice — keep. The phase tweens (2000 ms `FastOutLinearInEasing` radar sweeps, 700 ms `FastOutSlowInEasing` zoom pull-in, 400 ms glow/fade) are hand-tuned cinematic timing — acceptable as-is. **Optional:** the Phase-4 zoom pull-in (~lines 425–434) and connection glow (~line 417) may adopt theme motion tokens for app-wide consistency:
```kotlin
val motion = MaterialTheme.motionScheme   // capture in composition, pass into the effect
...
zoomScale.animateTo(6f, motion.slowSpatialSpec())
connectionGlow.animateTo(1f, motion.defaultEffectsSpec())
```
Only do this if the visual result is acceptable (springs overshoot; `zoomScale` overshooting past 6f is fine since it's a zoom-out cover). If in doubt, **skip — this is taste, not correctness.** The CosmicZoom hand-rolled impact "punch" (sin-based elastic, ~lines 584–587) and per-frame reseeded camera shake (~line 579) are deliberate effects — leave them.

---

### SPL-7 (P3): Stale theme colors captured in unkeyed `remember` (document only)
`floatingShapes` (~line 226) captures `primary/secondary/tertiary` in `remember { }` with no keys, and constructs 18 `Morph` objects bypassing Theme.kt's `morphCache`. If the theme changes mid-splash (dynamic color update), shapes keep stale colors for the splash's few seconds — cosmetically harmless, one-time cost acceptable. **Action:** add keys `remember(primary, secondary, tertiary) { ... }` if touching the file anyway (SPL-1/2 will); otherwise leave.

---

### Addendum non-findings (do NOT "fix")
- `"CosmicZoom"` is user-selectable (Personalization → splash style) — both style branches are live code.
- The deferred state reads inside `graphicsLayer { }` for the zoom transform (~lines 468–483) are already the correct pattern.
- Tap-to-skip with `HapticFeedbackConstants.CONFIRM` and the strike-synced `LONG_PRESS` haptic are intentional UX.
- `letterSpacing` on monospace display text, the chromatic-aberration RGB-split rings, and the full-screen flash are deliberate art direction.

### Addendum verification
SPL-1/2/3/4 are covered by §6.2 builds plus this manual pass: play both splash styles (switch in Personalization), tap-to-skip mid-way and at the very end (no double navigation), rotate during splash (combined with CMP-2: no replay), and set system font scale to 2.0 (SPL-5: title stays on-screen).

---

# Addendum B — Desktop Client Audit: Avalonia UI, Localization, FAQ/Tutorial (AVA / LOC / FAQ / E issues)

**Scope:** `Remex.Client` (shared Avalonia UI), 26 `.axaml` views, 9 `.resx` localization files (en + es, fr, hi, id, pl, pt-BR, tr, uk), themes in `Remex.Client/Themes/`. Same ground rules as §0. Architecture context the agent needs: `App.axaml` merges `BaseDarkGlass.axaml` at startup; `ThemeService.ApplyBaseThemeInternal` (`Services/ThemeService.cs` ~line 145) **removes all** merged theme dictionaries and inserts only the selected theme file — so every theme file must define the complete key vocabulary (no fallback to BaseDarkGlass).

---

## B1. P0 — Broken resource references (runtime failures)

### AVA-1: `RemoteDesktopView` references a converter that does not exist anywhere
**File:** `Remex.Client/Views/RemoteDesktopView.axaml` lines ~263 and ~269:
```xml
Opacity="{Binding IsRemoteCursorOverlayVisible, Converter={StaticResource BoolToDoubleConverter}, ConverterParameter='1.0|0.0'}"
```
`BoolToDoubleConverter` is defined in **no** `.axaml` and **no** `.cs` file in the repo. A `StaticResource` that fails to resolve throws at XAML load — this is the remote-cursor overlay added in commit `1493d29`, so the remote-desktop view is at risk of failing to construct (or did at minimum never bind these opacities).

**Fix:**
1. Create `Remex.Client/Converters/BoolToDoubleConverter.cs`:
```csharp
using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Remex.Client.Converters;

/// <summary>Converts bool → double using a "trueValue|falseValue" ConverterParameter (e.g. '1.0|0.0').</summary>
public sealed class BoolToDoubleConverter : IValueConverter
{
    public static readonly BoolToDoubleConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var parts = (parameter as string)?.Split('|');
        double t = 1.0, f = 0.0;
        if (parts is { Length: 2 })
        {
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out t);
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out f);
        }
        return value is true ? t : f;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
```
2. Register it in `RemoteDesktopView.axaml`'s root resources (or `App.axaml`'s `Application.Resources`):
```xml
<conv:BoolToDoubleConverter x:Key="BoolToDoubleConverter"/>
```
(match the file's existing `xmlns` alias for `Remex.Client.Converters`).

**Acceptance:** `dotnet build Remex.Client` passes; opening the Remote Desktop view no longer throws; the cursor overlay opacity toggles with `IsRemoteCursorOverlayVisible`.

### AVA-2: `DiagnosticLogsView` references undefined `TabControlTheme`
**File:** `Remex.Client/Views/DiagnosticLogsView.axaml` line ~34: `<TabControl Theme="{StaticResource TabControlTheme}">` — no `TabControlTheme` ControlTheme exists anywhere. Same StaticResource-throws-at-load failure mode as AVA-1, on the Diagnostic Logs view.

**Fix:** delete the attribute → `<TabControl>`. (Do not author a custom ControlTheme in this pass.)

**Acceptance:** Diagnostic Logs view opens; tabs render with default Fluent styling.

---

## B2. P1 — Theming and silent visual breakage

### AVA-3: Switching to CyberNOC / Monolith / SolarFlare drops 6 resource keys → corner radii collapse to 0
Because `ThemeService` **replaces** the theme dictionary (see scope note), every key a view references must exist in *every* theme file. `BaseDarkGlass.axaml` defines 6 keys the other three themes lack:
`SystemControlBackgroundListLowBrush`, `CornerRadiusSmall`, `CornerRadiusMedium`, `CornerRadiusLarge`, `CornerRadiusExtraLarge`, `RemoteCardCornerRadius`.
Views reference them via `DynamicResource` (e.g. `ShellView.axaml` ~line 105 `CornerRadius="{DynamicResource CornerRadiusLarge}"`), so under any non-default theme those resolve to nothing → corner radius 0, list-background brush missing.

**Fix:** copy this block verbatim from `BaseDarkGlass.axaml` into `CyberNOC.axaml`, `Monolith.axaml`, and `SolarFlare.axaml` (before the closing `</ResourceDictionary>`):
```xml
<!-- Compatibility aliases for WinUI-style resource names -->
<SolidColorBrush x:Key="SystemControlBackgroundListLowBrush" Color="{DynamicResource CardBackground}"/>

<!-- UI Geometry (Material 3 Expressive Tokens) -->
<CornerRadius x:Key="CornerRadiusSmall">8</CornerRadius>
<CornerRadius x:Key="CornerRadiusMedium">16</CornerRadius>
<CornerRadius x:Key="CornerRadiusLarge">28</CornerRadius>
<CornerRadius x:Key="CornerRadiusExtraLarge">36</CornerRadius>
<CornerRadius x:Key="RemoteCardCornerRadius">12</CornerRadius>
```
(Each theme already defines its own `CardBackground` color, so the brush alias adapts automatically.)

**Acceptance:** run the client, switch through all four themes in Customization — rounded corners persist in the nav rail and cards under every theme.

### AVA-4: `ShellView` style references nonexistent `AccentPrimaryHoverBrush`
**File:** `Remex.Client/Views/ShellView.axaml` line ~84 — a style setter uses `{DynamicResource AccentPrimaryHoverBrush}`; the defined key in every theme is `AccentHoverBrush`. The hover state silently does nothing.
**Fix:** change `AccentPrimaryHoverBrush` → `AccentHoverBrush`.

### AVA-5: `PairingDialog` error text uses nonexistent `ErrorTextBrush`
**File:** `Remex.Client/Views/PairingDialog.axaml` line ~39 — `Foreground="{DynamicResource ErrorTextBrush}"`. Every theme defines `SystemErrorBrush`.
**Fix:** change `ErrorTextBrush` → `SystemErrorBrush`.

### AVA-6: Dead theme file `Themes/Material3Android.axaml`
Defines 14 `M3*` keys (`M3CardCornerRadius`, `M3Elevation1`, …) with **zero consumers** anywhere, is absent from the `AppTheme` enum (`AppTheme.cs`: BaseDarkGlass, CyberNOC, SolarFlare, Monolith, Dynamic), and lacks the ~38 keys live views require — selecting it would break the whole UI if it were ever reachable. **Fix:** delete `Remex.Client/Themes/Material3Android.axaml`. Verify nothing references it: `grep -rn "Material3Android" Remex.Client Remex.Client.Desktop` must return 0 hits afterward (the `.csproj` includes themes by wildcard, so no project-file edit is needed).

**Acceptance (AVA-4/5/6):** `dotnet build Remex.sln` passes; pairing-dialog error text renders red/rose; grep confirms `AccentPrimaryHoverBrush`, `ErrorTextBrush`, and `Material3Android` each have 0 remaining references.

---

## B3. P1 — Localization

### LOC-1: The close-to-tray feature (commit `b352a24`) shipped English-only — 6 keys missing from all 8 translations
Base `Strings.resx` has 804 keys; **all eight** language files have exactly these 6 missing. Add the following `<data>` nodes to each language file (keep the standard resx node shape: `<data name="..." xml:space="preserve"><value>...</value></data>`). Translations to use:

| Key | es | fr |
|---|---|---|
| Settings_CloseToTray | Minimizar a la bandeja al cerrar | Réduire dans la zone de notification à la fermeture |
| Settings_CloseToTrayDesc | Si está activado, el botón X oculta RemEx en la bandeja del sistema y lo mantiene en ejecución. Si está desactivado, el botón X cierra la aplicación. | Si activé, le bouton X masque RemEx dans la zone de notification et le laisse en cours d'exécution. Si désactivé, le bouton X quitte l'application. |
| Settings_CloseToTrayTooltip | Elige si al cerrar la ventana RemEx se minimiza a la bandeja o se cierra por completo. | Choisissez si la fermeture de la fenêtre réduit RemEx dans la zone de notification ou le quitte complètement. |
| Settings_ExitApp | Salir de RemEx | Quitter RemEx |
| Settings_ExitAppDesc | Cerrar completamente la aplicación y detener su proceso en segundo plano. | Fermer complètement l'application et arrêter son processus en arrière-plan. |
| Settings_ExitAppButton | Salir | Quitter |

| Key | hi | id |
|---|---|---|
| Settings_CloseToTray | बंद करने पर ट्रे में छोटा करें | Minimalkan ke tray saat ditutup |
| Settings_CloseToTrayDesc | सक्षम होने पर, X बटन RemEx को सिस्टम ट्रे में छिपा देता है और इसे चालू रखता है। अक्षम होने पर, X बटन ऐप को बंद कर देता है। | Jika diaktifkan, tombol X menyembunyikan RemEx ke system tray dan tetap menjalankannya. Jika dinonaktifkan, tombol X menutup aplikasi. |
| Settings_CloseToTrayTooltip | चुनें कि विंडो बंद करने पर RemEx ट्रे में छोटा हो या पूरी तरह बंद हो जाए। | Pilih apakah menutup jendela meminimalkan RemEx ke tray atau menutupnya sepenuhnya. |
| Settings_ExitApp | RemEx से बाहर निकलें | Keluar dari RemEx |
| Settings_ExitAppDesc | एप्लिकेशन को पूरी तरह बंद करें और इसकी पृष्ठभूमि प्रक्रिया रोकें। | Tutup aplikasi sepenuhnya dan hentikan proses latar belakangnya. |
| Settings_ExitAppButton | बाहर निकलें | Keluar |

| Key | pl | pt-BR |
|---|---|---|
| Settings_CloseToTray | Minimalizuj do zasobnika przy zamykaniu | Minimizar para a bandeja ao fechar |
| Settings_CloseToTrayDesc | Gdy włączone, przycisk X ukrywa RemEx w zasobniku systemowym i pozostawia go uruchomionego. Gdy wyłączone, przycisk X zamyka aplikację. | Quando ativado, o botão X oculta o RemEx na bandeja do sistema e o mantém em execução. Quando desativado, o botão X encerra o aplicativo. |
| Settings_CloseToTrayTooltip | Wybierz, czy zamknięcie okna minimalizuje RemEx do zasobnika, czy całkowicie go zamyka. | Escolha se fechar a janela minimiza o RemEx para a bandeja ou o encerra completamente. |
| Settings_ExitApp | Zamknij RemEx | Sair do RemEx |
| Settings_ExitAppDesc | Całkowicie zamknij aplikację i zatrzymaj jej proces w tle. | Fechar completamente o aplicativo e interromper seu processo em segundo plano. |
| Settings_ExitAppButton | Zamknij | Sair |

| Key | tr | uk |
|---|---|---|
| Settings_CloseToTray | Kapatırken tepsiye küçült | Згортати в трей під час закриття |
| Settings_CloseToTrayDesc | Etkinleştirildiğinde, X düğmesi RemEx'i sistem tepsisine gizler ve çalışmaya devam etmesini sağlar. Devre dışı bırakıldığında, X düğmesi uygulamayı kapatır. | Якщо ввімкнено, кнопка X приховує RemEx у системний трей і залишає його запущеним. Якщо вимкнено, кнопка X закриває застосунок. |
| Settings_CloseToTrayTooltip | Pencereyi kapatmanın RemEx'i tepsiye küçültmesini mi yoksa tamamen kapatmasını mı istediğinizi seçin. | Виберіть, чи закриття вікна згортає RemEx у трей, чи повністю закриває його. |
| Settings_ExitApp | RemEx'ten Çık | Вийти з RemEx |
| Settings_ExitAppDesc | Uygulamayı tamamen kapatır ve arka plan işlemini durdurur. | Повністю закрити застосунок і зупинити його фоновий процес. |
| Settings_ExitAppButton | Çık | Вийти |

**Acceptance:** re-run the parity check — every language file has 804 keys, 0 missing:
```powershell
# quick parity check (PowerShell)
$base = ([xml](Get-Content Remex.Client/Localization/Strings.resx)).root.data.name
foreach ($l in 'es','fr','hi','id','pl','pt-BR','tr','uk') {
  $k = ([xml](Get-Content "Remex.Client/Localization/Strings.$l.resx")).root.data.name
  "${l}: missing=" + (Compare-Object $base $k | Where-Object SideIndicator -eq '<=' | Measure-Object).Count
}
```

### LOC-2: Hardcoded user-facing strings in XAML
1. `SettingsView.axaml` line ~156: `Content="Disconnect"` → `Content="{conv:Localize Btn_Disconnect}"` (key already exists; match the file's existing Localize xmlns alias).
2. `ConfirmationDialog.axaml` line 4: `Title="Confirm"` (OS window title). Add key `Dialog_ConfirmTitle` to base `Strings.resx` (value `Confirm`) and all 8 languages (es `Confirmar`, fr `Confirmer`, hi `पुष्टि करें`, id `Konfirmasi`, pl `Potwierdź`, pt-BR `Confirmar`, tr `Onayla`, uk `Підтвердити`), then bind: `Title="{local:Localize Dialog_ConfirmTitle}"` (check whether the markup extension works on `Window.Title` in this codebase — other windows like `MainWindow` use literals; if the extension fails on Title, set it from code-behind in the constructor: `Title = Localization.Strings.Dialog_ConfirmTitle;` — note the generated `Strings` class property must exist or use the `LocalizationService.Instance["Dialog_ConfirmTitle"]` indexer used by `AboutViewModel`).
3. Leave alone (intentional): keyboard shortcut literals in `AboutView`, `Watermark="wss://host:port/ws"` URL examples, `Watermark="#RRGGBB"`, `Title="Remex — Command Center"` branding, `Text="REMEX"` wordmark.

### LOC-3: FAQ items don't refresh on live language switch
`AboutViewModel.LoadFaq()` (`ViewModels/AboutViewModel.cs` ~line 70) reads `LocalizationService.Instance["Faq_Q1_Question"]` … once in the constructor. The app advertises live 8-language switching without restart — the FAQ list keeps the old language until the About view is recreated. **Fix:** subscribe to the localization-changed signal (inspect `LocalizationService` — it raises a culture/`PropertyChanged` event consumed by the `Localize` markup extension), and on change: `FaqItems.Clear(); LoadFaq();`. Unsubscribe pattern: if the VM is long-lived singleton, a plain subscription is fine; if transient, use a weak handler consistent with how other VMs in the codebase subscribe.

**Acceptance:** switch language in Settings while the About view is open → FAQ questions change language without reopening.

### LOC-4 (P2): Same-as-English triage
117–172 entries per language are byte-identical to English. Many are legitimately invariant (`RAM`, `GPU`, `OK`, `Wi-Fi`, proper nouns, format strings like `{0} ms`). Generate the candidate list per language (reuse the parity script above but compare values instead of names), exclude entries that are ALL-CAPS acronyms / contain `{0}` only / ≤3 chars / proper nouns (`RemEx`, `Tailscale`, `WireGuard`, `GitHub`), and translate the remainder. **Budget guard:** if the filtered remainder exceeds ~60 strings per language, translate only `fr` and `id` (the two worst: 165/172) and file the rest in the final report as a follow-up list.

---

## B4. Elegance workstream — make the Avalonia UI clean and consistent (E-1 … E-7)

The design system in `App.axaml` is already good (geometry icon library, `glass-card`/`glass-button` styles with spring-eased transitions, theme tokens). The problem is views bypassing it. These changes route everything back through the system.

### E-1: Nav rail — monochrome icons with accent-on-active (replaces 8 hardcoded neon fills)
**File:** `ShellView.axaml` nav buttons (~lines 105–195). Currently each nav `Path` hardcodes its own color (`#22C55E`, `#FF6B6B`, `#00F3FF`, `#FFB800`, `#34D399`, `#94A3B8`, `#E4E4E7`, `#9999FF`) — a rainbow that fights every theme and looks busiest exactly where the UI should be calmest.

**Fix:**
1. In `App.axaml` `Application.Styles`, add (near the existing `glass-button` styles):
```xml
<!-- Nav rail icons: quiet by default, accent when active, brighten on hover -->
<Style Selector="Button.nav-item Path">
    <Setter Property="Fill" Value="{DynamicResource TextSecondaryBrush}"/>
    <Setter Property="Transitions">
        <Transitions><BrushTransition Property="Fill" Duration="0:0:0.2" Easing="CubicEaseOut"/></Transitions>
    </Setter>
</Style>
<Style Selector="Button.nav-item:pointerover Path">
    <Setter Property="Fill" Value="{DynamicResource TextPrimaryBrush}"/>
</Style>
<Style Selector="Button.nav-item-active Path">
    <Setter Property="Fill" Value="{DynamicResource AccentPrimaryBrush}"/>
</Style>
```
2. In `ShellView.axaml`, delete the `Fill="..."` attribute from **every** `<Path>` inside a `Button.nav-item` (including the Home one currently using `AccentPrimaryBrush` — the style now owns it). Do **not** touch `Path`s outside nav buttons.

**Acceptance:** nav icons are uniformly muted, the active page's icon is accent-colored, hover brightens; correct under all four themes.

### E-2: Replace functional emoji glyphs with the existing geometry-icon system
Emoji render inconsistently across Windows/Linux font stacks and can't take theme colors. The icon infrastructure already exists — `IconWarning` is even defined but unused while `⚠` is hardcoded twice.
1. Add two geometries to `App.axaml` next to the existing ones:
```xml
<StreamGeometry x:Key="IconSearch">M9.5,3A6.5,6.5 0 0,1 16,9.5C16,11.11 15.41,12.59 14.44,13.73L14.71,14H15.5L20.5,19L19,20.5L14,15.5V14.71L13.73,14.44C12.59,15.41 11.11,16 9.5,16A6.5,6.5 0 0,1 3,9.5A6.5,6.5 0 0,1 9.5,3M9.5,5C7,5 5,7 5,9.5C5,12 7,14 9.5,14C12,14 14,12 14,9.5C14,7 12,5 9.5,5Z</StreamGeometry>
<StreamGeometry x:Key="IconClose">M19,6.41L17.59,5L12,10.59L6.41,5L5,6.41L10.59,12L5,17.59L6.41,19L12,13.41L17.59,19L19,17.59L13.41,12L19,6.41Z</StreamGeometry>
```
2. `ShellView.axaml` replacements (locate by quoted snippet):
   - line ~113 `<TextBlock Text="🔍" FontSize="10" .../>` → `<Path Data="{StaticResource IconSearch}" Fill="{DynamicResource TextSecondaryBrush}" Width="11" Height="11" Stretch="Uniform" VerticalAlignment="Center"/>`
   - lines ~233 and ~249 `<TextBlock ... Text="⚠" FontSize="16" Foreground="#FFB800" .../>` → `<Path Data="{StaticResource IconWarning}" Fill="{DynamicResource SystemWarningBrush}" Width="16" Height="16" Stretch="Uniform" VerticalAlignment="Center" Margin="0,0,10,0"/>` (preserve each line's original `Grid.Column`).
   - lines ~237, ~251, ~330: buttons with `Content="✕"` → replace the string content with `<Path Data="{StaticResource IconClose}" Fill="{DynamicResource TextMutedBrush}" Width="10" Height="10" Stretch="Uniform"/>` (keep each Button's command/visibility attributes untouched).
3. **Leave** the large decorative tutorial-page emoji (🖥️ 🔗 ⚙️ … 🎨 🎉, FontSize 48) and any emoji inside localized string *values* — they're content, not chrome.

### E-3: Consolidate inline hex colors into theme tokens
`ShellView` alone has ~100 inline hex colors; `HomeView` 8, `CustomizationView` 13, `DiagnosticLogsView` 6, `FileTransferView` 6. Replacement map (apply in **ShellView first**, then the others):

| Inline value | Replace with |
|---|---|
| `#22C55E` (success/connect) | `{DynamicResource SystemSuccessBrush}` |
| `#FFB800`, `#F59E0B` (warning) | `{DynamicResource SystemWarningBrush}` |
| `#FF6B6B`, `#F43F5E` (error/danger) | `{DynamicResource SystemErrorBrush}` |
| `#60000000` (settings backdrop) | `{DynamicResource GlassOverlayBrush}` |
| `#E6141720` (tutorial backdrop) | add token, see below |
| `#E0E0FF` (tutorial titles) | `{DynamicResource TextPrimaryBrush}` |
| `#AAAACC`, `#CCCCEE` (tutorial body) | `{DynamicResource TextSecondaryBrush}` |
| `#8888CC` (tutorial skip) | `{DynamicResource TextMutedBrush}` |
| `#0A0A1A` (tutorial diagram bg) | `{DynamicResource GlassBaseDarkBrush}` |
| `#1E1E2E` (tutorial diagram border) | `{DynamicResource CardBorderBrush}` |

New token — add to **all four** theme files (after AVA-3's block): `<SolidColorBrush x:Key="OverlayBackdropBrush" Color="#E6141720"/>` (in `SolarFlare.axaml` use a light-appropriate value: `#E6E8EAF2`).
**Exemptions — do not convert:** color-picker swatch palettes in `CustomizationView.axaml` (those hexes are *data*, the selectable colors themselves), per-sensor series colors in `CanvasView`/`DashboardBackgroundControl` if they form a data palette, and `BoxShadows` strings. When in doubt whether a hex is chrome or data: chrome appears on container/text properties (`Background`/`Foreground`/`BorderBrush` of layout elements); data appears in swatch lists/`ItemsControl` items.
**Acceptance:** `grep -c '="#' ShellView.axaml` drops from ~100 to < 15 (remaining ones documented in the final report as intentional).

### E-4: Tutorial page-dots — replace 19 hand-written Ellipses with an ItemsControl
**File:** `ShellView.axaml` lines ~584–602 (19 sibling `<Ellipse Width="8" ...>` rows differing only in converter parameter).
1. In `ShellViewModel` (locate the class defining `TutorialPageIndex` / `TutorialNextCommand`), add:
```csharp
public IReadOnlyList<int> TutorialPageDots { get; } = Enumerable.Range(0, 19).ToList(); // 19 = current page count; keep in sync with the page StackPanels
```
2. Replace the 19 Ellipse lines with:
```xml
<ItemsControl ItemsSource="{Binding TutorialPageDots}" HorizontalAlignment="Center">
    <ItemsControl.ItemsPanel>
        <ItemsPanelTemplate><StackPanel Orientation="Horizontal" Spacing="6"/></ItemsPanelTemplate>
    </ItemsControl.ItemsPanel>
    <ItemsControl.ItemTemplate>
        <DataTemplate>
            <Ellipse Width="8" Height="8" Fill="{DynamicResource AccentPrimaryBrush}"
                     Opacity="{Binding $parent[ItemsControl].((vm:ShellViewModel)DataContext).TutorialPageIndex,
                               Converter={x:Static conv:IntEqualToOpacityConverter.Instance},
                               ConverterParameter={Binding}}"/>
        </DataTemplate>
    </ItemsControl.ItemTemplate>
</ItemsControl>
```
⚠️ `ConverterParameter` cannot be a `Binding` in Avalonia — if the existing dots use `IntEqualConverter` with a literal parameter, the clean equivalent is a `MultiBinding` over (`TutorialPageIndex`, the item value) with a tiny `EqualsToOpacityConverter`. If `MultiBinding` proves awkward here, **keep the 19 Ellipses and skip this item** — report SKIPPED. Do not burn more than one attempt on it.
3. First count the actual number of tutorial pages (count the `IsVisible="{Binding TutorialPageIndex, Converter=...` StackPanels, currently ~21 page panels at lines 406–573) and size `TutorialPageDots` to match the dot count currently rendered (19).

### E-5: Typography + keyboard-focus polish
1. Add semantic text classes to `App.axaml` styles:
```xml
<Style Selector="TextBlock.h1"><Setter Property="FontSize" Value="22"/><Setter Property="FontWeight" Value="Black"/></Style>
<Style Selector="TextBlock.h2"><Setter Property="FontSize" Value="16"/><Setter Property="FontWeight" Value="SemiBold"/></Style>
<Style Selector="TextBlock.caption"><Setter Property="FontSize" Value="11"/><Setter Property="Foreground" Value="{DynamicResource TextMutedBrush}"/></Style>
```
Apply opportunistically where views hand-roll the same combos (`FontSize="22" FontWeight="Black"` in the tutorial titles, section headers in `SettingsView`/`AboutView`) — convert only exact-match combos, don't redesign.
2. Keyboard focus visibility (accessibility): add
```xml
<Style Selector="Button.glass-button:focus-visible, Button.nav-item:focus-visible, Button.theme-tile:focus-visible">
    <Setter Property="BorderBrush" Value="{DynamicResource AccentPrimaryBrush}"/>
    <Setter Property="BorderThickness" Value="2"/>
</Style>
```

### E-6: `ConfirmationDialog.axaml` lacks `x:DataType`
The only view without compiled bindings. Add `x:DataType="vm:ConfirmationDialogViewModel"` (verify the actual VM type from its code-behind/`DataContext` usage; if the dialog is driven by plain properties without a VM, add `x:CompileBindings="False"` explicitly with a comment instead, so the omission is documented rather than accidental).

### E-7: `HomeView` giant background watermark says `CLindsay94`
`HomeView.axaml` line ~55: `<TextBlock Text="CLindsay94" FontSize="280" ... Opacity=...>` — the author's username as the hero watermark. For product polish, change to `Text="REMEX"`. **This is a one-word branding decision** — make the change, but call it out prominently in the final report so the owner can veto.

---

## B5. Explicit non-findings (desktop — do NOT "fix")
- The Android FAQ (11 items) and Tutorial are fully resource-driven and localized — no action.
- The desktop FAQ exists in `AboutView` (11 localized Q/A via `AboutViewModel`) — only the live-refresh gap (LOC-3) needs fixing.
- The desktop tutorial overlay (ShellView ~395–615) is fully localized (`Tutorial_P0_*` … keys) — only styling tokens (E-3) and dots (E-4) apply.
- `App.axaml`'s glass-card/glass-button styles, spring easings, and StreamGeometry icon library are well-built — extend them, don't restructure.
- Tooltips: 81 `ToolTip.Tip` usages across views are localized via `{conv:Localize ...}` — no systematic gap found.
- `LocalizeExtension` + `Strings.Designer.cs` strongly-typed access is a sound localization architecture.
- `CanvasView`/`SettingsView` `Watermark` URL examples and `AboutView` keyboard-shortcut literals are intentionally invariant.

## B6. Verification (desktop) — see end of Addendum B

# Addendum C — Foundation: Build System, Dependencies, Host Architecture, Remote-Desktop Hardening

**Scope:** build scripts (`build-remex.ps1`, `scripts/`, `installer/`), NuGet dependency graph, the headless-host-vs-embedded-host architecture decision, and the remote-desktop input pipeline (Android keyboard → host injection). Same ground rules as §0.

**Architecture facts established by this audit (the agent should treat these as ground truth):**
- `Remex.Client.Desktop/Program.cs` starts an **embedded in-process host** (`TryStartHost` on port 5005, falling back to 5006) — **unless** the Windows service `RemexHost` is running (line ~51), in which case it skips embedding.
- The Inno installer (`installer/RemEx.iss`) offers "Desktop Client only" vs "Client + Host Service"; the service is registered by `scripts/install-service.ps1` (`New-Service`, StartupType Automatic, runs as a **user account** whose credentials are collected in the wizard).
- A Windows service runs in **session 0**: power commands, telemetry, WOL, and pairing all work there, but DXGI desktop duplication **cannot capture the interactive desktop** — the `.iss` text itself admits "Session 0 cannot provide interactive desktop features by itself", and `HostBootstrapper` ships a `WindowsRemoteDesktopDiagnosticReport` for exactly this failure.
- `RemoteDesktopViewModel.cs` (~line 792) already disambiguates hosts via `meta.HostInstanceId == App.EmbeddedHostInstanceId`.

---

## C1. Build system & dependency hygiene

### BLD-C1 (P2): Dead files in the repo root and scripts
Delete: root `package.json` (contents: `{}`), root `package-lock.json`, `installer/build-installer.ps1.bak`, `scripts/crop_icon_vector_backup.py`. Verify nothing references them first (`grep -rn "package.json\|crop_icon_vector_backup\|build-installer.ps1.bak" --include="*.ps1" --include="*.sh" --include="*.yml" .`).

### BLD-C2 (P2): Adopt Central Package Management; fix version drift
Symptoms: `Microsoft.Extensions.*` split between `10.0.3` and `10.0.5` across projects; `Microsoft.Win32.Registry 5.0.0` referenced on net10.0 where the API is in-box (legacy shim package).
1. Create `Directory.Packages.props` at repo root with `<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>` and one `<PackageVersion>` entry per package, taking the **highest** version currently used (i.e. all `Microsoft.Extensions.*` and `System.*` 10.0.x packages → `10.0.5`; everything else stays at its current version).
2. Strip `Version=` attributes from every `<PackageReference>` in all 7 `.csproj` files.
3. Remove `<PackageReference Include="Microsoft.Win32.Registry" ...>` from `Remex.Host.csproj`; build — if `Microsoft.Win32.Registry` types fail to resolve, restore the reference at `10.0.x` band equivalent (do not keep 5.0.0).
4. **Do NOT bump:** `FluentAssertions` 7.0.0 (v8 changed license — the pin is deliberate; add a comment saying so), `SkiaSharp` 2.88.8 (3.x is a breaking major; file a follow-up bead instead), `Makaretu.Dns.Multicast` 0.27.0 (unmaintained but load-bearing for mDNS; document the risk in a comment, no replacement in this pass).

**Acceptance:** `dotnet build Remex.sln -c Release` and `dotnet test Remex.sln` pass; `dotnet list Remex.sln package` shows no duplicate-version packages.

### BLD-C3 (P2): Build entry points — granular targets + a BUILDING.md
`build-remex.ps1` (554 lines) is solid (interactive wizard, SDK/NDK auto-install, staging into `build_output/`), but the only granularity is `android|linux|windows|all`, and the per-task entry points are undiscoverable.
1. Extend the `ValidateSet` on `-Target` with three aliases that reuse the existing blocks: `windows-client` (the `dotnet publish` step only, skip Inno), `installer` (skip publish if the publish dir already exists; run Inno only), `apk` (alias for android). Implementation: wrap the existing "Windows TARGET" block's publish and ISCC sections in `if` conditions keyed on the new values — do not restructure the script.
2. Create `docs/BUILDING.md` with the task-to-command matrix (verify each command works before writing it down):
```markdown
| Task                          | Command |
|---|---|
| Run host (dev)                | dotnet run --project Remex.Host |
| Run desktop client (dev)      | dotnet run --project Remex.Client.Desktop |
| All tests                     | dotnet test Remex.sln |
| Full release, all platforms   | ./build-remex.ps1 -c release -t all |
| Windows client publish only   | ./build-remex.ps1 -c release -t windows-client |
| Windows installer only        | ./build-remex.ps1 -c release -t installer |
| Android APK (hardened fresh)  | ./scripts/android-fresh.ps1 -Configuration Release |
| Android via unified script    | ./build-remex.ps1 -c release -t android |
| Linux packages                | ./installer/build-linux.sh   (WSL on Windows) |
| Install Windows service       | ./scripts/install-service.ps1 -Action Install  (admin) |
| Remove Windows service        | ./scripts/install-service.ps1 -Action Uninstall (admin) |
| Linux host prerequisites      | dotnet run --project Remex.Host -- --doctor |
```
3. Update the stale "API Level 36" comment in `build-remex.ps1` (~line 330) and its sdkmanager install target to API 37 (gradle already targets `compileSdk = 37`); same DOC-1 drift as CLAUDE.md.

---

## C2. Architecture: headless host vs. integrated client — DECISION + fixes

### ARCH-0 (decision record — add to `docs/`, no code): Keep the hybrid; it is the only design that satisfies the requirement
The question was whether to drop `Remex.Host` (headless) and just autostart the desktop client. **Answer: no — keep both planes, because Windows makes them physically non-mergeable:**
- A user-session process (desktop client + embedded host) **cannot run before login**. If the requirement is "phone can send commands while the PC sits at the login screen / nobody logged in," only a session-0 Windows service (or scheduled task at boot — same constraint) can serve it.
- A session-0 service **cannot stream the interactive desktop** (DXGI duplication requires an interactive session; capturing the secure login desktop is privileged territory RemEx should not enter).

So the correct model — which the codebase already 80% implements — is **two planes**:
| Plane | Process | Runs | Provides |
|---|---|---|---|
| Command plane | `Remex.Host` as Windows service / systemd unit | from boot, pre-login | power commands, telemetry, WOL, pairing |
| Interactive plane | embedded host inside `Remex.Client.Desktop` | from user login | remote desktop streaming, input, app launcher |

Write this as `docs/ARCHITECTURE-HOST.md` (one page, the table above plus the three findings below). The remaining work is closing the three gaps:

### ARCH-1 (P1): Installing the service currently *disables* remote desktop streaming
`Remex.Client.Desktop/Program.cs` ~line 51: if `IsWindowsServiceRunning("RemexHost")`, the client **skips** starting its embedded host. The service (session 0) cannot stream the desktop → a user who chose "Client + Host Service" in the installer gets *worse* remote desktop than one who chose client-only.

**Fix:** always start the embedded host; when the service is running, start on the fallback port so both coexist:
```csharp
// Two-plane model: the RemexHost service (session 0) owns the command plane from boot;
// the embedded host owns the interactive plane (desktop streaming) for this login session.
// They coexist on different ports; the phone disambiguates via DesktopMeta.HostInstanceId.
int preferredPort = IsWindowsServiceRunning("RemexHost")
    ? RemexConstants.DefaultPort + 1   // service owns 5005; take 5006
    : RemexConstants.DefaultPort;
EmbeddedHostPort = TryStartHost(args, preferredPort)
    ?? TryStartHost(args, preferredPort + 1);
```
(Replace the existing `if (IsWindowsServiceRunning(...))`-guarded block; keep all the `App.OverrideHostPort` / `EmbeddedHostInstanceId` wiring that follows.) `HostBootstrapper` already has port-probing and mDNS advertisement, and the Android client already receives a display catalog/stream descriptor per host.
**Risk note — discovery chain verified 2026-06-12:** the advertised port is the real bound port, not a constant (`HostBootstrapper.cs:191` writes `actualPort` into `Host:Port`; `MdnsAdvertisingService.cs:30` reads it), and the phone resolves the advertised port rather than assuming 5005 (`NsdDiscoveryManager.kt` ~lines 123–128 takes `service.port` from the NSD resolve callback). **One gap found:** `MdnsAdvertisingService.cs:31` uses bare `Environment.MachineName` as the mDNS instance name, so with both planes running the two `_remex._tcp` responders would advertise the *same* instance name — an mDNS conflict; the phone may resolve either one nondeterministically. **Include in ARCH-1:** make the instance name port-qualified off the default port, e.g. `string instanceName = port == RemexConstants.DefaultPort ? Environment.MachineName : $"{Environment.MachineName} ({port})";`. The manual two-plane test remains the gate: if the phone cannot reach the 5006 host after this change, STOP, revert, and report BLOCKED with findings — do not improvise a protocol change.

### ARCH-2 (P1): The desired "runs automatically on PC startup" piece is missing — no client launch-at-login exists
No autostart implementation exists anywhere in `Remex.Client`/`Remex.Client.Desktop` (no registry Run key, no startup shortcut). Combined with close-to-tray (already shipped in `b352a24`), a launch-at-login toggle completes the story: service covers pre-login, autostarted tray client covers everything after login.
1. New service `Remex.Client.Desktop/Services/StartupRegistrationService.cs` (Windows: registry `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, value name `RemEx`, data `"<exe path>" --minimized`; Linux: write `~/.config/autostart/remex-client.desktop` derived from `installer/linux/remex-client.desktop` with `X-GNOME-Autostart-enabled=true`):
```csharp
public interface IStartupRegistrationService
{
    bool IsSupported { get; }
    bool IsEnabled();
    void SetEnabled(bool enabled);
}
```
(Windows impl with `Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true)`; guard with `OperatingSystem.IsWindows()`.)
2. Settings toggle next to the existing close-to-tray toggle in `SettingsView.axaml`/`SettingsViewModel` — new resx keys `Settings_LaunchAtLogin` ("Launch RemEx when you sign in"), `Settings_LaunchAtLoginDesc` ("Starts minimized to the tray so your PC is immediately reachable from your phone.") — **add to base + all 8 languages** (translate in the same style as LOC-1).
3. Handle `--minimized` in `Program.cs`/`App.axaml.cs`: start with the main window hidden, tray icon active (the tray plumbing already exists in `App.axaml`).
4. Installer: add an Inno `[Tasks]` entry `launchatlogin` (checked by default) writing the same Run key via `[Registry]` section.

### ARCH-3 (P2): Service installer friction — credentials are demanded for a plane that doesn't need them
`install-service.ps1` + the `.iss` wizard collect a username/password and grant Log-On-As-A-Service. For the command plane (power/telemetry/WOL/pairing), `LocalSystem` is sufficient and zero-friction; a named account adds nothing because session 0 can't do desktop features regardless of account.
**Fix:** in `install-service.ps1`, make `-Username`/`-Password` optional; when omitted, register with `New-Service` without `-Credential` (= LocalSystem). In `RemEx.iss`, replace the credentials page with a checkbox note ("Runs as LocalSystem. The service provides remote commands and telemetry before login; remote desktop activates when you sign in.") and keep an "advanced: run as specific account" escape hatch only if trivially preserved — otherwise delete the page and document the manual command. Keep `Grant-LogOnAsService` only for the named-account path.

---

## C3. Remote desktop input hardening (the Gboard "send" bug + UX)

### REM-1 (P0 for UX): Gboard shows a send (▶) action key that does nothing — make Enter work for every IME
**Path today:** hidden `BasicTextField` (`RemoteDesktopScreen.kt` ~lines 667–685) diffs text via `applyRemoteKeyboardEdit` (`RemoteKeyboardInput.kt`) → `sendText`/`sendKeyPress` → JSON `{"eventType":"keyDown","keyCode":13}` → host `RemoteDesktopHandler` (~line 745) → `WindowsInputSimulationService.KeyDown` (VK 13 = VK_RETURN ✓). The screen *already* sets `imeAction = ImeAction.Send` with `KeyboardActions(onSend = { onSendKeyPress(13) })` — added in commit `7e8c8ef`, so a pre-`7e8c8ef` APK explains the dead button; but the wiring is fragile regardless: it depends on the IME dispatching exactly `ACTION_SEND`, and Gboard's action key is suppressed/repurposed in several modes (e.g. while composing, or with some languages).

**Fix (make all paths lead to Enter):** in `RemoteDesktopScreen.kt`, replace the two lines at ~679–680 with:
```kotlin
keyboardOptions = KeyboardOptions(
        keyboardType = KeyboardType.Text,
        autoCorrectEnabled = false,
        // Default (not Send): on a multiline field Gboard renders a real ↵ Enter key,
        // whose '\n' insertion is converted to keycode 13 by applyRemoteKeyboardEdit.
        imeAction = ImeAction.Default
),
keyboardActions = KeyboardActions(
        // Fallbacks: any IME that still renders an action key routes here.
        onDone = { onSendKeyPress(13) },
        onGo = { onSendKeyPress(13) },
        onNext = { onSendKeyPress(13) },
        onSearch = { onSendKeyPress(13) },
        onSend = { onSendKeyPress(13) }
),
```
Notes for the agent: (a) confirm the `BasicTextField` is **not** `singleLine = true` — multiline is required for the ↵ path; (b) if the project's Compose version names the option `autoCorrect` instead of `autoCorrectEnabled`, use the available one; (c) `autoCorrect` off reduces Gboard composing-region churn, which the diff algorithm otherwise has to unwind as backspaces.

### REM-2 (P1): Verify the Enter keycode survives the Linux host path
`LinuxInputBackendRouter.KeyDown` (~line 130) appears to pass the **raw** keycode to `_eis.SendKey((uint)keyCode, ...)`. The Android client sends Windows-style VK codes (13 = Enter), but EIS/uinput expect **evdev** codes (`KEY_ENTER` = 28). `LinuxInputEventTranslator` has the correct mapping (`0x0D => 28`, ~line 159) — confirm `LinuxInputSimulationService.KeyDown/KeyUp` runs the translator **before** calling the router; if any branch (EIS, uinput, portal, xdotool) receives untranslated VK codes, route it through `LinuxInputEventTranslator` first. Add/extend a unit test in `Remex.Host.Tests` asserting VK 13 → evdev 28 and VK 8 (backspace) → evdev 14 through the public KeyDown path with a fake backend.

### REM-3 (P2): On-screen utility key row (UX hardening)
Priority note: P2 (not P3) is deliberate — this is a capability gap, not polish: the diff-based keyboard can never express non-text keys (Esc, Tab, arrows, Ctrl+x). Add a compact horizontal key bar shown only while the remote keyboard is open (anchor it above the IME, alongside the existing remote-desktop controls in `RemoteDesktopScreen.kt`): chips for `Esc`(27) `Tab`(9) `⌫`(8) `↵`(13) `←`(37) `↑`(38) `↓`(40) `→`(39) `Del`(46) `Win`(91), each calling `viewModel.sendKeyPress(code)` — codes are Windows VKs, which REM-2 makes safe on Linux too. Use `FilledTonalButton`/`AssistChip` in a `LazyRow` with M3 spacing (8.dp gaps, `MaterialTheme.colorScheme` tokens), haptic on press (the codebase has `HapticFeedbackConstants` patterns to copy). New string resources for content descriptions in `strings.xml` + all locale files (follow the project's localization completeness convention).

### REM-4 (P3): Input-path resilience checks (verify, fix only if broken)
1. `viewModel.sendInput(...)` — confirm input JSON is dropped (not queued unbounded) when the stream socket is down, and that a user typing during a reconnect doesn't replay a burst afterward.
2. `applyRemoteKeyboardEdit` backspace storm: a "select-all + delete" on a 256-char buffer emits up to 256 individual backspace key events; cap at e.g. 32 and otherwise send Ctrl+A/Delete… **do not implement** — just measure and report whether Gboard swipe-deletion actually triggers this in practice.
3. Confirm keycode 8 (backspace) maps correctly on Windows (VK_BACK = 8 ✓ by passthrough) — covered by the REM-2 test.

---

## C4. Addendum C non-findings (do NOT "fix")
- `build-remex.ps1`'s interactive wizard, SDK/NDK auto-install, and `build_output/` staging are good — extend, don't rewrite.
- `installer/Output/` build artifacts are correctly gitignored.
- The embedded-host fallback-port probe and `HostInstanceId` disambiguation already exist — ARCH-1 builds on them.
- `Remex.Core` net10.0-android NativeAOT+Trimmed configuration is correct.
- `Program.cs --doctor` being Linux-only is intentional (Windows has the in-band diagnostic report).
- The pairing flow's `GetAwaiter().GetResult()` with bounded CTS budgets (noted in §5) remains acceptable.

## C5. Verification (Addendum C)
```powershell
dotnet build Remex.sln -c Release && dotnet test Remex.sln          # BLD-C2, REM-2 test
./build-remex.ps1 -c release -t windows-client                       # BLD-C3 new target
./build-remex.ps1 -c release -t installer                            # BLD-C3 new target
cd RemEx.Android; .\gradlew :app:assembleDebug; cd ..                # REM-1/3

# Manual (report SKIPPED per item if no device/second machine):
# 1. Install service (no credentials) → reboot → phone sends shutdown command at login screen (ARCH-3, command plane)
# 2. Log in → desktop client autostarts minimized to tray (ARCH-2) → remote desktop streams (ARCH-1: embedded host on 5006 while service holds 5005)
# 3. Android remote desktop + Gboard: action key sends Enter in a PC text editor; Esc/Tab/arrow chips work (REM-1/3)
# 4. Same test against a Linux host (REM-2)
```
Work order: BLD-C1/C2/C3 → REM-1/REM-2 → ARCH-2 → ARCH-3 → ARCH-1 (last: it needs the manual two-plane test) → REM-3 → REM-4.

ARCH-1 sits last because it ends in a manual hardware gate, not because anything builds on it — no other item is downstream of ARCH-1, so a BLOCKED outcome reverts ARCH-1 alone with zero wasted work elsewhere. Its desk-verifiable precondition (discovery resolves the advertised port end-to-end) is already confirmed; see the ARCH-1 risk note.

**PR batching:** land the work as two PRs — **PR A:** all P0/P1 items (JNI fixes, REM-1/REM-2, ARCH-1/ARCH-2, BLD-C1/C2/C3); **PR B:** remaining P2/P3 polish and infrastructure (CMP-*, CS-*, VC-1, ARCH-3, REM-3/REM-4, LOC/doc items). REM-3 is the natural seam.

**Commit grain:** one commit per PRD ID (`[Agent:<Name>] type: <ID> description` per AGENTS.md). Mechanical sweeps (CS-1, CS-2, CMP-1) are one commit each even when they touch many files.
```powershell
dotnet build Remex.sln -c Release
dotnet test Remex.Client.Tests

# Manual pass:
# 1. Launch client (dotnet run --project Remex.Client.Desktop)
# 2. Open Diagnostic Logs view (AVA-2) and Remote Desktop view (AVA-1) — no crash
# 3. Customization → switch all 4 themes — corner radii + nav colors correct (AVA-3, E-1)
# 4. Settings → switch each of the 8 languages — close-to-tray section translates (LOC-1),
#    About FAQ refreshes live (LOC-3)
# 5. Keyboard-Tab through the nav rail — focus ring visible (E-5)
# 6. Replay tutorial overlay — dots + colors themed (E-3, E-4)
```
Work order within this addendum: B1 → B2 → LOC-1/2/3 → E-1/2/3 → E-4/5/6/7 → LOC-4.
