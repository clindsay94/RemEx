plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.compose)
}

import org.gradle.api.tasks.Exec
import org.gradle.api.GradleException
import java.util.Properties
import java.security.MessageDigest
import java.util.zip.ZipFile
import java.util.concurrent.TimeUnit
import java.util.concurrent.TimeoutException

android {
    namespace = "com.clindsay94.remex"
    compileSdk = 36

    defaultConfig {
        applicationId = "com.clindsay94.remex"
        minSdk = 26
        targetSdk = 36
        versionCode = 1
        versionName = "1.0"

        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
    }

    buildTypes {
        release {
            isMinifyEnabled = false
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
    buildFeatures {
        compose = true
        viewBinding = true
    }
    packaging {
        jniLibs {
            useLegacyPackaging = false
        }
    }
    buildToolsVersion = "37.0.0 rc2"
    ndkVersion = "29.0.14206865"

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

val repoRootDir = rootProject.projectDir.parentFile
val remexCoreProjectDir = File(repoRootDir, "Remex.Core")
val remexGeneratedDebugJniRoot = layout.buildDirectory.get().asFile.resolve("generated/remexJniLibs/debug")
val remexGeneratedReleaseJniRoot = layout.buildDirectory.get().asFile.resolve("generated/remexJniLibs/release")
val androidNdkVersion = "29.0.14206865"
val androidLocalProperties = Properties().apply {
    rootProject.file("local.properties").inputStream().use(::load)
}
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

fun sha256(bytes: ByteArray): String = MessageDigest.getInstance("SHA-256").digest(bytes).toHexString()

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

    return apks.firstOrNull() ?: throw GradleException("No APK was found in ${directory.absolutePath}")
}

val arm64PublishedSo = { configuration: String ->
    val publishPath = File(
        remexCoreProjectDir,
        "bin/$configuration/net10.0-android/android-arm64/publish/libRemexCore.so"
    )
    val nativePath = File(
        remexCoreProjectDir,
        "bin/$configuration/net10.0-android/android-arm64/native/libRemexCore.so"
    )

    listOf(nativePath, publishPath)
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
        "-p:AndroidNdkDirectory=$androidNdkDirForMsbuild"
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
        "-p:AndroidNdkDirectory=$androidNdkDirForMsbuild"
    )
}

val syncRemexCoreDebugSo by tasks.registering {
    group = "remex"
    description = "Publishes and synchronizes debug libRemexCore.so into generated jniLibs"
    dependsOn(publishRemexCoreAndroidDebug)
    outputs.file(remexGeneratedDebugArm64So)

    doLast {
        val source = requireExistingFile(arm64PublishedSo("Debug"), "Published debug libRemexCore.so")
        remexGeneratedDebugArm64So.parentFile.mkdirs()
        source.copyTo(remexGeneratedDebugArm64So, overwrite = true)

        val sourceHash = sha256(source)
        val destHash = sha256(remexGeneratedDebugArm64So)
        if (sourceHash != destHash) {
            throw GradleException(
                "Debug native library hash mismatch after sync. source=$sourceHash, destination=$destHash"
            )
        }
        if (remexGeneratedDebugArm64So.lastModified() < source.lastModified()) {
            throw GradleException(
                "Debug generated libRemexCore.so is older than published source. source=${source.lastModified()}, destination=${remexGeneratedDebugArm64So.lastModified()}"
            )
        }

        logger.lifecycle(
            "Synced debug libRemexCore.so => ${remexGeneratedDebugArm64So.absolutePath} (sha256=$destHash)"
        )
    }
}

val syncRemexCoreReleaseSo by tasks.registering {
    group = "remex"
    description = "Publishes and synchronizes release libRemexCore.so into generated jniLibs"
    dependsOn(publishRemexCoreAndroidRelease)
    outputs.file(remexGeneratedReleaseArm64So)

    doLast {
        val source = requireExistingFile(arm64PublishedSo("Release"), "Published release libRemexCore.so")
        remexGeneratedReleaseArm64So.parentFile.mkdirs()
        source.copyTo(remexGeneratedReleaseArm64So, overwrite = true)

        val sourceHash = sha256(source)
        val destHash = sha256(remexGeneratedReleaseArm64So)
        if (sourceHash != destHash) {
            throw GradleException(
                "Release native library hash mismatch after sync. source=$sourceHash, destination=$destHash"
            )
        }
        if (remexGeneratedReleaseArm64So.lastModified() < source.lastModified()) {
            throw GradleException(
                "Release generated libRemexCore.so is older than published source. source=${source.lastModified()}, destination=${remexGeneratedReleaseArm64So.lastModified()}"
            )
        }

        logger.lifecycle(
            "Synced release libRemexCore.so => ${remexGeneratedReleaseArm64So.absolutePath} (sha256=$destHash)"
        )
    }
}

val verifyRemexCoreInDebugApk by tasks.registering {
    group = "remex"
    description = "Verifies debug APK contains the latest libRemexCore.so built from Remex.Core"
    dependsOn("assembleDebug")

    doLast {
        val published = requireExistingFile(arm64PublishedSo("Debug"), "Published debug libRemexCore.so")
        val generated = requireExistingFile(remexGeneratedDebugArm64So, "Generated debug libRemexCore.so")
        val merged = requireExistingFile(mergedDebugArm64So, "Merged debug libRemexCore.so")
        val debugApk = latestApkIn(layout.buildDirectory.get().asFile.resolve("outputs/apk/debug"))

        val publishedHash = sha256(published)
        val generatedHash = sha256(generated)
        val mergedHash = sha256(merged)
        val packagedReference = if (strippedDebugArm64So.exists()) strippedDebugArm64So else merged
        val packagedReferenceHash = sha256(packagedReference)

        if (publishedHash != generatedHash) {
            throw GradleException(
                "Generated debug libRemexCore.so is stale. published=$publishedHash, generated=$generatedHash"
            )
        }
        if (publishedHash != mergedHash) {
            throw GradleException(
                "Merged debug libRemexCore.so is stale. published=$publishedHash, merged=$mergedHash"
            )
        }

        val apkHash = ZipFile(debugApk).use { zip ->
            val entry = zip.getEntry("lib/arm64-v8a/libRemexCore.so")
                ?: throw GradleException("${debugApk.name} does not contain lib/arm64-v8a/libRemexCore.so")
            zip.getInputStream(entry).use { input ->
                sha256(input.readBytes())
            }
        }

        if (apkHash != packagedReferenceHash) {
            throw GradleException(
                "Debug APK contains stale libRemexCore.so. packagedReference=$packagedReferenceHash, apk=$apkHash"
            )
        }
        if (debugApk.lastModified() < packagedReference.lastModified()) {
            throw GradleException(
                "Debug APK timestamp is older than packaged libRemexCore.so. apk=${debugApk.lastModified()}, packaged=${packagedReference.lastModified()}"
            )
        }

        logger.lifecycle(
            "Verified debug APK ${debugApk.name} with libRemexCore.so hash $publishedHash"
        )
    }
}

val verifyRemexCoreInReleaseApk by tasks.registering {
    group = "remex"
    description = "Verifies release APK contains the latest libRemexCore.so built from Remex.Core"
    dependsOn("assembleRelease")

    doLast {
        val published = requireExistingFile(arm64PublishedSo("Release"), "Published release libRemexCore.so")
        val generated = requireExistingFile(remexGeneratedReleaseArm64So, "Generated release libRemexCore.so")
        val merged = requireExistingFile(mergedReleaseArm64So, "Merged release libRemexCore.so")
        val releaseApk = latestApkIn(layout.buildDirectory.get().asFile.resolve("outputs/apk/release"))

        val publishedHash = sha256(published)
        val generatedHash = sha256(generated)
        val mergedHash = sha256(merged)
        val packagedReference = if (strippedReleaseArm64So.exists()) strippedReleaseArm64So else merged
        val packagedReferenceHash = sha256(packagedReference)

        if (publishedHash != generatedHash) {
            throw GradleException(
                "Generated release libRemexCore.so is stale. published=$publishedHash, generated=$generatedHash"
            )
        }
        if (publishedHash != mergedHash) {
            throw GradleException(
                "Merged release libRemexCore.so is stale. published=$publishedHash, merged=$mergedHash"
            )
        }

        val apkHash = ZipFile(releaseApk).use { zip ->
            val entry = zip.getEntry("lib/arm64-v8a/libRemexCore.so")
                ?: throw GradleException("${releaseApk.name} does not contain lib/arm64-v8a/libRemexCore.so")
            zip.getInputStream(entry).use { input ->
                sha256(input.readBytes())
            }
        }

        if (apkHash != packagedReferenceHash) {
            throw GradleException(
                "Release APK contains stale libRemexCore.so. packagedReference=$packagedReferenceHash, apk=$apkHash"
            )
        }
        if (releaseApk.lastModified() < packagedReference.lastModified()) {
            throw GradleException(
                "Release APK timestamp is older than packaged libRemexCore.so. apk=${releaseApk.lastModified()}, packaged=${packagedReference.lastModified()}"
            )
        }

        logger.lifecycle(
            "Verified release APK ${releaseApk.name} with libRemexCore.so hash $publishedHash"
        )
    }
}

val purgeRepoBinObj by tasks.registering {
    group = "remex"
    description = "Deletes every bin/ and obj/ directory under the repository root"

    doLast {
        val isWindows = System.getProperty("os.name").contains("Windows", ignoreCase = true)
        if (isWindows) {
            logger.lifecycle("Force-stopping dotnet and RemEx desktop processes before purge.")
            fun runWindowsKill(vararg args: String) {
                val process = ProcessBuilder(*args)
                    .redirectErrorStream(true)
                    .start()
                process.inputStream.bufferedReader().use { reader ->
                    reader.lineSequence().forEach { line ->
                        if (line.isNotBlank()) {
                            logger.lifecycle(line)
                        }
                    }
                }
                process.waitFor(10, TimeUnit.SECONDS)
            }

            runWindowsKill("cmd", "/c", "taskkill", "/F", "/IM", "dotnet.exe", "/T")
            runWindowsKill("cmd", "/c", "taskkill", "/F", "/IM", "Remex.Client.Desktop.exe", "/T")
            Thread.sleep(1000)
        }

        val currentPid = ProcessHandle.current().pid()
        val repoPath = repoRootDir.absolutePath
        val processesToStop = ProcessHandle
            .allProcesses()
            .filter { process -> process.pid() != currentPid }
            .filter { process ->
                val commandLine = process.info().commandLine().orElse("")
                commandLine.contains("dotnet", ignoreCase = true) &&
                    commandLine.contains(repoPath, ignoreCase = true)
            }
            .toList()

        if (processesToStop.isNotEmpty()) {
            logger.lifecycle("Stopping ${processesToStop.size} repo-scoped dotnet process(es) before purge.")
            processesToStop.forEach { process ->
                val commandLine = process.info().commandLine().orElse("<unknown>")
                process.destroy()
                try {
                    process.onExit().get(8, TimeUnit.SECONDS)
                } catch (_: TimeoutException) {
                    process.destroyForcibly()
                    process.onExit().get(8, TimeUnit.SECONDS)
                }
                logger.lifecycle("Stopped PID ${process.pid()} :: $commandLine")
            }
        }

        val protectedRoots = listOf(
            File(repoRootDir, ".git").canonicalPath,
            File(repoRootDir, ".gradle").canonicalPath
        )

        val candidates = repoRootDir
            .walkTopDown()
            .filter {
                it.isDirectory && (it.name.equals("bin", ignoreCase = true) || it.name.equals("obj", ignoreCase = true))
            }
            .filter { directory ->
                val canonical = directory.canonicalPath
                protectedRoots.none { protected ->
                    canonical == protected || canonical.startsWith("$protected${File.separator}")
                }
            }
            .toList()
            .sortedByDescending { it.absolutePath.length }

        if (candidates.isEmpty()) {
            logger.lifecycle("No bin/obj directories found under ${repoRootDir.absolutePath}.")
            return@doLast
        }

        fun deleteWithRetry(directory: File): Boolean {
            val delays = listOf(0L, 200L, 500L, 1000L, 1500L)
            delays.forEach { delayMs ->
                if (delayMs > 0L) {
                    Thread.sleep(delayMs)
                }
                project.delete(directory)
                if (!directory.exists()) {
                    return true
                }
            }
            return !directory.exists()
        }

        candidates.forEach { directory ->
            if (!deleteWithRetry(directory)) {
                throw GradleException(
                    "Failed to delete ${directory.absolutePath}. Ensure no external process is locking files under this directory."
                )
            }
            logger.lifecycle("Deleted ${directory.relativeTo(repoRootDir)}")
        }

        logger.lifecycle("Deleted ${candidates.size} bin/obj directories under ${repoRootDir.absolutePath}.")
    }
}

val remexFreshAssembleDebug by tasks.registering {
    group = "remex"
    description = "Hard reset build for debug: purge bin/obj, clean Android build, assemble, and verify native freshness"
    dependsOn(purgeRepoBinObj)
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
    description = "Hard reset build for release: purge bin/obj, clean Android build, assemble, and verify native freshness"
    dependsOn(purgeRepoBinObj)
    dependsOn("clean")
    dependsOn("assembleRelease")
    dependsOn(verifyRemexCoreInReleaseApk)
}

tasks.named("clean").configure {
    mustRunAfter(purgeRepoBinObj)
}

tasks.matching { it.name == "assembleDebug" }.configureEach {
    mustRunAfter("clean")
}

tasks.matching { it.name == "assembleRelease" }.configureEach {
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
    description = "Uninstalls existing app package from connected device to avoid signature conflicts"

    doLast {
        val isWindows = System.getProperty("os.name").contains("Windows", ignoreCase = true)
        val adbExecutable = if (isWindows) {
            File(androidSdkDir, "platform-tools/adb.exe")
        } else {
            File(androidSdkDir, "platform-tools/adb")
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
                    logger.lifecycle(line)
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
    implementation(libs.androidx.graphics.path)
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
    testImplementation(libs.junit)
    androidTestImplementation(libs.androidx.junit)
    androidTestImplementation(libs.androidx.espresso.core)
    androidTestImplementation(platform(libs.androidx.compose.bom))
    androidTestImplementation(libs.androidx.compose.ui.test.junit4)
    debugImplementation(libs.androidx.compose.ui.tooling)
    debugImplementation(libs.androidx.compose.ui.test.manifest)
}