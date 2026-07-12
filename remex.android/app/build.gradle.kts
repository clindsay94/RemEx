import com.android.build.api.variant.VariantOutputConfiguration
import com.android.build.api.variant.impl.VariantOutputImpl
import org.jetbrains.kotlin.gradle.dsl.JvmTarget
import java.security.MessageDigest
import java.util.Properties
import java.util.zip.ZipFile

plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.compose)
    id("kotlin-parcelize")
    id("com.google.gms.google-services")
    id("com.google.firebase.crashlytics")
}
val repoRootDir: File = rootProject.projectDir.parentFile
val remexAndroidApplicationId = "com.clindsay94.remex"
val remexCoreProjectDirLocal = File(repoRootDir, "remex.core")
val androidLocalProperties = Properties().apply {
    rootProject.file("local.properties").takeIf { it.exists() }?.inputStream()?.use(::load)
}

// ── Version management ──────────────────────────────────────────────────────
// Source of truth: app/version.properties (tracked in git).
// - remexFreshAssembleRelease  → builds with current version, no changes.
// - remexPublishRelease        → bumps versionCode+1, minor+1 (patch→0),
//                                writes back to version.properties, then builds.
val versionPropsFile = file("version.properties")
val versionProps = Properties().apply {
    if (versionPropsFile.exists()) versionPropsFile.inputStream().use(::load)
}

var remexVersionCode = versionProps.getProperty("versionCode", "1").toInt()
var remexVersionName: String? = versionProps.getProperty("versionName", "1.0.0")

val isPublishBuild = gradle.startParameter.taskNames.any {
    it.contains("remexPublishRelease", ignoreCase = true)
}

if (isPublishBuild) {
    remexVersionCode += 1
    val parts = remexVersionName?.split(".")?.toMutableList() ?: mutableListOf("1", "0", "0")
    if (parts.size >= 3) {
        parts[2] = (parts[2].toInt() + 1).toString()
    }
    remexVersionName = parts.joinToString(".")

    versionProps.setProperty("versionCode", remexVersionCode.toString())
    versionProps.setProperty("versionName", remexVersionName)
    versionPropsFile.writer().use { w ->
        w.write("versionCode=$remexVersionCode\n")
        w.write("versionName=$remexVersionName\n")
    }
    logger.lifecycle("remexPublishRelease: version bumped to $remexVersionName (versionCode=$remexVersionCode)")
}
// ─────────────────────────────────────────────────────────────────────────────

android {
    namespace = remexAndroidApplicationId
    //noinspection GradleDependency
    compileSdk = 37

    defaultConfig {
        applicationId = remexAndroidApplicationId
        minSdk = 26
        targetSdk = 37
        //noinspection OldTargetApi
        versionCode = remexVersionCode
        versionName = remexVersionName

        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"

        ndk {
            abiFilters += listOf("arm64-v8a")
        }
    }
    signingConfigs {
        getByName("debug") {
            // Use a local copy of the debug keystore to avoid 'other has different root' error on Windows
            // when the project is on a different drive than the default C:\Users\...\.android\debug.keystore.
            val localDebugKeystore = file("debug.keystore")
            if (localDebugKeystore.exists()) {
                storeFile = localDebugKeystore
            }
        }
        create("release") {
            storeFile =
                androidLocalProperties.getProperty("remex.signing.storeFile")?.let { file(it) }
            storePassword = androidLocalProperties.getProperty("remex.signing.storePassword")
            keyAlias = androidLocalProperties.getProperty("remex.signing.keyAlias")
            keyPassword = androidLocalProperties.getProperty("remex.signing.keyPassword")
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = true
            isShrinkResources = true
            // Use the real release keystore when configured in local.properties;
            // fall back to debug signing for local test builds without secrets.
            signingConfig = if (androidLocalProperties.getProperty("remex.signing.storeFile") != null)
                signingConfigs.getByName("release")
            else
                signingConfigs.getByName("debug")
            ndk { debugSymbolLevel = "FULL" }
            proguardFiles(
                getDefaultProguardFile("proguard-android-optimize.txt"),
                "proguard-rules.pro"
            )
        }
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlin {
        compilerOptions {
            jvmTarget.set(JvmTarget.JVM_17)
            freeCompilerArgs.add("-Xannotation-default-target=param-property")
        }
    }
    buildFeatures {
        compose = true
        viewBinding = true
        buildConfig = true
    }
    packaging {
        jniLibs {
            useLegacyPackaging = false
        }
    }
    buildToolsVersion = "37.0.0"
    ndkVersion = "30.0.14904198"

    testOptions {
        unitTests {
            // Return defaults instead of throwing on unmocked android.* framework calls
            // (e.g. android.util.Log used inside RemexCoreClient's catch blocks). Tests
            // that exercise singletons whose static init touches Log would otherwise fail
            // with "Method e in android.util.Log not mocked". See:
            // https://developer.android.com/r/studio-ui/build/not-mocked
            isReturnDefaultValues = true
        }
    }

    sourceSets {
        getByName("main") {
            // Keep packaging deterministic by ignoring src/main/jniLibs for Remex.Core.
            jniLibs.directories.clear()
        }
        getByName("debug") {
            jniLibs.directories.clear()
            jniLibs.directories.add(layout.buildDirectory.dir("generated/remexJniLibs/debug").get().asFile.absolutePath)
        }
        getByName("release") {
            jniLibs.directories.clear()
            jniLibs.directories.add(layout.buildDirectory.dir("generated/remexJniLibs/release").get().asFile.absolutePath)
        }
    }
    dependenciesInfo {
        includeInApk = true
        includeInBundle = true
    }
}

androidComponents {
    onVariants { variant ->
        val mainOutput =
            variant.outputs.single { it.outputType == VariantOutputConfiguration.OutputType.SINGLE }
        val globalVersionName = android.defaultConfig.versionName ?: "1.0"

        // Use set on the file name property directly using the newer variant API
        if (mainOutput is VariantOutputImpl) {
            mainOutput.outputFileName.set("RemEx-V${globalVersionName}-${variant.name}.apk")
        }
    }
}


val remexGeneratedDebugJniRoot =
    layout.buildDirectory.get().asFile.resolve("generated/remexJniLibs/debug")
val remexGeneratedReleaseJniRoot =
    layout.buildDirectory.get().asFile.resolve("generated/remexJniLibs/release")
val androidNdkVersion = "30.0.14904198"
val androidSdkDir = androidLocalProperties.getProperty("sdk.dir")
    ?: error("Missing sdk.dir in local.properties")
val androidNdkDir = File(androidSdkDir, "ndk/$androidNdkVersion")
val androidNdkDirForMsbuild = androidNdkDir.absolutePath.trimEnd('\\', '/') + File.separator

val remexGeneratedDebugArm64So = File(remexGeneratedDebugJniRoot, "arm64-v8a/libRemexCore.so")
val remexGeneratedReleaseArm64So = File(remexGeneratedReleaseJniRoot, "arm64-v8a/libRemexCore.so")
val mergedDebugArm64So = layout.buildDirectory.get().asFile.resolve(
    "intermediates/merged_native_libs/debug/mergeDebugNativeLibs/out/lib/arm64-v8a/libRemexCore.so"
)
val mergedReleaseArm64So = layout.buildDirectory.get().asFile.resolve(
    "intermediates/merged_native_libs/release/mergeReleaseNativeLibs/out/lib/arm64-v8a/libRemexCore.so"
)
val strippedDebugArm64So = layout.buildDirectory.get().asFile.resolve(
    "intermediates/stripped_native_libs/debug/stripDebugDebugSymbols/out/lib/arm64-v8a/libRemexCore.so"
)
val strippedReleaseArm64So = layout.buildDirectory.get().asFile.resolve(
    "intermediates/stripped_native_libs/release/stripReleaseDebugSymbols/out/lib/arm64-v8a/libRemexCore.so"
)

fun ByteArray.toHexString(): String = joinToString("") { "%02x".format(it) }

fun sha256(file: File): String {
    val digest = MessageDigest.getInstance("SHA-256")
    file.inputStream().buffered().use { input ->
        val buffer = ByteArray(8192)
        while (true) {
            val read = input.read(buffer)
            if (read <= 0) {
                break
            }
            digest.update(buffer, 0, read)
        }
    }
    return digest.digest().toHexString()
}

fun sha256(bytes: ByteArray): String =
    MessageDigest.getInstance("SHA-256").digest(bytes).toHexString()

fun requireExistingFile(file: File, label: String): File {
    if (!file.exists()) {
        throw GradleException("$label not found: ${file.absolutePath}")
    }
    return file
}

fun latestApkIn(directory: File): File {
    val apks = directory
        .listFiles()
        ?.filter { it.isFile && it.extension.equals("apk", ignoreCase = true) }
        ?.sortedByDescending { it.lastModified() }
        .orEmpty()

    return apks.firstOrNull()
        ?: throw GradleException("No APK was found in ${directory.absolutePath}")
}

// Candidate locations for the NativeAOT-built libRemexCore.so, covering BOTH the legacy
// per-project bin/ layout and the repo-wide artifacts/ layout (Directory.Build.props sets
// UseArtifactsOutput=true, which relocates output to
// artifacts/bin/remex.core/<config-lower>_net10.0-android_android-arm64/native/).
// remexCoreProjectDir is <repoRoot>/remex.core, so its parent is the repo root.
fun arm64PublishedSoCandidates(remexCoreProjectDir: File, configuration: String): List<File> {
    val repoRoot = remexCoreProjectDir.parentFile
    val artifactsPivot = "${configuration.lowercase()}_net10.0-android_android-arm64"
    return listOf(
        // Artifacts layout (current default) — native lib and publish output.
        File(repoRoot, "artifacts/bin/remex.core/$artifactsPivot/native/libRemexCore.so"),
        File(repoRoot, "artifacts/publish/remex.core/$artifactsPivot/libRemexCore.so"),
        // Legacy per-project layout (UseArtifactsOutput disabled).
        File(remexCoreProjectDir, "bin/$configuration/net10.0-android/android-arm64/native/libRemexCore.so"),
        File(remexCoreProjectDir, "bin/$configuration/net10.0-android/android-arm64/publish/libRemexCore.so")
    )
}

fun getArm64PublishedSoPath(remexCoreProjectDir: File, configuration: String): File {
    val candidates = arm64PublishedSoCandidates(remexCoreProjectDir, configuration)
    return candidates
        .filter { it.exists() }
        .maxByOrNull { it.lastModified() }
        ?: candidates.first()
}

val javaSdkDirTopLevel: String? = project.findProperty("org.gradle.java.home")?.toString() ?: System.getProperty("java.home")

val publishRemexCoreAndroidDebug by project.tasks.registering(Exec::class) {
    group = "remex"
    description = "Builds and links Remex.Core Android arm64 debug native library"
    workingDir = repoRootDir
    commandLine(
        "dotnet",
        "msbuild",
        "remex.core/remex.core.csproj",
        "-restore",
        "-t:Build,LinkNative",
        "-p:Configuration=Debug",
        "-p:TargetFramework=net10.0-android",
        "-p:RuntimeIdentifier=android-arm64",
        "-p:AndroidSdkDirectory=$androidSdkDir",
        "-p:AndroidNdkDirectory=$androidNdkDirForMsbuild",
        "-p:JavaSdkDirectory=$javaSdkDirTopLevel"
    )
}

val publishRemexCoreAndroidRelease by project.tasks.registering(Exec::class) {
    group = "remex"
    description = "Builds and links Remex.Core Android arm64 release native library"
    workingDir = repoRootDir
    commandLine(
        "dotnet",
        "msbuild",
        "remex.core/remex.core.csproj",
        "-restore",
        "-t:Build,LinkNative",
        "-p:Configuration=Release",
        "-p:TargetFramework=net10.0-android",
        "-p:RuntimeIdentifier=android-arm64",
        "-p:AndroidSdkDirectory=$androidSdkDir",
        "-p:AndroidNdkDirectory=$androidNdkDirForMsbuild",
        "-p:JavaSdkDirectory=$javaSdkDirTopLevel"
    )
}


abstract class SyncRemexCoreSoTask : DefaultTask() {
    @get:Input
    abstract val configuration: Property<String>

    @get:Input
    abstract val rcDir: Property<String>

    // The dotnet-published source .so candidates, declared as CONTENT-tracked inputs. Without this,
    // gradle only sees the (unchanging) configuration/rcDir strings and keeps the task UP-TO-DATE on an
    // -NoClean build even when the published library changed — so the APK packaged a stale .so and the
    // downstream hash verification failed (RemEx-l79). PathSensitivity.NONE: only content matters, not
    // which candidate path supplied it. Missing candidates are treated as empty by gradle.
    @get:InputFiles
    @get:PathSensitive(PathSensitivity.NONE)
    abstract val sourceCandidates: ConfigurableFileCollection

    @get:OutputFile
    abstract val generatedSo: RegularFileProperty

    @TaskAction
    fun doSync() {
        val conf = configuration.get()
        val generated = generatedSo.get().asFile

        // Read the candidate .so paths straight from the declared task inputs, so the up-to-date check
        // and the actual copy can never diverge (RemEx-l79). This MUST read the task property rather
        // than call the script-level arm64PublishedSoCandidates(): referencing a build-script function
        // from inside the task class captures the script instance and makes Gradle reject the class as
        // a non-static inner class. The registration populates sourceCandidates from that same helper.
        val candidates = sourceCandidates.files.toList()

        // Verify a file is a 64-bit AArch64 ELF so a stale/corrupt/cross-config artifact can never be
        // packaged into the APK (RemEx-hht). ELF header: magic 0x7F454C46, EI_CLASS==2 (64-bit) at
        // offset 4, e_machine==0xB7 (EM_AARCH64) little-endian u16 at offset 18.
        fun isAarch64Elf(file: File): Boolean = try {
            file.inputStream().use { input ->
                val header = ByteArray(20)
                if (input.read(header) < header.size) {
                    false
                } else {
                    val magicOk = header[0] == 0x7F.toByte() && header[1] == 'E'.code.toByte() &&
                        header[2] == 'L'.code.toByte() && header[3] == 'F'.code.toByte()
                    val is64Bit = header[4].toInt() == 2
                    val machine = (header[18].toInt() and 0xFF) or ((header[19].toInt() and 0xFF) shl 8)
                    magicOk && is64Bit && machine == 0xB7
                }
            }
        } catch (e: Exception) {
            false
        }

        // Restrict candidates to the requested configuration before choosing the newest. artifactsPivot
        // lowercases conf while bin/$conf preserves case, so the substring match must be case-insensitive
        // (RemEx-hht) — otherwise a "Release" request could match a path the casing comparison missed.
        val confLower = conf.lowercase()
        val source = candidates
            .filter { it.exists() }
            .filter { it.absolutePath.lowercase().contains(confLower) }
            .maxByOrNull { it.lastModified() }
            ?: candidates.first()

        if (!source.exists()) {
            throw GradleException("Published $conf libRemexCore.so not found: ${source.absolutePath}")
        }

        if (!isAarch64Elf(source)) {
            throw GradleException(
                "$conf libRemexCore.so at ${source.absolutePath} is not a 64-bit AArch64 ELF " +
                    "(stale, corrupt, or cross-config build). Rebuild Remex.Core for android-arm64."
            )
        }

        generated.parentFile.mkdirs()
        source.copyTo(generated, overwrite = true)

        fun sha256local(file: File): String {
            val digest = MessageDigest.getInstance("SHA-256")
            file.inputStream().buffered().use { input ->
                val buffer = ByteArray(8192)
                while (true) {
                    val read = input.read(buffer)
                    if (read <= 0) {
                        break
                    }
                    digest.update(buffer, 0, read)
                }
            }
            return digest.digest().joinToString("") { "%02x".format(it) }
        }

        val sourceHash = sha256local(source)
        val destHash = sha256local(generated)
        if (sourceHash != destHash) {
            throw GradleException(
                "$conf native library hash mismatch after sync. source=$sourceHash, destination=$destHash"
            )
        }
        if (generated.lastModified() < source.lastModified()) {
            throw GradleException(
                "$conf generated libRemexCore.so is older than published source. source=${source.lastModified()}, destination=${generated.lastModified()}"
            )
        }

        println("Synced $conf libRemexCore.so => ${generated.absolutePath} (sha256=$destHash)")
    }
}

val syncRemexCoreDebugSo by project.tasks.registering(SyncRemexCoreSoTask::class) {
    group = "remex"
    description = "Publishes and synchronizes debug libRemexCore.so into generated jniLibs"
    dependsOn(publishRemexCoreAndroidDebug)

    configuration.set("Debug")
    rcDir.set(remexCoreProjectDirLocal.absolutePath)
    sourceCandidates.from(arm64PublishedSoCandidates(remexCoreProjectDirLocal, "Debug"))
    generatedSo.set(remexGeneratedDebugArm64So)
}

val syncRemexCoreReleaseSo by project.tasks.registering(SyncRemexCoreSoTask::class) {
    group = "remex"
    description = "Publishes and synchronizes release libRemexCore.so into generated jniLibs"
    dependsOn(publishRemexCoreAndroidRelease)

    configuration.set("Release")
    rcDir.set(remexCoreProjectDirLocal.absolutePath)
    sourceCandidates.from(arm64PublishedSoCandidates(remexCoreProjectDirLocal, "Release"))
    generatedSo.set(remexGeneratedReleaseArm64So)
}

abstract class VerifyRemexCoreInApkTask : DefaultTask() {
    @get:Input
    abstract val configuration: Property<String>

    @get:Input
    abstract val rcDir: Property<String>

    // Not @InputFile: both files are produced by upstream tasks (syncRemexCore*So and
    // mergeXNativeLibs) and do not exist yet at task-property-validation time on a fresh/clean
    // build. With the configuration cache enabled, declaring them @InputFile makes Gradle fail
    // validation ("input file ... doesn't exist") before the producing task runs — this is what
    // broke `remexFreshAssembleDebug` until the config cache was cleared by hand. Same rationale
    // as strippedArm64So/apkDirectory below: the task has no outputs (so it always reruns) and
    // verifies existence + content hashes at execution time, so a formal input adds nothing.
    @get:Internal
    abstract val generatedArm64So: RegularFileProperty

    @get:Internal
    abstract val mergedArm64So: RegularFileProperty

    @get:Internal
    abstract val strippedArm64So: RegularFileProperty

    // Not an @InputDirectory: this directory is produced by the assembleDebug/assembleRelease
    // dependency and does not exist yet at task-property-validation time on a fresh/clean build.
    // Declaring it @InputDirectory makes Gradle fail validation ("directory ... doesn't exist")
    // before the producing task runs. The task has no outputs (so it always reruns) and validates
    // the actual APK at execution via latestApkIn(), so tracking it as a formal input adds nothing.
    @get:Internal
    abstract val apkDirectory: DirectoryProperty

    @TaskAction
    fun verify() {
        val conf = configuration.get()
        val rcDirFile = File(rcDir.get())
        val generated = generatedArm64So.get().asFile
        val merged = mergedArm64So.get().asFile
        val stripped = strippedArm64So.asFile.orNull
        val apkDir = apkDirectory.get().asFile

        // Search both the legacy per-project bin/ layout and the repo-wide artifacts/ layout
        // (Directory.Build.props sets UseArtifactsOutput=true). rcDirFile is <repoRoot>/remex.core.
        val repoRoot = rcDirFile.parentFile
        val artifactsPivot = "${conf.lowercase()}_net10.0-android_android-arm64"
        val candidates = listOf(
            File(repoRoot, "artifacts/bin/remex.core/$artifactsPivot/native/libRemexCore.so"),
            File(repoRoot, "artifacts/publish/remex.core/$artifactsPivot/libRemexCore.so"),
            File(rcDirFile, "bin/$conf/net10.0-android/android-arm64/native/libRemexCore.so"),
            File(rcDirFile, "bin/$conf/net10.0-android/android-arm64/publish/libRemexCore.so")
        )

        val published = candidates
            .filter { it.exists() }
            .maxByOrNull { it.lastModified() }
            ?: candidates.first()

        if (!published.exists()) {
            throw GradleException("Published $conf libRemexCore.so not found: ${published.absolutePath}")
        }

        if (!generated.exists()) {
            throw GradleException("Generated $conf libRemexCore.so not found: ${generated.absolutePath}")
        }

        if (!merged.exists()) {
            throw GradleException("Merged $conf libRemexCore.so not found: ${merged.absolutePath}")
        }

        val apks = apkDir
            .listFiles()
            ?.filter { it.isFile && it.extension.equals("apk", ignoreCase = true) }
            ?.sortedByDescending { it.lastModified() }
            .orEmpty()

        val apk = apks.firstOrNull()
            ?: throw GradleException("No APK was found in ${apkDir.absolutePath}")

        fun sha256local(file: File): String {
            val digest = MessageDigest.getInstance("SHA-256")
            file.inputStream().buffered().use { input ->
                val buffer = ByteArray(8192)
                while (true) {
                    val read = input.read(buffer)
                    if (read <= 0) {
                        break
                    }
                    digest.update(buffer, 0, read)
                }
            }
            return digest.digest().joinToString("") { "%02x".format(it) }
        }

        fun sha256localBytes(bytes: ByteArray): String {
            return MessageDigest.getInstance("SHA-256").digest(bytes)
                .joinToString("") { "%02x".format(it) }
        }

        val publishedHash = sha256local(published)
        val generatedHash = sha256local(generated)
        val mergedHash = sha256local(merged)
        val packagedReference = if (stripped != null && stripped.exists()) stripped else merged
        val packagedReferenceHash = sha256local(packagedReference)

        if (publishedHash != generatedHash) {
            throw GradleException(
                "Generated $conf libRemexCore.so is stale. published=$publishedHash, generated=$generatedHash"
            )
        }
        if (publishedHash != mergedHash) {
            throw GradleException(
                "Merged $conf libRemexCore.so is stale. published=$publishedHash, merged=$mergedHash"
            )
        }

        val apkHash = ZipFile(apk).use { zip ->
            val entry = zip.getEntry("lib/arm64-v8a/libRemexCore.so")
                ?: throw GradleException("${apk.name} does not contain lib/arm64-v8a/libRemexCore.so")
            zip.getInputStream(entry).use { input ->
                sha256localBytes(input.readBytes())
            }
        }

        if (apkHash != packagedReferenceHash) {
            throw GradleException(
                "$conf APK contains stale libRemexCore.so. packagedReference=$packagedReferenceHash, apk=$apkHash"
            )
        }
        if (apk.lastModified() < packagedReference.lastModified()) {
            throw GradleException(
                "$conf APK timestamp is older than packaged libRemexCore.so. apk=${apk.lastModified()}, packaged=${packagedReference.lastModified()}"
            )
        }

        println(
            "Verified $conf APK ${apk.name} with libRemexCore.so hash $publishedHash"
        )
    }
}

val verifyRemexCoreInDebugApk by project.tasks.registering(VerifyRemexCoreInApkTask::class) {
    group = "remex"
    description = "Verifies debug APK contains the latest libRemexCore.so built from Remex.Core"
    dependsOn("assembleDebug")
    configuration.set("Debug")
    rcDir.set(remexCoreProjectDirLocal.absolutePath)
    generatedArm64So.set(remexGeneratedDebugArm64So)
    mergedArm64So.set(mergedDebugArm64So)
    strippedArm64So.set(strippedDebugArm64So)
    apkDirectory.set(layout.buildDirectory.dir("outputs/apk/debug"))
}

val verifyRemexCoreInReleaseApk by project.tasks.registering(VerifyRemexCoreInApkTask::class) {
    group = "remex"
    description = "Verifies release APK contains the latest libRemexCore.so built from Remex.Core"
    dependsOn("assembleRelease")
    configuration.set("Release")
    rcDir.set(remexCoreProjectDirLocal.absolutePath)
    generatedArm64So.set(remexGeneratedReleaseArm64So)
    mergedArm64So.set(mergedReleaseArm64So)
    strippedArm64So.set(strippedReleaseArm64So)
    apkDirectory.set(layout.buildDirectory.dir("outputs/apk/release"))
}


val remexFreshAssembleDebug by project.tasks.registering {
    group = "remex"
    description =
        "Hard reset build for debug: clean Android build, assemble, and verify native freshness"
    dependsOn("clean")
    dependsOn("assembleDebug")
    dependsOn(verifyRemexCoreInDebugApk)
}

val remexFreshInstallDebug by project.tasks.registering {
    group = "remex"
    description = "Hard reset build for debug and install APK on a connected device"
    dependsOn(remexFreshAssembleDebug)
    dependsOn("remexUninstallExistingDebugApp")
    dependsOn("installDebug")
}

val remexFreshAssembleRelease by project.tasks.registering {
    group = "remex"
    description =
        "Hard reset build for release: clean Android build, assemble, bundle, and verify native freshness"
    dependsOn("clean")
    dependsOn("assembleRelease")
    dependsOn("bundleRelease")
    dependsOn(verifyRemexCoreInReleaseApk)
}

val remexPublishRelease by project.tasks.registering {
    group = "remex"
    description =
        "Bump version (versionCode+1, minor+1, patch→0), clean build release APK + AAB, and verify"
    dependsOn("clean")
    dependsOn("mergeReleaseAssets") // Force assets to be merged first
    dependsOn("assembleRelease")
    dependsOn("bundleRelease")
    dependsOn(verifyRemexCoreInReleaseApk)

    // Capture variables to be compatible with configuration cache
    val buildDirFile = layout.buildDirectory.get().asFile
    val currentVersionName = remexVersionName
    val currentVersionCode = remexVersionCode

    doLast {
        val apkDir = buildDirFile.resolve("outputs/apk/release")
        val aabDir = buildDirFile.resolve("outputs/bundle/release")
        val apk = apkDir.listFiles()
            ?.filter { it.extension.equals("apk", ignoreCase = true) }
            ?.maxByOrNull { it.lastModified() }
        val aab = aabDir.listFiles()
            ?.filter { it.extension.equals("aab", ignoreCase = true) }
            ?.maxByOrNull { it.lastModified() }

        println()
        println("═══════════════════════════════════════════════════════")
        println("  RemEx v$currentVersionName (versionCode=$currentVersionCode)")
        println("───────────────────────────────────────────────────────")
        if (apk != null) println("  APK: ${apk.absolutePath}")
        if (aab != null) println("  AAB: ${aab.absolutePath}")
        println("───────────────────────────────────────────────────────")
        println("  Upload the AAB to Google Play Console.")
        println("  version.properties has been updated — commit the change.")
        println("═══════════════════════════════════════════════════════")
    }
}

// Force EVERY task (except clean itself) to run after clean whenever clean is part of the build.
// The remexFreshAssemble* tasks declare dependsOn("clean") + dependsOn("assemble..."), but Gradle
// does not order those dependencies relative to each other, and a mustRunAfter("clean") on the
// top-level assemble task does NOT propagate to its hundreds of transitive AGP tasks
// (processDebugMainManifest, generateDebugGlobalSynthetics, the native sync/merge, etc.). Without
// this, clean runs concurrently with those tasks and deletes their inputs/outputs mid-build, which
// produced intermittent failures: vanished navigation.json, R8 "Compilation failed", "Generated
// libRemexCore.so not found", and "clean: Unable to delete directory (a process is still writing)".
// mustRunAfter is a soft ordering — inert when clean is not in the graph, so a plain
// `assembleDebug`/`assembleRelease` (no clean) is unaffected.
tasks.configureEach {
    if (name != "clean") {
        mustRunAfter("clean")
    }
}

tasks.matching { it.name == "installDebug" }.configureEach {
    mustRunAfter(remexFreshAssembleDebug)
    mustRunAfter("remexUninstallExistingDebugApp")
}

verifyRemexCoreInDebugApk.configure {
    mustRunAfter("assembleDebug")
}

verifyRemexCoreInReleaseApk.configure {
    mustRunAfter("assembleRelease")
}

val remexUninstallExistingDebugApp by project.tasks.registering {
    group = "remex"
    description =
        "Uninstalls existing app package from connected device to avoid signature conflicts"

    val sdkDir = androidSdkDir
    doLast {
        val isWindows = System.getProperty("os.name").contains("Windows", ignoreCase = true)
        val adbExecutable = if (isWindows) {
            File(sdkDir, "platform-tools/adb.exe")
        } else {
            File(sdkDir, "platform-tools/adb")
        }

        if (!adbExecutable.exists()) {
            throw GradleException("adb was not found at ${adbExecutable.absolutePath}")
        }

        val output = StringBuilder()
        val process = ProcessBuilder(
            adbExecutable.absolutePath,
            "uninstall",
            remexAndroidApplicationId
        )
            .redirectErrorStream(true)
            .start()

        process.inputStream.bufferedReader().use { reader ->
            reader.lineSequence().forEach { line ->
                output.appendLine(line)
                if (line.isNotBlank()) {
                    println(line)
                }
            }
        }

        process.waitFor(30, TimeUnit.SECONDS)
        // Give system monitors (like Samsung's npumanager) time to settle after uninstall
        // to avoid transient NameNotFoundException during rapid reinstall.
        Thread.sleep(1000)

        val exitCode = process.exitValue()
        val outputText = output.toString()
        val noDevice = outputText.contains("no devices/emulators found", ignoreCase = true)
        val unknownPackage = outputText.contains("Unknown package", ignoreCase = true)

        if (exitCode != 0 && !unknownPackage && !noDevice) {
            throw GradleException(
                "adb uninstall failed for $remexAndroidApplicationId with exit code $exitCode. Output: $outputText"
            )
        }
    }
}

remexUninstallExistingDebugApp.configure {
    mustRunAfter(remexFreshAssembleDebug)
}

tasks.matching { it.name == "mergeDebugNativeLibs" }.configureEach {
    dependsOn(syncRemexCoreDebugSo)
}

tasks.matching { it.name == "mergeDebugJniLibFolders" }.configureEach {
    dependsOn(syncRemexCoreDebugSo)
}

tasks.matching { it.name == "mergeReleaseNativeLibs" }.configureEach {
    dependsOn(syncRemexCoreReleaseSo)
}

tasks.matching { it.name == "mergeReleaseJniLibFolders" }.configureEach {
    dependsOn(syncRemexCoreReleaseSo)
}

dependencies {
    // Firebase
    implementation(platform("com.google.firebase:firebase-bom:34.15.0"))
    implementation("com.google.firebase:firebase-analytics")
    implementation("com.google.firebase:firebase-crashlytics-ndk")
    implementation("com.google.firebase:firebase-crashlytics")

    implementation(libs.material)
    implementation(libs.androidx.compose.ui.text.google.fonts)
    implementation(libs.androidx.graphics.path)
    implementation(libs.androidx.graphics.shapes)
    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.core.splashscreen)
    implementation(libs.tink.android)
    implementation(libs.okhttp)
    implementation(libs.androidx.lifecycle.runtime.ktx)
    implementation(libs.androidx.lifecycle.viewmodel.compose)
    implementation(libs.androidx.lifecycle.runtime.compose)
    implementation(libs.androidx.activity.compose)
    implementation(libs.androidx.compose.ui)
    implementation(libs.androidx.compose.ui.graphics)
    implementation(libs.androidx.compose.ui.tooling.preview)
    implementation(libs.androidx.compose.material3)
    implementation(libs.androidx.compose.material3.adaptive.navigation.suite)
    implementation(libs.androidx.compose.material3.adaptive)
    implementation(libs.androidx.compose.material3.adaptive.layout)
    implementation(libs.androidx.compose.material3.adaptive.navigation)
    implementation(libs.androidx.compose.material.icons.extended)
    implementation(libs.androidx.navigation.compose)
    implementation(libs.androidx.datastore.preferences)
    implementation(libs.androidx.glance.appwidget)
    implementation(libs.androidx.glance.material3)
    implementation(libs.mlkit.barcode.scanning)
    implementation(libs.androidx.camera.camera2)
    implementation(libs.androidx.camera.lifecycle)
    implementation(libs.androidx.camera.view)
    testImplementation(libs.junit)
    testImplementation("org.mockito:mockito-core:5.23.0")
    testImplementation("org.mockito.kotlin:mockito-kotlin:6.3.0")
    // org.json is an Android stub in unit tests; use the real implementation
    testImplementation("org.json:json:20260522")
    androidTestImplementation(libs.androidx.junit)
    androidTestImplementation(libs.androidx.espresso.core)
    androidTestImplementation(libs.androidx.compose.ui.test.junit4)
    // ui-tooling must be implementation (not debugImplementation) for Preview classloading in this environment
    implementation(libs.androidx.compose.ui.tooling)
    debugImplementation(libs.androidx.compose.ui.test.manifest)
}

android.buildTypes.getByName("release").configure<com.google.firebase.crashlytics.buildtools.gradle.CrashlyticsExtension> {
    nativeSymbolUploadEnabled = true
    unstrippedNativeLibsDir = layout.buildDirectory.dir("intermediates/merged_native_libs/release/mergeReleaseNativeLibs/out/lib").get().asFile.path
}
