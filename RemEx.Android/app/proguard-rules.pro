# Preserve .NET Android Crypto Proxies
-keep class net.dot.android.crypto.** { *; }

# Preserve Remex JNI Bridge
-keep class com.clindsay94.remex.RemexCoreClient { *; }
-keep class com.clindsay94.remex.RemexCoreClient$RemexCallback { *; }
-keep class com.clindsay94.remex.RemexClientManager { *; }

# Preserve standard JNI functions and classes used by .NET
-keep class java.lang.** { *; }
-keep class java.net.** { *; }
-keep class javax.net.** { *; }

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

# Keep NSec.Cryptography (cryptography library can be reflectively invoked)
-keep class com.nsec.** { *; }

# Keep pairing native bridge methods
-keepclassmembers class com.clindsay94.remex.RemexCoreClient {
    public static native <methods>;
    private static native <methods>;
}
-keepclassmembers class com.clindsay94.remex.security.PinnedHostStore { *; }
-keepclassmembers class com.clindsay94.remex.security.PairingViewModel { *; }

# Keep file-transfer native bridge callbacks
-keepclassmembers class com.clindsay94.remex.RemexCoreClient$RemexCallback {
    void onDesktopStreamDescriptor(java.lang.String);
    void onFileTransferProgress(java.lang.String);
    void onFileTransferComplete(java.lang.String);
}

