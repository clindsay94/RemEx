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
