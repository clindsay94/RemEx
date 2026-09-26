# Preserve .NET Android Crypto Proxies
-keep class net.dot.android.crypto.** { *; }

# Preserve Remex JNI Bridge
-keep class com.clindsay94.remex.RemexCoreClient { *; }
-keep class com.clindsay94.remex.RemexCoreClient$RemexCallback { *; }
-keep class com.clindsay94.remex.RemexClientManager { *; }

# ML Kit keep rules
-keep class com.google.mlkit.** { *; }
-keep class com.google.android.gms.internal.mlkit_vision_barcode.** { *; }
-keep class com.google.android.gms.internal.mlkit_common.** { *; }
-keep class com.google.android.datatransport.** { *; }

# Prevent R8 from stripping App Startup components
-keep class androidx.startup.** { *; }
-keep class androidx.work.** { *; }
-keep class androidx.glance.** { *; }
-keep class androidx.lifecycle.ProcessLifecycleInitializer { *; }

# Keep all implementations of androidx.startup.Initializer
-keep class * implements androidx.startup.Initializer {
    <init>();
}

# Glance widget tap callbacks (live-check A1/A3). actionRunCallback<T>() stores T's class NAME and
# Glance instantiates it reflectively via getDeclaredConstructor().newInstance(). Glance's own
# consumer rule keeps the class but not its constructor, and R8 full mode (AGP 8+ default) strips the
# unreferenced <init>() - so every widget tap threw inside Glance's receiver, which logs and swallows
# it, and the tap did nothing. Release usage.txt listed "<init>()" as removed for all three callbacks.
-keep class * implements androidx.glance.appwidget.action.ActionCallback {
    <init>();
}

# Keep pairing native bridge methods
-keepclassmembers class com.clindsay94.remex.RemexCoreClient {
    public static native <methods>;
    private static native <methods>;
}
-keepclassmembers class com.clindsay94.remex.security.PinnedHostStore { *; }


# All RemexCallback methods are resolved from native code via JNI GetMethodID.
-keepclassmembers class com.clindsay94.remex.RemexCoreClient$RemexCallback {
    <methods>;
}

# Perf audit P3-9: Log.d/Log.v calls survived into the release APK, including frame-sampled debug
# logging that fires several times a second while streaming. R8 strips the ENTIRE call (including
# evaluating its arguments) when a method is declared to have no side effects, so a call built from
# an interpolated string still costs nothing once this is in effect - not just "the log line is
# silent", the string concatenation itself never runs. Deliberately excludes Log.i/w/e: those are
# genuine diagnostics that should survive into a release build (e.g. reachable in a bug report),
# only the two verbosity levels nobody reads outside active development are stripped.
-assumenosideeffects class android.util.Log {
    public static int d(...);
    public static int v(...);
}
