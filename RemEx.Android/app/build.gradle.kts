import com.android.build.api.variant.VariantOutputConfiguration
import com.android.build.api.variant.impl.VariantOutputImpl
import java.security.MessageDigest
import java.util.Properties
import java.util.zip.ZipFile

plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.compose)
}

val repoRootDir: File = rootProject.projectDir.parentFile
val remexCoreProjectDirLocal = File(repoRootDir, "Remex.Core")
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
        parts[1] = (parts[1].toInt() + 1).toString()
        parts[2] = "0"
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
    namespace = "com.clindsay94.remex"
    compileSdk = 36

    defaultConfig {
        applicationId = "com.clindsay94.remex"
        minSdk = 26
        targetSdk = 36
        versionCode = remexVersionCode
        versionName = remexVersionName

        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
    }

    signingConfigs {
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
            signingConfig = signingConfigs.getByName("release")
            proguardFiles(
                getDefaultProguardFile("proguard-android-optimize.txt"),
                "proguard-rules.pro"
            )
        }
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_11
        targetCompatibility = JavaVersion.VERSION_11
    }

    kotlin {
        compilerOptions {
            jvmTarget.set(org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_11)
            freeCompilerArgs.addAll("-Xannotation-default-target=param-property")
        }
    }
    buildFeatures {
        compose = true
        viewBinding = true
    }
    packaging {
        jniLibs {
            useLegacyPackaging = false
        }
    }
    buildToolsVersion = "36.0.0"
    ndkVersion = "30.0.14904198"

    sourceSets {
        getByName("main") {
            // Keep packaging deterministic by ignoring src/main/jniLibs for Remex.Core.
            jniLibs.directories.clear()
        }
        getByName("debug") {
            jniLibs.directories.clear()
            jniLibs.directories.add(
                layout.buildDirectory.get().asFile.resolve("generated/remexJniLibs/debug").absolutePath
            )
        }
        getByName("release") {
            jniLibs.directories.clear()
            jniLibs.directories.add(
                layout.buildDirectory.get().asFile.resolve("generated/remexJniLibs/release").absolutePath
            )
        }
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
val remexAndroidApplicationId = "com.clindsay94.remex"

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

fun getArm64PublishedSoPath(remexCoreProjectDir: File, configuration: String): File {
    val publishPath = File(
        remexCoreProjectDir,
        "bin/$configuration/net10.0-android/android-arm64/publish/libRemexCore.so"
    )
    val nativePath = File(
        remexCoreProjectDir,
        "bin/$configuration/net10.0-android/android-arm64/native/libRemexCore.so"
    )

    return listOf(nativePath, publishPath)
        .filter { it.exists() }
        .maxByOrNull { it.lastModified() }
        ?: nativePath
}

val publishRemexCoreAndroidDebug by tasks.registering(Exec::class) {
    group = "remex"
    description = "Builds and links Remex.Core Android arm64 debug native library"
    workingDir = repoRootDir
    commandLine(
        "dotnet",
        "msbuild",
        "Remex.Core/Remex.Core.csproj",
        "-restore",
        "-t:Build,LinkNative",
        "-p:Configuration=Debug",
        "-p:TargetFramework=net10.0-android",
        "-p:RuntimeIdentifier=android-arm64",
        "-p:AndroidSdkDirectory=$androidSdkDir",
        "-p:AndroidNdkDirectory=$androidNdkDirForMsbuild",
        "-p:JavaSdkDirectory=${System.getProperty("java.home")}"
    )
}

val publishRemexCoreAndroidRelease by tasks.registering(Exec::class) {
    group = "remex"
    description = "Builds and links Remex.Core Android arm64 release native library"
    workingDir = repoRootDir
    commandLine(
        "dotnet",
        "msbuild",
        "Remex.Core/Remex.Core.csproj",
        "-restore",
        "-t:Build,LinkNative",
        "-p:Configuration=Release",
        "-p:TargetFramework=net10.0-android",
        "-p:RuntimeIdentifier=android-arm64",
        "-p:AndroidSdkDirectory=$androidSdkDir",
        "-p:AndroidNdkDirectory=$androidNdkDirForMsbuild",
        "-p:JavaSdkDirectory=${System.getProperty("java.home")}"
    )
}


abstract class SyncRemexCoreSoTask : DefaultTask() {
    @get:Input
    abstract val configuration: Property<String>

    @get:Input
    abstract val rcDir: Property<String>

    @get:OutputFile
    abstract val generatedSo: RegularFileProperty

    @TaskAction
    fun doSync() {
        val conf = configuration.get()
        val generated = generatedSo.get().asFile
        val rcDirFile = File(rcDir.get())

        val publishPath = File(
            rcDirFile,
            "bin/$conf/net10.0-android/android-arm64/publish/libRemexCore.so"
        )
        val nativePath = File(
            rcDirFile,
            "bin/$conf/net10.0-android/android-arm64/native/libRemexCore.so"
        )

        val source = listOf(nativePath, publishPath)
            .filter { it.exists() }
            .maxByOrNull { it.lastModified() }
            ?: nativePath

        if (!source.exists()) {
            throw GradleException("Published $conf libRemexCore.so not found: ${source.absolutePath}")
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

val syncRemexCoreDebugSo by tasks.registering(SyncRemexCoreSoTask::class) {
    group = "remex"
    description = "Publishes and synchronizes debug libRemexCore.so into generated jniLibs"
    dependsOn(publishRemexCoreAndroidDebug)

    configuration.set("Debug")
    rcDir.set(remexCoreProjectDirLocal.absolutePath)
    generatedSo.set(remexGeneratedDebugArm64So)
}

val syncRemexCoreReleaseSo by tasks.registering(SyncRemexCoreSoTask::class) {
    group = "remex"
    description = "Publishes and synchronizes release libRemexCore.so into generated jniLibs"
    dependsOn(publishRemexCoreAndroidRelease)

    configuration.set("Release")
    rcDir.set(remexCoreProjectDirLocal.absolutePath)
    generatedSo.set(remexGeneratedReleaseArm64So)
}

abstract class VerifyRemexCoreInApkTask : DefaultTask() {
    @get:Input
    abstract val configuration: Property<String>

    @get:Input
    abstract val rcDir: Property<String>

    @get:InputFile
    abstract val generatedArm64So: RegularFileProperty

    @get:InputFile
    abstract val mergedArm64So: RegularFileProperty

    @get:Internal
    abstract val strippedArm64So: RegularFileProperty

    @get:InputDirectory
    abstract val apkDirectory: DirectoryProperty

    @TaskAction
    fun verify() {
        val conf = configuration.get()
        val rcDirFile = File(rcDir.get())
        val generated = generatedArm64So.get().asFile
        val merged = mergedArm64So.get().asFile
        val stripped = strippedArm64So.asFile.orNull
        val apkDir = apkDirectory.get().asFile

        val publishPath = File(
            rcDirFile,
            "bin/$conf/net10.0-android/android-arm64/publish/libRemexCore.so"
        )
        val nativePath = File(
            rcDirFile,
            "bin/$conf/net10.0-android/android-arm64/native/libRemexCore.so"
        )

        val published = listOf(nativePath, publishPath)
            .filter { it.exists() }
            .maxByOrNull { it.lastModified() }
            ?: nativePath

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

val verifyRemexCoreInDebugApk by tasks.registering(VerifyRemexCoreInApkTask::class) {
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

val verifyRemexCoreInReleaseApk by tasks.registering(VerifyRemexCoreInApkTask::class) {
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


val remexFreshAssembleDebug by tasks.registering {
    group = "remex"
    description =
        "Hard reset build for debug: clean Android build, assemble, and verify native freshness"
    dependsOn("clean")
    dependsOn("assembleDebug")
    dependsOn(verifyRemexCoreInDebugApk)
}

val remexFreshInstallDebug by tasks.registering {
    group = "remex"
    description = "Hard reset build for debug and install APK on a connected device"
    dependsOn(remexFreshAssembleDebug)
    dependsOn("remexUninstallExistingDebugApp")
    dependsOn("installDebug")
}

val remexFreshAssembleRelease by tasks.registering {
    group = "remex"
    description =
        "Hard reset build for release: clean Android build, assemble, bundle, and verify native freshness"
    dependsOn("clean")
    dependsOn("assembleRelease")
    dependsOn("bundleRelease")
    dependsOn(verifyRemexCoreInReleaseApk)
}

val remexPublishRelease by tasks.registering {
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

tasks.matching { it.name == "assembleDebug" }.configureEach {
    mustRunAfter("clean")
}


tasks.matching { it.name == "assembleRelease" }.configureEach {
    mustRunAfter("clean")
}

tasks.matching { it.name == "bundleRelease" }.configureEach {
    mustRunAfter("clean")
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

val remexUninstallExistingDebugApp by tasks.registering {
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
    implementation(libs.material)
    implementation(libs.androidx.compose.ui.text.google.fonts)
    implementation(libs.androidx.graphics.path)
    implementation(libs.androidx.graphics.shapes)
    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.lifecycle.runtime.ktx)
    implementation(libs.androidx.lifecycle.viewmodel.compose)
    implementation(libs.androidx.activity.compose)
    implementation(platform(libs.androidx.compose.bom))
    implementation(libs.androidx.compose.ui)
    implementation(libs.androidx.compose.ui.graphics)
    implementation(libs.androidx.compose.ui.tooling.preview)
    implementation(libs.androidx.compose.material3)
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
    androidTestImplementation(libs.androidx.junit)
    androidTestImplementation(libs.androidx.espresso.core)
    androidTestImplementation(platform(libs.androidx.compose.bom))
    androidTestImplementation(libs.androidx.compose.ui.test.junit4)
    debugImplementation(libs.androidx.compose.ui.tooling)
    debugImplementation(libs.androidx.compose.ui.test.manifest)
}
