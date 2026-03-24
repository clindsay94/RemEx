plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.compose)
}

import org.gradle.api.tasks.Exec
import org.gradle.api.tasks.Copy
import java.util.Properties

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
        getByName("debug") {
            jniLibs.srcDir(layout.buildDirectory.get().asFile.resolve("generated/remexJniLibs/debug"))
        }
        getByName("release") {
            jniLibs.srcDir(layout.buildDirectory.get().asFile.resolve("generated/remexJniLibs/release"))
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
val arm64PublishedSo = { configuration: String ->
    File(
        remexCoreProjectDir,
        "bin/$configuration/net10.0-android/android-arm64/publish/libRemexCore.so"
    )
}

val publishRemexCoreAndroidDebug by tasks.registering(Exec::class) {
    group = "remex"
    description = "Publishes Remex.Core Android arm64 debug native library"
    workingDir = repoRootDir
    commandLine(
        "dotnet",
        "publish",
        "Remex.Core/Remex.Core.csproj",
        "-c",
        "Debug",
        "-f",
        "net10.0-android",
        "-r",
        "android-arm64",
        "-p:AndroidNdkDirectory=$androidNdkDirForMsbuild"
    )
}

val publishRemexCoreAndroidRelease by tasks.registering(Exec::class) {
    group = "remex"
    description = "Publishes Remex.Core Android arm64 release native library"
    workingDir = repoRootDir
    commandLine(
        "dotnet",
        "publish",
        "Remex.Core/Remex.Core.csproj",
        "-c",
        "Release",
        "-f",
        "net10.0-android",
        "-r",
        "android-arm64",
        "-p:AndroidNdkDirectory=$androidNdkDirForMsbuild"
    )
}

val syncRemexCoreDebugSo by tasks.registering(Copy::class) {
    group = "remex"
    description = "Copies published debug libRemexCore.so into generated jniLibs"
    dependsOn(publishRemexCoreAndroidDebug)

    from(arm64PublishedSo("Debug"))
    into(File(remexGeneratedDebugJniRoot, "arm64-v8a"))
    rename { "libRemexCore.so" }
}

val syncRemexCoreReleaseSo by tasks.registering(Copy::class) {
    group = "remex"
    description = "Copies published release libRemexCore.so into generated jniLibs"
    dependsOn(publishRemexCoreAndroidRelease)

    from(arm64PublishedSo("Release"))
    into(File(remexGeneratedReleaseJniRoot, "arm64-v8a"))
    rename { "libRemexCore.so" }
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