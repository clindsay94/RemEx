// Top-level build file where you can add configuration options common to all sub-projects/modules.
plugins {
    alias(libs.plugins.android.application) apply false
    alias(libs.plugins.kotlin.compose) apply false
    alias(libs.plugins.kotlin.android) apply false
    id("com.google.gms.google-services") version "4.4.4" apply false
    id("com.google.firebase.crashlytics") version "3.0.2" apply false
}


tasks.register("remexFreshAssembleDebug") {
    group = "remex"
    description = "Hard reset debug build with native freshness verification"
    dependsOn(":app:remexFreshAssembleDebug")
}

tasks.register("remexFreshInstallDebug") {
    group = "remex"
    description = "Hard reset debug build, verify native freshness, then install on device"
    dependsOn(":app:remexFreshInstallDebug")
}

tasks.register("remexFreshAssembleRelease") {
    group = "remex"
    description = "Hard reset release build with native freshness verification"
    dependsOn(":app:remexFreshAssembleRelease")
}

tasks.register("remexVerifyDebugApkFreshness") {
    group = "remex"
    description = "Verifies debug APK embeds the latest published libRemexCore.so"
    dependsOn(":app:verifyRemexCoreInDebugApk")
}

tasks.register("remexVerifyReleaseApkFreshness") {
    group = "remex"
    description = "Verifies release APK embeds the latest published libRemexCore.so"
    dependsOn(":app:verifyRemexCoreInReleaseApk")
}
