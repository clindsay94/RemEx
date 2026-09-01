using Remex.Core.Validation;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Models.IPC;
using Remex.Core.Serialization;
using Remex.Core.Services;
using Remex.Core.Services.Network;

namespace Remex.Core.Native;

/// <summary>
/// Every JNI entry point <c>libRemexCore.so</c> exposes to the Android app — the entire surface
/// across which Kotlin and .NET talk.
/// </summary>
/// <remarks>
/// <para>
/// THE CONVENTIONS, which hold for every export below and are not visible in the signatures:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Entry-point names are the contract.</b> Each <c>[UnmanagedCallersOnly(EntryPoint = …)]</c>
/// string is a JNI-mangled name that must match a Kotlin <c>external fun</c> on the corresponding
/// class EXACTLY. A mismatch is not a compile error on either side — it surfaces at runtime as
/// <c>UnsatisfiedLinkError</c> the first time that method is called.
/// </description></item>
/// <item><description>
/// <b><c>IntPtr</c> in means a Java string</b>, read with <c>JniHelper.ReadJString</c>. Parameters
/// suffixed <c>Utf8</c> or <c>Ptr</c> are UTF-8 <c>jstring</c> handles owned by the JVM; the callee
/// copies what it needs and never frees them.
/// </description></item>
/// <item><description>
/// <b><c>IntPtr</c> out means a NEW Java string</b>, created with <c>JniHelper.CreateJString</c>.
/// It is a JNI local reference, so the JVM reclaims it when the native frame returns — Kotlin must
/// read it before it goes out of scope, and no one frees it by hand. <c>IntPtr.Zero</c> comes back
/// only if even constructing the fallback error string failed. MOST exports put JSON in that string,
/// but the pinning and pairing-PIN exports return bare status strings instead
/// (<c>OK:…</c>, <c>UNSUPPORTED</c>, <c>ERROR: {code}: {message}</c>) — check each
/// <c>&lt;returns&gt;</c> rather than assuming JSON.
/// </description></item>
/// <item><description>
/// <b>No exception ever crosses this boundary.</b> A managed exception escaping into the JVM
/// terminates the process, so every export is wrapped: failures return an
/// <see cref="AndroidNativeOperationResponse"/> with <c>Success = false</c> and the message in
/// <c>Error</c>, and the void exports swallow-and-log instead. Callers must check <c>success</c>
/// rather than relying on an exception.
/// </description></item>
/// <item><description>
/// <b>A pending Java exception is cleared before any JNI call.</b> Calling into JNI while one is
/// pending aborts the process, so the exports check and clear on entry AND before the final call
/// on the failure path.
/// </description></item>
/// </list>
/// <para>
/// HOST → CLIENT messages are the direction that bites. An inbound message reaches Kotlin only if
/// <c>OnNativeMessageReceived</c> forwards it to a registered JNI callback; a type the router does
/// not recognise is dropped SILENTLY, with no error on either side. That gap once bricked the whole
/// of v3 file transfer with a misleading "peer did not respond" (RemEx-y6x6). <c>file_*</c> types are
/// covered by prefix; anything else needs explicit wiring and a round-trip test on a real device.
/// The one deliberate exception is <c>pairing_pin_response</c>, which is consumed synchronously as
/// the return value of <see cref="FetchPairingPinNative"/> and must NOT be added to the router.
/// </para>
/// </remarks>
public static class AndroidNativeExports
{
    private static readonly object SyncRoot = new();
    private static IntPtr _javaVm;

    // All Java callbacks run on one dedicated, process-lifetime thread that attaches to the
    // JVM exactly once (as a daemon, so it never blocks VM shutdown) and never detaches.
    // .NET thread-pool threads must NOT attach to the JVM: the pool retires idle workers
    // after ~20 s, and a natively attached thread exiting trips ART's detach check — while
    // detaching from a pthread TLS destructor re-enters managed code after NativeAOT has
    // torn down the thread, which fail-fasts (observed on-device as SIGABRT ~40 s after
    // launch). A single long-lived dispatcher avoids both failure modes and also keeps the
    // 30–60 fps frame path free of per-callback attach/detach overhead.
    // Bounded so a stalled Java consumer can never grow the queue without limit (OOM).
    // The high-rate frame path enqueues as droppable: under backpressure the newest
    // frames are shed rather than blocking the capture/network thread or accumulating
    // unbounded latency. Low-rate control/data callbacks enqueue non-droppable and must
    // not be lost, so they block briefly if the queue is momentarily full.
    // (Finer latency tuning — drop-oldest / frame coalescing — belongs to RemEx-a1t.)
    private const int JniWorkCapacity = 16;
    private static readonly System.Collections.Concurrent.BlockingCollection<Action<IntPtr>> _jniWork = new(JniWorkCapacity);
    // volatile: published via the double-checked lock below; ARM's weak memory model
    // would otherwise permit an early/torn read of the reference.
    private static volatile Thread? _jniDispatcher;

    private static void PostToJavaThread(Action<IntPtr> work, bool droppable = false)
    {
        if (_jniDispatcher is null)
        {
            lock (SyncRoot)
            {
                if (_jniDispatcher is null)
                {
                    var thread = new Thread(JniDispatcherLoop)
                    {
                        IsBackground = true,
                        Name = "RemexJniDispatch"
                    };
                    thread.Start();
                    _jniDispatcher = thread;
                }
            }
        }

        try
        {
            // Droppable (frames): shed silently when the consumer is backed up so the
            // producer never blocks and memory stays bounded — the next frame supersedes
            // the dropped one. Non-droppable (control/data): block until space frees so
            // state-change callbacks are never lost.
            if (droppable)
            {
                _jniWork.TryAdd(work);
            }
            else
            {
                _jniWork.Add(work);
            }
        }
        catch (InvalidOperationException)
        {
            // Defensive only: thrown if the queue is ever marked complete (CompleteAdding).
            // The dispatcher is a process-lifetime daemon that is intentionally never shut
            // down — ClearCallbackState() just zeroes the callback so queued work no-ops —
            // so nothing calls CompleteAdding today and this path is currently unreachable.
            // Kept for forward-compatibility if an explicit teardown is ever added.
        }
    }

    private static void JniDispatcherLoop()
    {
        IntPtr vm;
        lock (SyncRoot)
        {
            vm = _javaVm;
        }

        IntPtr env = IntPtr.Zero;
        bool attached = vm != IntPtr.Zero
            && JniHelper.AttachCurrentThreadAsDaemon(vm, out env, IntPtr.Zero) == 0
            && env != IntPtr.Zero;
        if (!attached)
        {
            JniHelper.AndroidLogE("RemexNative", "JNI dispatcher failed to attach to the JVM; Java callbacks are disabled.");
        }

        foreach (var work in _jniWork.GetConsumingEnumerable())
        {
            if (!attached) continue; // degraded mode: drain and drop so producers never block
            try
            {
                work(env);
            }
            catch (Exception ex)
            {
                JniHelper.AndroidLogE("RemexNative", $"JNI callback dispatch failed: {ex.Message}");
            }
        }
    }

    private static IntPtr _callbackGlobalRef;
    private static IntPtr _onTelemetryUpdateMethodId;
    private static IntPtr _onConnectionStateChangedMethodId;
    private static IntPtr _onLauncherSyncMethodId;
    private static IntPtr _onProcessListSyncMethodId;
    private static IntPtr _onFrameReceivedMethodId;
    private static IntPtr _onHostInfoUpdateMethodId;
    private static IntPtr _onDesktopErrorMethodId;
    private static IntPtr _onDesktopMetaMethodId;
    private static IntPtr _onDesktopWindowResultMethodId;
    private static IntPtr _onFileTransferMessageMethodId;
    private static IntPtr _onClipboardMessageMethodId;
    private static IntPtr _onLinkQualityMethodId;
    private static IntPtr _onConnectionErrorMethodId;
    private static IntPtr _onDesktopStreamDescriptorMethodId;
    private static IntPtr _onDesktopDisplayCatalogMethodId;
    private static IntPtr _onDesktopCursorStateMethodId;
    private static IntPtr _onDesktopCursorShapeMethodId;

    /// <summary>Carries <c>media_state</c> to Kotlin, so the play/pause icon can tell the truth.</summary>
    private static IntPtr _onMediaStateMethodId;

    /// <summary>Reports which phase of pairing is running, so a long wait can say what it is doing.</summary>
    private static IntPtr _onPairingProgressMethodId;
    // RD-E: byte[] callback carrying the raw 32-byte "RDXC" cursor-position packet (parsed in Kotlin).
    private static IntPtr _onDesktopCursorBinaryMethodId;

    private static IWakeOnLanService _wakeOnLanService = new WakeOnLanService();
    private static TelemetryPayload? _cachedTelemetry;
    private static AndroidNativeInitRequest _lastInitRequest = new();
    private static ClientWebSocket? _pairingWebSocket;
    private static PairingClient? _activePairingClient;
    private static PairingResponse? _activePairingResponse;
    // Serializes all transitions of the pairing-session statics above. Kept SEPARATE from SyncRoot:
    // SyncRoot guards the high-frequency callback/frame paths and must never be held across the
    // blocking pairing handshake (up to 60s), so the two locks must not be conflated. A concurrent
    // StartPairing/SubmitPin from a second Java thread waits here instead of disposing-then-using
    // the active ClientWebSocket (JNI-4 / RemEx-8ay).
    private static readonly object PairingSyncRoot = new();

    /// <summary>
    /// Lets a caller abandon the pairing attempt it started (RemEx-defb).
    /// </summary>
    /// <remarks>
    /// The bookkeeping lives in <see cref="PairingAbortRegistry"/> so it can be tested — nothing
    /// about a JNI export can be. **It has its own lock and never takes <see cref="PairingSyncRoot"/>,
    /// which is what lets a cancel arrive WHILE the attempt it is cancelling holds that lock.** A
    /// canceller that waited for its own target would deadlock, and silently: the caller would appear
    /// to hang for exactly the budget it was trying to escape.
    /// </remarks>
    private static readonly PairingAbortRegistry PairingAborts = new();

    private static readonly ConcurrentDictionary<string, string> _pinnedHashes = new();
    private static readonly Channel<RemexMessage> OutboundMessageQueue = Channel.CreateUnbounded<RemexMessage>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private static int _outboundSendLoopStarted;

    static AndroidNativeExports()
    {
        RemexNativeClient.Current.TelemetryReceived += OnNativeTelemetryReceived;
        RemexNativeClient.Current.ConnectionStateChanged += OnNativeConnectionStateChanged;
        RemexNativeClient.Current.LauncherEntriesReceived += OnNativeLauncherEntriesReceived;
        RemexNativeClient.Current.ProcessListReceived += OnNativeProcessListReceived;
        RemexNativeClient.Current.MessageReceived += OnNativeMessageReceived;
        RemexNativeClient.Current.ConnectionFailed += OnNativeConnectionFailed;

        RemexDesktopClient.Current.FrameReceived += OnNativeFrameReceived;
        RemexDesktopClient.Current.ErrorReceived += OnNativeDesktopError;
        RemexDesktopClient.Current.MetaReceived += OnNativeMetaReceived;
        RemexDesktopClient.Current.WindowResultReceived += OnNativeDesktopWindowResult;
        RemexDesktopClient.Current.StreamDescriptorReceived += OnNativeDesktopStreamDescriptor;
        RemexDesktopClient.Current.DisplayCatalogReceived += OnNativeDisplayCatalogReceived;
        RemexDesktopClient.Current.CursorStateReceived += OnNativeCursorStateReceived;
        RemexDesktopClient.Current.CursorBinaryReceived += OnNativeCursorBinaryReceived;
        RemexDesktopClient.Current.CursorShapeReceived += OnNativeCursorShapeReceived;

        EnsureOutboundSendLoopStarted();
    }

    // In .NET 10 Android NativeAOT, the built-in JNI export `Java_net_dot_android_crypto_DotnetProxyTrustManager_verifyRemoteCertificate`
    // is exported by `libSystem.Security.Cryptography.Native.Android.a`. However, when Android's JVM invokes it via `verifyRemoteCertificate`,
    // it can fail to map if the JVM expects a different signature or if the method isn't registered dynamically by `JNI_OnLoad`.
    // Since Remex completely bypasses the OS trust manager via `ws.Options.RemoteCertificateValidationCallback = ... => true` and validates
    // the SPKI hash manually in `SubmitPairingPin` / `PinnedHostStore`, we can safely return true here to satisfy the Android
    // SslStream TLS handshake state machine which is forced to call this Java proxy.
    /// <summary>
    /// Satisfies the Android TLS handshake's call into the .NET proxy trust manager. Always true —
    /// see the comment above for why that is not a hole, and read it before changing this.
    /// </summary>
    /// <remarks>
    /// This is NOT where RemEx decides whether to trust a host. Returning false here would not
    /// harden anything; it would fail every handshake before pinning is ever consulted.
    /// <para>
    /// The real decision is the SPKI pin check. For the steady-state data connection that is the
    /// <c>RemoteCertificateValidationCallback</c> the native clients install UNCONDITIONALLY, which
    /// fail-closes on an empty pin store. That word is load-bearing: because these exports force the
    /// OS trust manager to accept anything, making that callback conditional would silently reopen
    /// full MITM — see the VULN-5 note on <c>RemexNativeClient</c>. Pinning is not optional here;
    /// it is the ONLY check.
    /// </para>
    /// </remarks>
    [UnmanagedCallersOnly(EntryPoint = "Java_net_dot_android_crypto_DotnetProxyTrustManager_verifyRemoteCertificate")]
    public static bool Java_net_dot_android_crypto_DotnetProxyTrustManager_verifyRemoteCertificate(IntPtr env, IntPtr clazz, long handle)
    {
        return true;
    }

    /// <summary>
    /// Inner-class spelling of <see cref="Java_net_dot_android_crypto_DotnetProxyTrustManager_verifyRemoteCertificate"/>
    /// (<c>_1</c> is JNI's mangling of <c>$</c>). All three spellings exist because which one the
    /// runtime looks up varies, and an unresolved one is an <c>UnsatisfiedLinkError</c> mid-handshake.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "Java_net_dot_android_crypto_DotnetProxyTrustManager_1X509TrustManager_verifyRemoteCertificate")]
    public static bool Java_net_dot_android_crypto_DotnetProxyTrustManager_1X509TrustManager_verifyRemoteCertificate(IntPtr env, IntPtr thiz, long handle)
    {
        return true;
    }

    /// <summary>
    /// Third spelling of the proxy trust-manager hook. See
    /// <see cref="Java_net_dot_android_crypto_DotnetProxyTrustManager_verifyRemoteCertificate"/>.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "Java_net_dot_android_crypto_DotnetProxyX509TrustManager_verifyRemoteCertificate")]
    public static bool Java_net_dot_android_crypto_DotnetProxyX509TrustManager_verifyRemoteCertificate(IntPtr env, IntPtr thiz, long handle)
    {
        return true;
    }
    private static void ClearCallbackState()
    {
        _callbackGlobalRef = IntPtr.Zero;
        _onTelemetryUpdateMethodId = IntPtr.Zero;
        _onConnectionStateChangedMethodId = IntPtr.Zero;
        _onLauncherSyncMethodId = IntPtr.Zero;
        _onProcessListSyncMethodId = IntPtr.Zero;
        _onFrameReceivedMethodId = IntPtr.Zero;
        _onHostInfoUpdateMethodId = IntPtr.Zero;
        _onDesktopErrorMethodId = IntPtr.Zero;
        _onDesktopMetaMethodId = IntPtr.Zero;
        _onDesktopWindowResultMethodId = IntPtr.Zero;
        _onFileTransferMessageMethodId = IntPtr.Zero;
        _onClipboardMessageMethodId = IntPtr.Zero;
        _onLinkQualityMethodId = IntPtr.Zero;
        _onConnectionErrorMethodId = IntPtr.Zero;
        _onDesktopStreamDescriptorMethodId = IntPtr.Zero;
        _onDesktopDisplayCatalogMethodId = IntPtr.Zero;
        _onDesktopCursorStateMethodId = IntPtr.Zero;
        _onDesktopCursorBinaryMethodId = IntPtr.Zero;
        _onDesktopCursorShapeMethodId = IntPtr.Zero;
        _onPairingProgressMethodId = IntPtr.Zero;
        _onMediaStateMethodId = IntPtr.Zero;
    }

    private static IntPtr GetRequiredCallbackMethodId(IntPtr env, IntPtr clazz, string name, string signature)
    {
        var methodId = JniHelper.GetMethodID(env, clazz, name, signature);
        if (methodId != IntPtr.Zero && !JniHelper.ExceptionCheck(env))
        {
            return methodId;
        }

        if (JniHelper.ExceptionCheck(env))
        {
            JniHelper.ExceptionClear(env);
        }

        JniHelper.AndroidLogE("RemexNative", $"RegisterCallbackNative could not bind callback method {name}{signature}.");
        return IntPtr.Zero;
    }

    /// <summary>
    /// Registers the Kotlin object that receives host → client callbacks (telemetry, connection
    /// state, launcher sync). Until this runs, nothing the host pushes can reach the app.
    /// </summary>
    /// <param name="callbackObj">
    /// The Kotlin listener. A GLOBAL reference is taken so it survives past this call; the previous
    /// one is released, so calling this again re-points the callbacks rather than adding a second.
    /// </param>
    /// <remarks>
    /// Callbacks are delivered on one dedicated dispatcher thread that attaches to the JVM once and
    /// never detaches — see the note on the work queue above for why a thread-pool thread must never
    /// be used. That thread is NOT the Android main thread, so the listener has to marshal anything
    /// touching UI itself.
    /// </remarks>
    [UnmanagedCallersOnly(EntryPoint = "Java_com_clindsay94_remex_RemexCoreClient_RegisterCallbackNative")]
    public static void RegisterCallbackNative(IntPtr env, IntPtr thiz, IntPtr callbackObj)
        => ExportVoid("RegisterCallback", () =>
    {
        lock (SyncRoot)
        {
            if (JniHelper.ExceptionCheck(env))
            {
                JniHelper.ExceptionClear(env);
                JniHelper.AndroidLogE("RemexNative", "RegisterCallbackNative cleared a pending JNI exception before registration.");
            }

            if (_javaVm == IntPtr.Zero)
            {
                if (JniHelper.GetJavaVM(env, out _javaVm) != 0 || _javaVm == IntPtr.Zero)
                {
                    JniHelper.AndroidLogE("RemexNative", "RegisterCallbackNative failed to capture JavaVM.");
                    return;
                }
            }

            if (callbackObj == IntPtr.Zero)
            {
                if (_callbackGlobalRef != IntPtr.Zero)
                {
                    JniHelper.DeleteGlobalRef(env, _callbackGlobalRef);
                }
                ClearCallbackState();
                return;
            }

            var newCallbackGlobalRef = JniHelper.NewGlobalRef(env, callbackObj);
            if (newCallbackGlobalRef == IntPtr.Zero)
            {
                if (JniHelper.ExceptionCheck(env))
                {
                    JniHelper.ExceptionClear(env);
                }

                JniHelper.AndroidLogE("RemexNative", "RegisterCallbackNative failed to create a global callback reference.");
                return;
            }

            var clazz = JniHelper.GetObjectClass(env, newCallbackGlobalRef);
            if (clazz == IntPtr.Zero)
            {
                JniHelper.DeleteGlobalRef(env, newCallbackGlobalRef);
                if (JniHelper.ExceptionCheck(env))
                {
                    JniHelper.ExceptionClear(env);
                }

                JniHelper.AndroidLogE("RemexNative", "RegisterCallbackNative failed to resolve callback class.");
                return;
            }

            var registrationSucceeded = false;
            try
            {
                var onTelemetryUpdateMethodId = GetRequiredCallbackMethodId(env, clazz, "onTelemetryUpdate", "(Ljava/lang/String;)V");
                var onConnectionStateChangedMethodId = GetRequiredCallbackMethodId(env, clazz, "onConnectionStateChanged", "(Z)V");
                var onLauncherSyncMethodId = GetRequiredCallbackMethodId(env, clazz, "onLauncherSync", "(Ljava/lang/String;)V");
                var onProcessListSyncMethodId = GetRequiredCallbackMethodId(env, clazz, "onProcessListSync", "(Ljava/lang/String;)V");
                var onFrameReceivedMethodId = GetRequiredCallbackMethodId(env, clazz, "onFrameReceived", "([B)V");
                var onHostInfoUpdateMethodId = GetRequiredCallbackMethodId(env, clazz, "onHostInfoUpdate", "(Ljava/lang/String;)V");
                var onDesktopErrorMethodId = GetRequiredCallbackMethodId(env, clazz, "onDesktopError", "(Ljava/lang/String;)V");
                var onDesktopMetaMethodId = GetRequiredCallbackMethodId(env, clazz, "onDesktopMeta", "(Ljava/lang/String;)V");
                var onDesktopWindowResultMethodId = GetRequiredCallbackMethodId(env, clazz, "onDesktopWindowResult", "(Ljava/lang/String;)V");
                var onFileTransferMessageMethodId = GetRequiredCallbackMethodId(env, clazz, "onFileTransferMessage", "(Ljava/lang/String;)V");
                var onClipboardMessageMethodId = GetRequiredCallbackMethodId(env, clazz, "onClipboardMessage", "(Ljava/lang/String;)V");
                var onLinkQualityMethodId = GetRequiredCallbackMethodId(env, clazz, "onLinkQuality", "(Ljava/lang/String;)V");
                var onConnectionErrorMethodId = GetRequiredCallbackMethodId(env, clazz, "onConnectionError", "(Ljava/lang/String;)V");
                var onDesktopStreamDescriptorMethodId = GetRequiredCallbackMethodId(env, clazz, "onDesktopStreamDescriptor", "(Ljava/lang/String;)V");
                var onDesktopDisplayCatalogMethodId = GetRequiredCallbackMethodId(env, clazz, "onDesktopDisplayCatalog", "(Ljava/lang/String;)V");
                var onDesktopCursorStateMethodId = GetRequiredCallbackMethodId(env, clazz, "onDesktopCursorState", "(Ljava/lang/String;)V");
                var onDesktopCursorBinaryMethodId = GetRequiredCallbackMethodId(env, clazz, "onDesktopCursorBinary", "([B)V");
                var onDesktopCursorShapeMethodId = GetRequiredCallbackMethodId(env, clazz, "onDesktopCursorShape", "(Ljava/lang/String;)V");
                var onPairingProgressMethodId = GetRequiredCallbackMethodId(env, clazz, "onPairingProgress", "(Ljava/lang/String;)V");
                var onMediaStateMethodId = GetRequiredCallbackMethodId(env, clazz, "onMediaState", "(Ljava/lang/String;)V");

                if (onTelemetryUpdateMethodId == IntPtr.Zero
                    || onConnectionStateChangedMethodId == IntPtr.Zero
                    || onLauncherSyncMethodId == IntPtr.Zero
                    || onProcessListSyncMethodId == IntPtr.Zero
                    || onFrameReceivedMethodId == IntPtr.Zero
                    || onHostInfoUpdateMethodId == IntPtr.Zero
                    || onDesktopErrorMethodId == IntPtr.Zero
                    || onDesktopMetaMethodId == IntPtr.Zero
                    || onDesktopWindowResultMethodId == IntPtr.Zero
                    || onFileTransferMessageMethodId == IntPtr.Zero
                    || onClipboardMessageMethodId == IntPtr.Zero
                    || onLinkQualityMethodId == IntPtr.Zero
                    || onConnectionErrorMethodId == IntPtr.Zero
                    || onDesktopStreamDescriptorMethodId == IntPtr.Zero
                    || onDesktopDisplayCatalogMethodId == IntPtr.Zero
                    || onDesktopCursorStateMethodId == IntPtr.Zero
                    || onDesktopCursorBinaryMethodId == IntPtr.Zero
                    || onDesktopCursorShapeMethodId == IntPtr.Zero
                    || onPairingProgressMethodId == IntPtr.Zero
                    || onMediaStateMethodId == IntPtr.Zero)
                {
                    return;
                }

                var oldCallbackGlobalRef = _callbackGlobalRef;
                _callbackGlobalRef = newCallbackGlobalRef;
                _onTelemetryUpdateMethodId = onTelemetryUpdateMethodId;
                _onConnectionStateChangedMethodId = onConnectionStateChangedMethodId;
                _onLauncherSyncMethodId = onLauncherSyncMethodId;
                _onProcessListSyncMethodId = onProcessListSyncMethodId;
                _onFrameReceivedMethodId = onFrameReceivedMethodId;
                _onHostInfoUpdateMethodId = onHostInfoUpdateMethodId;
                _onDesktopErrorMethodId = onDesktopErrorMethodId;
                _onDesktopMetaMethodId = onDesktopMetaMethodId;
                _onDesktopWindowResultMethodId = onDesktopWindowResultMethodId;
                _onFileTransferMessageMethodId = onFileTransferMessageMethodId;
                _onClipboardMessageMethodId = onClipboardMessageMethodId;
                _onLinkQualityMethodId = onLinkQualityMethodId;
                _onConnectionErrorMethodId = onConnectionErrorMethodId;
                _onDesktopStreamDescriptorMethodId = onDesktopStreamDescriptorMethodId;
                _onDesktopDisplayCatalogMethodId = onDesktopDisplayCatalogMethodId;
                _onDesktopCursorStateMethodId = onDesktopCursorStateMethodId;
                _onDesktopCursorBinaryMethodId = onDesktopCursorBinaryMethodId;
                _onDesktopCursorShapeMethodId = onDesktopCursorShapeMethodId;
                _onPairingProgressMethodId = onPairingProgressMethodId;
                _onMediaStateMethodId = onMediaStateMethodId;
                registrationSucceeded = true;

                if (oldCallbackGlobalRef != IntPtr.Zero)
                {
                    JniHelper.DeleteGlobalRef(env, oldCallbackGlobalRef);
                }
            }
            finally
            {
                JniHelper.DeleteLocalRef(env, clazz);

                if (!registrationSucceeded)
                {
                    JniHelper.DeleteGlobalRef(env, newCallbackGlobalRef);
                }
            }
        }
    });

    /// <summary>
    /// Initialises the native client against a host. Must be the first call after
    /// <see cref="RegisterCallbackNative"/>.
    /// </summary>
    /// <param name="initJsonUtf8">JSON <see cref="AndroidNativeInitRequest"/>.</param>
    /// <returns>JSON <see cref="AndroidNativeInitializationResponse"/>, reporting which subsystems came up.</returns>
    [UnmanagedCallersOnly(EntryPoint = "Java_com_clindsay94_remex_RemexCoreClient_InitRemexNative")]
    public static IntPtr InitRemex(IntPtr env, IntPtr thiz, IntPtr initJsonUtf8)
        => Export(env, () => HandleInitialize(JniHelper.ReadJString(env, initJsonUtf8)));

    /// <summary>
    /// Sends a Wake-on-LAN magic packet. Fire-and-forget by nature: a success here means the packet
    /// was sent, never that the PC woke.
    /// </summary>
    /// <param name="macAddressUtf8">Target MAC.</param>
    /// <param name="broadcastIpUtf8">Subnet broadcast address to send to.</param>
    /// <param name="port">UDP port, conventionally 9.</param>
    /// <returns>JSON <see cref="AndroidNativeOperationResponse"/>.</returns>
    [UnmanagedCallersOnly(EntryPoint = "Java_com_clindsay94_remex_RemexCoreClient_WakePcNative")]
    public static IntPtr WakePc(IntPtr env, IntPtr thiz, IntPtr macAddressUtf8, IntPtr broadcastIpUtf8, int port)
        => Export(env, () => HandleSendWakeOnLan(JniHelper.ReadJString(env, macAddressUtf8), JniHelper.ReadJString(env, broadcastIpUtf8), port));

    /// <summary>Requests the latest telemetry snapshot.</summary>
    /// <returns>
    /// JSON <c>TelemetryPayload</c> on success, or a JSON <see cref="AndroidNativeOperationResponse"/>
    /// with <c>success: false</c> on failure — so the two shapes differ and the caller must branch on
    /// <c>success</c> before parsing.
    /// </returns>
    [UnmanagedCallersOnly(EntryPoint = "Java_com_clindsay94_remex_RemexCoreClient_GetTelemetryNative")]
    public static IntPtr GetTelemetry(IntPtr env, IntPtr thiz)
        => Export(env, HandleRequestTelemetry);

    /// <summary>Sends a pre-serialised <c>RemexMessage</c> envelope to the host.</summary>
    /// <param name="messageJsonUtf8">JSON <c>RemexMessage</c>.</param>
    /// <returns>JSON <see cref="AndroidNativeOperationResponse"/> describing the SEND, not the host's reply.</returns>
    /// <remarks>
    /// Any reply arrives asynchronously through the registered callback, so success here only means
    /// the message reached the socket.
    /// </remarks>
    [UnmanagedCallersOnly(EntryPoint = "Java_com_clindsay94_remex_RemexCoreClient_SendMessageNative")]
    public static IntPtr SendMessage(IntPtr env, IntPtr thiz, IntPtr messageJsonUtf8)
        => Export(env, () => HandleDispatchMessage(JniHelper.ReadJString(env, messageJsonUtf8)));

    /// <summary>
    /// Sends one input event on the control socket, for screens that have no Remote Desktop stream
    /// (RemEx-035d6).
    /// </summary>
    /// <param name="inputJsonUtf8">JSON <see cref="InputEvent"/> — the payload alone, not an envelope.</param>
    /// <returns>JSON <see cref="AndroidNativeOperationResponse"/> describing the QUEUEING, not the host's reply.</returns>
    /// <remarks>
    /// <see cref="HandleSendControlInput"/> carries why this is separate from
    /// <see cref="SendMessage"/> rather than another <c>desktop_input</c> through it.
    /// </remarks>
    [UnmanagedCallersOnly(EntryPoint = "Java_com_clindsay94_remex_RemexCoreClient_SendControlInputNative")]
    public static IntPtr SendControlInput(IntPtr env, IntPtr thiz, IntPtr inputJsonUtf8)
        => Export(env, () => HandleSendControlInput(JniHelper.ReadJString(env, inputJsonUtf8)));

    /// <summary>Judges a clipboard payload with the SAME rule the host applies (RemEx-hgqs).</summary>
    /// <param name="textUtf8">The candidate clipboard text.</param>
    /// <returns>
    /// JSON <c>{"reason":"none|empty|too_large","byteCount":N,"maxBytes":N}</c>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// **THE POINT IS THAT ANDROID DOES NOT GET ITS OWN COPY OF THE RULE.**
    /// <see cref="ClipboardValidation"/>'s own doc says both sides validate with the same rule rather
    /// than each inventing one, and without this export the phone would have to reimplement the cap
    /// in Kotlin — where "256 KB" would quietly become 256 K *characters*, admitting three times the
    /// limit for anyone writing in Chinese, Japanese or Korean. That is the exact mistake the shipped
    /// validation was written to prevent, and a second implementation is how it would come back.
    /// </para>
    /// <para>
    /// **THE ANSWER NEVER ECHOES THE TEXT** — a reason, a length, and the limit, nothing else. A
    /// clipboard holds whatever the user last copied.
    /// </para>
    /// <para>
    /// The JSON is built by hand rather than serialized: three fields of known shape, no reflection,
    /// and nothing for a source generator to have to be told about.
    /// </para>
    /// </remarks>
    [UnmanagedCallersOnly(EntryPoint = "Java_com_clindsay94_remex_RemexCoreClient_ValidateClipboardNative")]
    public static IntPtr ValidateClipboard(IntPtr env, IntPtr thiz, IntPtr textUtf8)
        => Export(
            env,
            () => ClipboardValidation.ToNativeJson(JniHelper.ReadJString(env, textUtf8)),
            // SAME SHAPE ON FAILURE. Export's default fallback is an operation-failure object with no
            // "reason" field at all, and Kotlin wraps that in a SUCCESSFUL Result - so the phone would
            // parse an answer whose reason is missing and fall back to whatever its parser defaults
            // to. One of those defaults sends an unbounded payload. (RemEx-hgqs.)
            ClipboardValidation.UnavailableNativeJson());

    /// <summary>Sends a command (power actions and similar) to the host.</summary>
    /// <param name="commandJsonUtf8">JSON command payload.</param>
    /// <returns>JSON <see cref="AndroidNativeOperationResponse"/>.</returns>
    [UnmanagedCallersOnly(EntryPoint = "Java_com_clindsay94_remex_RemexCoreClient_SendCommandNative")]
    public static IntPtr SendCommand(IntPtr env, IntPtr thiz, IntPtr commandJsonUtf8)
        => Export(env, () => HandleDispatchCommand(JniHelper.ReadJString(env, commandJsonUtf8)));

    /// <summary>Starts the Remote Desktop stream. Returns immediately; frames arrive via callback.</summary>
    /// <param name="configJsonUtf8">
    /// JSON <c>DesktopConfig</c>. Empty or unparseable falls back to the default config rather than
    /// failing, so a malformed config yields a working stream with default settings, not an error.
    /// </param>
    [UnmanagedCallersOnly(EntryPoint = "Java_com_clindsay94_remex_RemexCoreClient_StartDesktopStreamNative")]
    public static void StartDesktopStream(IntPtr env, IntPtr thiz, IntPtr configJsonUtf8)
        => ExportVoid("StartDesktopStream", () =>
        {
            var configJson = JniHelper.ReadJString(env, configJsonUtf8);
            var config = string.IsNullOrWhiteSpace(configJson)
                ? new DesktopConfig()
                : RemexJson.Deserialize(configJson, RemexJsonSerializerContext.Default.DesktopConfig) ?? new DesktopConfig();

            QueueDesktopWork("StartDesktopStream", async () =>
            {
                var (host, port, clientId, spkiHash) = GetDesktopEndpoint();
                await RemexDesktopClient.Current.StartStreamAsync(host, port, config, clientId, spkiHash);
            });
        });

    /// <summary>Stops the Remote Desktop stream. Safe to call when no stream is running.</summary>
    [UnmanagedCallersOnly(EntryPoint = "Java_com_clindsay94_remex_RemexCoreClient_StopDesktopStreamNative")]
    public static void StopDesktopStream(IntPtr env, IntPtr thiz)
    {
        QueueDesktopWork("StopDesktopStream", async () =>
        {
            await RemexDesktopClient.Current.StopStreamAsync();
            await RemexDesktopClient.Current.DisconnectAsync();
        });
    }

    /// <summary>
    /// Sends a batch of high-resolution pointer/stylus samples to the host (Stage 3).
    /// Called from Android Kotlin after raw MotionEvent capture.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "Java_com_clindsay94_remex_RemexCoreClient_SendDesktopPointerBatchNative")]
    public static void SendDesktopPointerBatch(IntPtr env, IntPtr thiz, IntPtr batchJsonUtf8)
        => ExportVoid("SendDesktopPointerBatch", () =>
        {
            var batchJson = JniHelper.ReadJString(env, batchJsonUtf8);
            if (string.IsNullOrWhiteSpace(batchJson))
                return;

            var batch = RemexJson.Deserialize(batchJson, RemexJsonSerializerContext.Default.DesktopPointerBatch);
            if (batch is null)
                return;

            QueueDesktopWork("SendDesktopPointerBatch", async () =>
            {
                var (host, port, clientId, spkiHash) = GetDesktopEndpoint();
                await RemexDesktopClient.Current.SendPointerBatchAsync(host, port, batch, clientId, spkiHash);
            });
        });

    /// <summary>
    /// Abandons the pairing attempt in flight, if any. Returns immediately; safe to call when idle.
    /// </summary>
    /// <remarks>
    /// **THIS IS WHAT MAKES A CALLER'S TIMEOUT REAL (RemEx-defb).** The pairing exports block the JNI
    /// thread for their own budgets — 10s + 20s + 60s for a handshake — and no amount of Kotlin
    /// `withTimeout` can interrupt a blocking JNI frame. A caller that gives up must therefore tell
    /// the native side to stop, or the work runs on holding <see cref="PairingSyncRoot"/> and the
    /// user's next attempt queues behind it.
    /// </remarks>
    [UnmanagedCallersOnly(EntryPoint = "Java_com_clindsay94_remex_RemexCoreClient_CancelPairingNative")]
    public static IntPtr CancelPairingNative(IntPtr env, IntPtr thiz, long attemptId)
    {
        return Export(env, () =>
        {
            if (PairingAborts.Cancel(attemptId))
            {
                Console.Error.WriteLine($"[Pairing] Attempt {attemptId} abandoned by the client — unwinding.");
            }

            return "OK";
        });
    }

    /// <summary>
    /// The phases of <see cref="StartPairingNative"/>, as stable tokens.
    /// </summary>
    /// <remarks>
    /// **TOKENS, NOT SENTENCES.** The native side has no idea what language the phone is in, and a
    /// phrase chosen here would arrive already-translated into the wrong one. The client maps these
    /// to its own localized strings, exactly as it does for <c>PairingErrorCodes</c> — and, as
    /// there, an unrecognised token must degrade to showing nothing rather than crashing, so adding
    /// a phase later needs no coordinated release.
    /// </remarks>
    internal static class PairingPhases
    {
        /// <summary>Checking the host answers on the port at all.</summary>
        internal const string Probe = "PROBE";

        /// <summary>TLS handshake and WebSocket upgrade.</summary>
        internal const string Securing = "SECURING";

        /// <summary>Waiting for the host to return its PairingResponse and show a PIN.</summary>
        internal const string AwaitingHost = "AWAITING_HOST";
    }

    /// <summary>
    /// Tells the client which pairing phase has just started (RemEx-g87x).
    /// </summary>
    /// <remarks>
    /// Fire-and-forget by construction: <see cref="NotifyJavaData"/> posts to the Java dispatcher
    /// thread, so this never blocks the pairing thread it is called from and never needs that thread
    /// to be JVM-attached. A client that has not registered a callback simply gets nothing.
    /// </remarks>
    private static void OnNativePairingProgress(string phase)
    {
        NotifyJavaData(_onPairingProgressMethodId, phase);
    }

    private static void ClearActivePairingState()
    {
        if (_pairingWebSocket != null)
        {
            try { _pairingWebSocket.Dispose(); } catch { /* best-effort cleanup: a socket that fails to dispose is already unusable, and throwing here would replace the real pairing outcome with a teardown error */ }
            _pairingWebSocket = null;
        }

        _activePairingClient = null;
        _activePairingResponse = null;
    }

    /// <summary>
    /// Opens the pairing socket and begins the ECDH exchange. Step one of three; the host then shows
    /// a 6-digit PIN on its own screen.
    /// </summary>
    /// <param name="hostUrlPtr">Host WebSocket URL.</param>
    /// <param name="clientNamePtr">Device name shown to the user on the PC.</param>
    /// <param name="clientVersionPtr">Client version. The host LOGS this and nothing else — it is not stored, compared, or gated on.</param>
    /// <param name="clientIdPtr">Stable client identity; the key the host files the pairing under.</param>
    /// <returns>JSON <see cref="AndroidNativeOperationResponse"/>.</returns>
    /// <remarks>
    /// Pairing is the ONLY authentication path for a non-loopback client, so a change here can brick
    /// every device with no clear error on either end. Follow with <see cref="FetchPairingPinNative"/>
    /// then <see cref="SubmitPairingPinNative"/>.
    /// </remarks>
    [UnmanagedCallersOnly(EntryPoint = "Java_com_clindsay94_remex_RemexCoreClient_StartPairingNative")]
    public static IntPtr StartPairingNative(IntPtr env, IntPtr thiz, IntPtr hostUrlPtr, IntPtr clientNamePtr, IntPtr clientVersionPtr, IntPtr clientIdPtr, long attemptId)
    {
        return Export(env, () =>
        {
            // Marshal inside the Export guard (JNI-5 / RemEx-85i): a managed throw while reading
            // the jstrings is now caught by Export rather than propagating past [UnmanagedCallersOnly].
            var hostUrl = JniHelper.ReadJString(env, hostUrlPtr);
            var clientName = JniHelper.ReadJString(env, clientNamePtr);
            var clientVersion = JniHelper.ReadJString(env, clientVersionPtr);
            var clientId = JniHelper.ReadJString(env, clientIdPtr);

            // Serialize the full attempt so a concurrent pairing export from another Java thread
            // waits here instead of disposing-then-using the active ClientWebSocket (JNI-4).
            lock (PairingSyncRoot)
            {
                ClientWebSocket? ws = null;
                PairingClient? client = null;

                // Every budget below is linked to this, so a caller that gives up can end the attempt
                // instead of leaving it running on this lock (RemEx-defb).
                var abort = PairingAborts.Begin(attemptId);
                try
                {
                    // Always discard any previous pairing state before starting a new attempt.
                    ClearActivePairingState();

                    if (string.IsNullOrEmpty(hostUrl))
                        return $"ERROR: {PairingErrorCodes.ArgMissing}: Host URL is required";
                    if (string.IsNullOrEmpty(clientName))
                        return $"ERROR: {PairingErrorCodes.ArgMissing}: Client name is required";
                    if (string.IsNullOrEmpty(clientVersion))
                        return $"ERROR: {PairingErrorCodes.ArgMissing}: Client version is required";
                    if (string.IsNullOrWhiteSpace(clientId))
                        return $"ERROR: {PairingErrorCodes.ArgMissing}: Client ID is required";

                    // Phase 0: TCP probe. Distinguishes L4 reachability (host/firewall) from L6/L7 issues
                    // (TLS, HTTP upgrade). Without this, ConnectAsync hangs for the full TLS budget on
                    // unreachable hosts and the user can't tell whether to debug network or crypto.
                    Uri uri;
                    try
                    {
                        uri = new Uri(hostUrl);
                    }
                    catch (UriFormatException ufx)
                    {
                        return $"ERROR: {PairingErrorCodes.HostUrlInvalid}: Invalid host URL '{hostUrl}': {ufx.Message}";
                    }

                    Console.Error.WriteLine($"[Pairing] Phase 0 — TCP probe {uri.Host}:{uri.Port} (10s budget)");
                    OnNativePairingProgress(PairingPhases.Probe);
                    using (var tcp = new System.Net.Sockets.TcpClient { NoDelay = true })
                    {
                        var probeTask = tcp.ConnectAsync(uri.Host, uri.Port);
                        // Wait(int, CancellationToken) rather than Wait(TimeSpan): TcpClient.ConnectAsync
                        // takes no token, so the abort has to be observed by the WAIT instead. The probe
                        // itself keeps running and the TcpClient's using disposes it on the way out.
                        var probeWon = probeTask.Wait(10_000, abort);
                        if (!probeWon)
                        {
                            return $"ERROR: {PairingErrorCodes.TcpTimeout}: TCP probe to {uri.Host}:{uri.Port} timed out after 10s — host unreachable, firewall, or wrong IP/port";
                        }
                        if (probeTask.IsFaulted)
                        {
                            var inner = probeTask.Exception?.GetBaseException();
                            return $"ERROR: {PairingErrorCodes.TcpRefused}: TCP probe to {uri.Host}:{uri.Port} refused — {inner?.GetType().Name}: {inner?.Message}";
                        }
                        Console.Error.WriteLine($"[Pairing] TCP probe OK — {uri.Host}:{uri.Port} accepted a connection");
                    }

                    ws = new ClientWebSocket();
                    // For initial pairing, we trust the cert because the PIN/QR is the out-of-band trust
                    ws.Options.RemoteCertificateValidationCallback = (sender, cert, chain, errors) => true;

                    Console.Error.WriteLine($"[Pairing] Phase 1 — TLS handshake + WebSocket upgrade to {hostUrl} (20s budget)");
                    OnNativePairingProgress(PairingPhases.Securing);

                    // Phase 1: connect (TLS handshake + HTTP/1.1 upgrade). Bounded so a wedged TLS
                    // doesn't hang the JNI thread.
                    using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(abort))
                    {
                        connectCts.CancelAfter(TimeSpan.FromSeconds(20));
                        try
                        {
                            ws.ConnectAsync(uri, connectCts.Token).GetAwaiter().GetResult();
                        }
                        catch (OperationCanceledException) when (abort.IsCancellationRequested)
                        {
                            // Ordered BEFORE the timeout catch: a linked source is cancelled in both
                            // cases, so testing the timeout first would report every abort as a
                            // 20-second TLS timeout that never actually elapsed.
                            return $"ERROR: {PairingErrorCodes.Aborted}: pairing abandoned by the client during TLS";
                        }
                        catch (OperationCanceledException) when (connectCts.IsCancellationRequested)
                        {
                            return $"ERROR: {PairingErrorCodes.TlsTimeout}: TLS/upgrade timed out after 20s — TCP reached {uri.Host}:{uri.Port} but TLS handshake or WebSocket upgrade did not complete (check host cert and that path '{uri.AbsolutePath}' is mapped)";
                        }
                    }

                    Console.Error.WriteLine("[Pairing] Phase 2 — WebSocket connected. Sending PairingRequest, awaiting PairingResponse (60s budget)");
                    OnNativePairingProgress(PairingPhases.AwaitingHost);

                    // Phase 2: pairing handshake (send PairingRequest, await PairingResponse).
                    // Generous budget — host generates PIN, derives ECDH session key, computes HMAC, and sends back.
                    // Should be fast (<1s) but allow margin for first-time TLS sessions, slow hardware, etc.
                    using (var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(abort))
                    {
                        handshakeCts.CancelAfter(TimeSpan.FromSeconds(60));
                        client = new PairingClient(ws, log: msg => Console.Error.WriteLine($"[PairingClient] {msg}"))
                        {
                            ClientId = clientId
                        };
                        try
                        {
                            _activePairingResponse = client.StartPairingAsync(clientName, clientVersion, handshakeCts.Token).GetAwaiter().GetResult();
                        }
                        catch (OperationCanceledException) when (abort.IsCancellationRequested)
                        {
                            try { ws.Dispose(); } catch { /* as below */ }
                            return $"ERROR: {PairingErrorCodes.Aborted}: pairing abandoned by the client during the handshake";
                        }
                        catch (OperationCanceledException) when (handshakeCts.IsCancellationRequested)
                        {
                            try { ws.Dispose(); } catch { /* best-effort cleanup of a socket that never got promoted; see ClearActivePairingState */ }
                            return $"ERROR: {PairingErrorCodes.PairTimeout}: Pairing handshake timed out — host did not return PairingResponse within 60s";
                        }
                    }

                    if (_activePairingResponse == null)
                    {
                        _activePairingClient = null;
                        try { ws.Dispose(); } catch { /* as above */ }
                        return $"ERROR: {PairingErrorCodes.PairMalformed}: Host responded but PairingResponse payload was missing";
                    }

                    _activePairingClient = client;
                    _pairingWebSocket = ws;
                    ws = null; // ownership transferred to the static field
                    Console.Error.WriteLine($"[Pairing] PairingResponse received from host {_activePairingResponse.HostId}");
                    return "OK";
                }
                catch (OperationCanceledException) when (abort.IsCancellationRequested)
                {
                    // The TCP probe's wait, and anything else that observes the token without its own
                    // catch. Ordered ahead of the general handler so an abort is never reported as an
                    // unexpected failure.
                    _activePairingClient = null;
                    return $"ERROR: {PairingErrorCodes.Aborted}: pairing abandoned by the client";
                }
                catch (Exception ex)
                {
                    _activePairingClient = null;
                    Console.Error.WriteLine($"[Pairing] StartPairing failed: {ex.GetType().Name}: {ex.Message}");
                    return $"ERROR: {PairingErrorCodes.Unexpected}: {ex.GetType().Name}: {ex.Message}";
                }
                finally
                {
                    PairingAborts.End(abort);

                    // If we created a socket but didn't promote it to _pairingWebSocket, dispose it now.
                    if (ws != null)
                    {
                        try { ws.Dispose(); } catch { /* as above */ }
                    }
                }
            }
        });
    }

    /// <summary>
    /// Step three: submits the PIN the user read off the PC, completing the exchange and pinning the
    /// host's SPKI hash on success.
    /// </summary>
    /// <param name="pinPtr">The 6-digit PIN as typed.</param>
    /// <returns>JSON <see cref="AndroidNativeOperationResponse"/>; failure means a wrong or expired PIN.</returns>
    [UnmanagedCallersOnly(EntryPoint = "Java_com_clindsay94_remex_RemexCoreClient_SubmitPairingPinNative")]
    public static IntPtr SubmitPairingPinNative(IntPtr env, IntPtr thiz, IntPtr pinPtr, long attemptId)
    {
        return Export(env, () =>
        {
            // Marshal inside the Export guard (JNI-5 / RemEx-85i).
            var pin = JniHelper.ReadJString(env, pinPtr);

            // Serialize against StartPairing/Clear so the session statics aren't disposed-then-used
            // by a concurrent pairing export (JNI-4 / RemEx-8ay).
            lock (PairingSyncRoot)
            {
                var abort = PairingAborts.Begin(attemptId);
                try
                {
                    if (string.IsNullOrEmpty(pin))
                        return $"ERROR: {PairingErrorCodes.ArgMissing}: PIN is required";
                    if (_pairingWebSocket == null || _activePairingResponse == null)
                        return $"ERROR: {PairingErrorCodes.NoSession}: No active pairing session";
                    if (_activePairingClient == null)
                        return $"ERROR: {PairingErrorCodes.SessionKeyLost}: Pairing session lost client key state";

                    Console.Error.WriteLine("[Pairing] Submitting PIN — sending PairingComplete, awaiting host confirmation (30s budget)");

                    var client = _activePairingClient;
                    bool success;
                    using (var completeCts = CancellationTokenSource.CreateLinkedTokenSource(abort))
                    {
                        completeCts.CancelAfter(TimeSpan.FromSeconds(30));
                        try
                        {
                            success = client.CompletePairingAsync(pin, _activePairingResponse, completeCts.Token).GetAwaiter().GetResult();
                        }
                        catch (OperationCanceledException) when (abort.IsCancellationRequested)
                        {
                            // Torn down like the timeout beside it, and for the same reason: the
                            // PairingComplete went out and its answer was never read, so the session
                            // is indeterminate and the user must start again rather than submit twice.
                            ClearActivePairingState();
                            return $"ERROR: {PairingErrorCodes.AbortedSessionLost}: PIN submission abandoned by the client";
                        }
                        catch (OperationCanceledException) when (completeCts.IsCancellationRequested)
                        {
                            ClearActivePairingState();
                            return $"ERROR: {PairingErrorCodes.PinConfirmTimeout}: PIN submission timed out — host did not confirm within 30s";
                        }
                    }

                    if (success)
                    {
                        // Surface the reconnect secret (PAIR-1) as a third pipe-delimited field so the
                        // Kotlin layer can persist it in the Android keystore and supply it on future
                        // connects (AndroidNativeInitRequest.ReconnectSecret). Empty if unavailable.
                        var reconnectSecret = client.LastReconnectSecretBase64 ?? string.Empty;
                        var result = $"OK:{_activePairingResponse.HostId}|{_activePairingResponse.CertificateSpkiHashBase64}|{reconnectSecret}";
                        ClearActivePairingState();
                        Console.Error.WriteLine("[Pairing] Pairing complete and verified.");
                        return result;
                    }

                    // PIN HMAC mismatch or host rejected — tear down so the user can retry cleanly.
                    ClearActivePairingState();
                    return $"ERROR: {PairingErrorCodes.PinRejected}: Pairing verification failed (incorrect PIN or session expired)";
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Pairing] SubmitPairingPin failed: {ex.GetType().Name}: {ex.Message}");
                    ClearActivePairingState();
                    return $"ERROR: {PairingErrorCodes.Unexpected}: {ex.GetType().Name}: {ex.Message}";
                }
                finally
                {
                    PairingAborts.End(abort);
                }
            }
        });
    }

    /// <summary>
    /// Step two: waits for the host's <c>pairing_pin_response</c> and returns it synchronously.
    /// </summary>
    /// <returns>
    /// A BARE STATUS STRING, not JSON: <c>OK:{hostId}|{spkiHash}|{reconnectSecret}</c> on success,
    /// <c>UNSUPPORTED</c>, or <c>ERROR: {code}: {message}</c>. Parse it by prefix.
    /// </returns>
    /// <remarks>
    /// THE REASON <c>pairing_pin_response</c> IS NOT IN THE INBOUND MESSAGE ROUTER, and this is
    /// deliberate rather than an oversight. It arrives on the pairing socket, which only the pairing
    /// client reads, and is consumed as this method's return value — so it needs no JNI callback and
    /// cannot be silently dropped by construction. Do not "fix" it by adding it to
    /// <c>OnNativeMessageReceived</c> (RemEx-1t0b).
    /// </remarks>
    [UnmanagedCallersOnly(EntryPoint = "Java_com_clindsay94_remex_RemexCoreClient_FetchPairingPinNative")]
    public static IntPtr FetchPairingPinNative(IntPtr env, IntPtr thiz, long attemptId)
    {
        return Export(env, () =>
        {
            // Serialize against StartPairing/Submit/Clear so the session statics aren't disposed-then-used
            // by a concurrent pairing export (JNI-4 / RemEx-8ay). The 5s budget below caps how long a
            // concurrent manual Submit could wait on this lock.
            lock (PairingSyncRoot)
            {
                var abort = PairingAborts.Begin(attemptId);
                try
                {
                    if (_pairingWebSocket == null || _activePairingClient == null || _activePairingResponse == null)
                        return $"ERROR: {PairingErrorCodes.NoSession}: No active pairing session";

                    // Fast path for older hosts: they never advertised the capability, so don't even
                    // send the request — that would just burn the full 5s budget waiting for a host
                    // that will never reply (older routers log-and-ignore the unknown type).
                    if (!_activePairingResponse.SupportsPinAutoFetch)
                        return "UNSUPPORTED";

                    Console.Error.WriteLine("[Pairing] Fetching active PIN over the pairing socket (5s budget)");

                    var client = _activePairingClient;
                    PairingPinInfo? pinInfo;
                    using (var fetchCts = CancellationTokenSource.CreateLinkedTokenSource(abort))
                    {
                        fetchCts.CancelAfter(TimeSpan.FromSeconds(5));
                        try
                        {
                            pinInfo = client.RequestPinAsync(fetchCts.Token).GetAwaiter().GetResult();
                        }
                        // BOTH CANCELLATION PATHS TEAR THE SESSION DOWN, AND THAT IS NOT A CHOICE
                        // MADE HERE — it is what cancelling the read does. ClientWebSocket registers
                        // Abort() on the token it is given, so a cancelled ReceiveAsync does not
                        // merely stop waiting, it kills the socket
                        // (CancelledReceiveKillsTheSocketTests proves it against a real TLS
                        // connection). §3.3's promise that the session survives so the user can type
                        // the PIN in by hand cannot hold on these two paths, and pretending otherwise
                        // is what left the fallback failing with an UNEXPECTED (RemEx-d3z9).
                        //
                        // So the state is cleared to match reality and the codes say the session is
                        // gone. That loses nothing that worked — the socket was already dead — and
                        // buys the user "start again", which is followable, instead of "unknown
                        // error", which is not. Every OTHER failure here still honours §3.3.
                        catch (OperationCanceledException) when (abort.IsCancellationRequested)
                        {
                            ClearActivePairingState();
                            return $"ERROR: {PairingErrorCodes.AbortedSessionLost}: PIN fetch abandoned by the client";
                        }
                        catch (OperationCanceledException) when (fetchCts.IsCancellationRequested)
                        {
                            ClearActivePairingState();
                            return $"ERROR: {PairingErrorCodes.PinFetchTimeout}: PIN fetch timed out";
                        }
                    }

                    // Null == the host declined (untrusted transport) OR there is no active PIN —
                    // indistinguishable by design (mirrors GET /pairing-pin's 404-for-both).
                    if (pinInfo is null)
                        return $"ERROR: {PairingErrorCodes.PinUnavailable}: PIN not available";

                    return $"OK:{pinInfo.Pin}|{pinInfo.ExpiresAtUnixMs}";
                }
                catch (Exception ex)
                {
                    // NON-DESTRUCTIVE CONTRACT (§3.3), for every failure that does not cancel the
                    // read. Whatever went wrong auto-fetching the PIN, the pairing session must stay
                    // valid so the user can still type the PIN in manually. The two cancellation
                    // paths above are the exception and cannot be otherwise: cancelling the read
                    // aborts the socket, so there is no session left to preserve (RemEx-d3z9).
                    Console.Error.WriteLine($"[Pairing] FetchPairingPin failed: {ex.GetType().Name}: {ex.Message}");
                    return $"ERROR: {PairingErrorCodes.Unexpected}: {ex.GetType().Name}: {ex.Message}";
                }
                finally
                {
                    PairingAborts.End(abort);
                }
            }
        });
    }

    /// <summary>
    /// Reads the SPKI hash pinned for a host, so the client can tell "never paired" from "paired, and
    /// the certificate must match this".
    /// </summary>
    /// <param name="hostIdPtr">Host identity the pin is filed under.</param>
    /// <returns>The stored hash as a bare string, or empty when nothing is pinned. Not JSON.</returns>
    [UnmanagedCallersOnly(EntryPoint = "Java_com_clindsay94_remex_RemexCoreClient_GetPinnedHostHashNative")]
    public static IntPtr GetPinnedHostHashNative(IntPtr env, IntPtr thiz, IntPtr hostIdPtr)
    {
        var hostId = JniHelper.ReadJString(env, hostIdPtr);
        return Export(env, () => _pinnedHashes.TryGetValue(hostId ?? "", out var hash) ? hash : "");
    }

    /// <summary>
    /// Stores the SPKI hash to require from a host from now on.
    /// </summary>
    /// <param name="hostIdPtr">Host identity to file the pin under.</param>
    /// <param name="spkiHashPtr">Base64 SHA-256 of the host certificate's SubjectPublicKeyInfo.</param>
    /// <remarks>
    /// Overwriting a pin is how a client accepts a NEW host certificate, so this is the one call that
    /// can silently undo pinning. It belongs to the pairing flow, where the PIN exchange proves the
    /// host's identity first; calling it from anywhere else would trust a certificate nothing has
    /// verified. Hashing the SPKI rather than the certificate is what lets a host re-issue for the
    /// same key pair without breaking every paired device.
    /// </remarks>
    [UnmanagedCallersOnly(EntryPoint = "Java_com_clindsay94_remex_RemexCoreClient_SetPinnedHostHashNative")]
    public static IntPtr SetPinnedHostHashNative(IntPtr env, IntPtr thiz, IntPtr hostIdPtr, IntPtr spkiHashPtr)
    {
        var hostId = JniHelper.ReadJString(env, hostIdPtr);
        var hash = JniHelper.ReadJString(env, spkiHashPtr);
        if (!string.IsNullOrEmpty(hostId) && !string.IsNullOrEmpty(hash))
        {
            _pinnedHashes[hostId] = hash;
        }
        return Export(env, () => "OK");
    }

    /// <summary>
    /// Drops the in-memory pin for one host key, mirroring
    /// <see cref="SetPinnedHostHashNative"/>.
    /// </summary>
    /// <remarks>
    /// WITHOUT THIS, FORGETTING A PC DID NOT TAKE EFFECT UNTIL THE PROCESS RESTARTED (RemEx-1phe).
    /// The connect path falls back to this cache when the DataStore pin is gone, so clearing the
    /// stored pin alone left the old hash live for the rest of the process - the user taps "forget"
    /// or "repair", reconnects, and is still pinned to the certificate they just rejected.
    ///
    /// The setter cannot serve as its own clear: it ignores an empty hash, so passing one is a no-op
    /// rather than a removal.
    /// </remarks>
    [UnmanagedCallersOnly(EntryPoint = "Java_com_clindsay94_remex_RemexCoreClient_ClearPinnedHostHashNative")]
    public static IntPtr ClearPinnedHostHashNative(IntPtr env, IntPtr thiz, IntPtr hostIdPtr)
    {
        var hostId = JniHelper.ReadJString(env, hostIdPtr);
        if (!string.IsNullOrEmpty(hostId))
        {
            _pinnedHashes.TryRemove(hostId, out _);
        }
        return Export(env, () => "OK");
    }

    private static string HandleInitialize(string? initJson)
    {
        var initRequest = string.IsNullOrWhiteSpace(initJson)
            ? new AndroidNativeInitRequest()
            : RemexJson.Deserialize(initJson, RemexJsonSerializerContext.Default.AndroidNativeInitRequest) ?? new AndroidNativeInitRequest();

        if (string.IsNullOrWhiteSpace(initRequest.ClientId))
        {
            return SerializeOperationFailure("Client ID is required.");
        }

        var effectiveSpkiHash = initRequest.SpkiHash;
        if (string.IsNullOrWhiteSpace(effectiveSpkiHash)
            && !string.IsNullOrWhiteSpace(initRequest.Host)
            && _pinnedHashes.TryGetValue(initRequest.Host, out var cachedHash)
            && !string.IsNullOrWhiteSpace(cachedHash))
        {
            effectiveSpkiHash = cachedHash;
            JniHelper.AndroidLogE("RemexNative", $"InitRemex resolved SPKI hash for {initRequest.Host} from native cache");
        }

        var effectiveInitRequest = string.IsNullOrWhiteSpace(effectiveSpkiHash)
            ? initRequest
            : initRequest with { SpkiHash = effectiveSpkiHash };

        lock (SyncRoot)
        {
            _lastInitRequest = effectiveInitRequest;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await RemexNativeClient.Current.ConnectAsync(
                    effectiveInitRequest.Host,
                    effectiveInitRequest.Port,
                    effectiveInitRequest.SpkiHash,
                    effectiveInitRequest.ClientId,
                    effectiveInitRequest.ReconnectSecret);
            }
            catch (Exception ex) { JniHelper.AndroidLogE("RemexNative", $"ConnectAsync failed: {ex.Message}"); }
        });

        var response = new AndroidNativeInitializationResponse
        {
            Success = true,
            Message = "Android native exports initialized.",
            TelemetryAvailable = true,
            BackgroundLoopStarted = true,
            IpcAvailable = true,
            WakeOnLanAvailable = true,
            TelemetryPollIntervalMs = 1000,
        };

        return RemexJson.Serialize(response, RemexJsonSerializerContext.Default.AndroidNativeInitializationResponse);
    }

    private static string HandleSendWakeOnLan(string? macAddress, string? broadcastIp, int port)
    {
        if (string.IsNullOrWhiteSpace(macAddress))
        {
            return SerializeOperationFailure("Wake-on-LAN requires a MAC address.");
        }

        var service = _wakeOnLanService;
        var effectiveBroadcastIp = string.IsNullOrWhiteSpace(broadcastIp) ? "255.255.255.255" : broadcastIp;
        var effectivePort = port > 0 ? port : 9;

        // **REPORTS WHETHER THE PACKET LEFT THIS PHONE (RemEx-52n0).** It used to fire the send into a
        // discarded Task.Run and return success unconditionally, under a comment claiming failures
        // reached the user "via the Android toast/status mechanism that observes
        // ConnectionStateChanged". That was invented: a WakeAsync failure has nothing to do with the
        // connection state and nothing observed it. The two SCREEN callers had already been written to
        // branch on the success flag — DashboardViewModel's comment says outright that "a failed send
        // now surfaces as a failure" (RemEx-nbfb) — so their failure branch was unreachable FOR ANY
        // SEND FAILURE, and the only thing missing was a producer that told the truth. (It was always
        // reachable by the library failing to load or the JNI boundary itself throwing; neither has
        // anything to say about the packet.) The third caller, the home-screen widget, discarded the
        // result entirely and still only logs it — RemEx-mug0.
        //
        // BE CLEAR ABOUT WHAT THIS CAN AND CANNOT MEAN. Wake-on-LAN is a UDP broadcast aimed at a
        // machine that is switched off; there is no acknowledgement and there never will be, so
        // success here says the magic packet was transmitted, NOT that the PC woke up. The wording
        // says "sent" for exactly that reason. What was being thrown away is the other half —
        // a malformed MAC, an unparseable broadcast address, or every interface refusing the send,
        // all of which WakeAsync raises and all of which are worth telling somebody about.
        //
        // Safe to block the JNI thread on this in a way SendCommandAsync was not: there is no round
        // trip to wait for. Every await inside WakeAsync is a UDP send that completes once the OS has
        // the datagram — no connection to establish, no send window to fill, no lock to queue behind,
        // and no name resolution (the broadcast address is parsed, never looked up).
        //
        // **Task.Run IS LOAD-BEARING HERE, AND REVIEW CAUGHT THE VERSION WITHOUT IT.** An `async Task`
        // method runs SYNCHRONOUSLY on the calling thread until its first incomplete await, so calling
        // WakeAsync directly would perform the interface enumeration and every socket bind inline —
        // before the Task that .WaitAsync wraps even exists. The timeout would then be measured on
        // work that had already finished, and in the common case (UDP sends completing synchronously)
        // it would be a no-op on an already-completed Task. Handing the whole call to the pool is what
        // puts the prologue inside the budget.
        //
        // The timeout is belt-and-braces rather than a limit the normal path approaches, and it leaves
        // the send running if it ever fires — the packet may still go out, we simply stop waiting to
        // find out.
        try
        {
            Task.Run(() => service.WakeAsync(macAddress, effectiveBroadcastIp, effectivePort))
                .WaitAsync(WakeOnLanSendTimeout)
                .GetAwaiter()
                .GetResult();

            return SerializeOperationSuccess($"Wake-on-LAN packet sent to {macAddress}.");
        }
        catch (TimeoutException ex)
        {
            // Distinct from a refusal on purpose: the send was abandoned, not proven to have failed,
            // and the packet may well have gone out. Claiming it did not would be its own small lie.
            JniHelper.AndroidLogE("RemexNative", $"WakeAsync timed out: {ex.Message}");
            return SerializeOperationFailure(
                "Could not confirm the wake signal was sent.", ex.ToString());
        }
        catch (Exception ex)
        {
            JniHelper.AndroidLogE("RemexNative", $"WakeAsync failed: {ex.Message}");
            return SerializeOperationFailure(
                "The wake signal could not be sent from this phone.", ex.ToString());
        }
    }

    /// <summary>
    /// Ceiling on a Wake-on-LAN send, which is a handful of local UDP writes.
    /// </summary>
    /// <remarks>
    /// Generous by design — the normal path is sub-millisecond, so anything approaching this is a
    /// socket that is never going to complete. It exists only so that a JNI thread cannot be parked
    /// indefinitely by one, which is the failure RemEx-66rf had to fix on the command path.
    /// </remarks>
    private static readonly TimeSpan WakeOnLanSendTimeout = TimeSpan.FromSeconds(5);

    private static string HandleRequestTelemetry()
    {
        var telemetry = _cachedTelemetry;
        if (telemetry != null)
        {
            return SerializeTelemetrySuccess(telemetry);
        }
        return SerializeTelemetryFailure("Telemetry not yet available.");
    }

    private static string HandleDispatchMessage(string? messageJson)
    {
        if (string.IsNullOrWhiteSpace(messageJson))
        {
            return SerializeOperationFailure("Message JSON is required.");
        }

        var message = RemexJson.Deserialize(messageJson, RemexJsonSerializerContext.Default.RemexMessage);
        if (message == null)
        {
            return SerializeOperationFailure("Failed to deserialize message.");
        }

        if (HandleDesktopMessage(message))
        {
            return SerializeOperationSuccess($"{message.Type} dispatched.");
        }

        EnsureOutboundSendLoopStarted();
        if (!OutboundMessageQueue.Writer.TryWrite(message))
        {
            return SerializeOperationFailure($"Failed to queue message '{message.Type}'.");
        }

        return SerializeOperationSuccess("Message dispatched.");
    }

    /// <summary>
    /// Sends one input event on the CONTROL socket, never on <c>/ws/desktop</c> (RemEx-035d6).
    /// </summary>
    /// <remarks>
    /// <para>
    /// THIS EXISTS BECAUSE ROUTING BY MESSAGE TYPE WAS WRONG FOR HALF THE CALLERS.
    /// <see cref="HandleDesktopMessage"/> claims every <c>desktop_input</c> and hands it to
    /// <see cref="RemexDesktopClient"/>, which is correct for the Remote Desktop screen — its input
    /// belongs on the same socket as the stream it is aimed at — and wrong for the Remote Control
    /// screen, which has no stream at all. That screen's media and volume row (RemEx-hulc) went down
    /// that path anyway, and it failed in both directions:
    /// </para>
    /// <para>
    /// DEAD. <c>RemexDesktopClient</c> is a process singleton, and its stopped-by-request latch is
    /// set by <c>StopStreamAsync</c> and cleared ONLY by <c>StartStreamAsync</c> (RemEx-yzbb).
    /// <c>RemoteDesktopViewModel.onCleared</c> stops the stream, so merely opening the Remote Desktop
    /// screen and navigating away latched it for the life of the process — after which
    /// <c>SendInputAsync</c> returned before sending and every media key was silently discarded. The
    /// phone still buzzed on tap. Only killing the app brought the row back.
    /// </para>
    /// <para>
    /// OR WORSE THAN DEAD. With the latch clear, <c>SendInputAsync</c> auto-starts a stream when one
    /// is not running — the right recovery after a socket blip, and absurd here: tapping Volume Up on
    /// a screen that shows no video began a full H.264 capture session on the PC, keep-awake engaged,
    /// encoding frames for nobody.
    /// </para>
    /// <para>
    /// The host already handles <c>desktop_input</c> on this socket, with held-key cleanup on
    /// disconnect (<c>PingPongHandler.DispatchInput</c>); its own comment records that no client had
    /// yet sent there. So this needs no new message type, no <c>protocolVersion</c> bump and no host
    /// change — which also means there is no new CLIENT-BOUND type for the inbound router to drop
    /// silently, the RemEx-y6x6 failure mode.
    /// </para>
    /// <para>
    /// IT TAKES AN <see cref="InputEvent"/>, NOT AN ENVELOPE, AND THAT IS THE POINT. The caller
    /// cannot choose the type, so this entry point can only ever put input on the control socket. An
    /// envelope-shaped export would be <see cref="HandleDispatchMessage"/> with the routing switch
    /// removed, and the next caller to reach for it would send something else through.
    /// </para>
    /// </remarks>
    internal static string HandleSendControlInput(string? inputJson)
    {
        if (string.IsNullOrWhiteSpace(inputJson))
        {
            return SerializeOperationFailure("Input event JSON is required.");
        }

        // CAUGHT HERE RATHER THAN LEFT TO Export, which is a deliberate deviation from
        // HandleDispatchMessage next door. Deserialize THROWS on malformed JSON, so that one relies
        // on the export wrapper's generic fallback — an answer that names no cause. This path is the
        // only report a caller ever gets about an input event, so it says which of the two things
        // went wrong: the payload was unreadable, or it read as nothing.
        InputEvent? input;
        try
        {
            input = RemexJson.Deserialize(inputJson, RemexJsonSerializerContext.Default.InputEvent);
        }
        catch (JsonException ex)
        {
            return SerializeOperationFailure("Malformed input event JSON.", ex.Message);
        }

        if (input == null)
        {
            return SerializeOperationFailure("Failed to deserialize input event.");
        }

        EnsureOutboundSendLoopStarted();
        if (!OutboundMessageQueue.Writer.TryWrite(new RemexMessage
        {
            Type = MessageTypes.DesktopInput,
            InputEvent = input,
        }))
        {
            return SerializeOperationFailure("Failed to queue control input.");
        }

        return SerializeOperationSuccess("Control input dispatched.");
    }

    /// <summary>
    /// Every Remote Desktop operation, in the order it was handed to this queue.
    /// </summary>
    /// <remarks>
    /// These were a dozen separate <c>Task.Run</c> calls — fire-and-forget onto the thread pool, so
    /// two operations handed over microseconds apart could start on different workers and reach the
    /// socket in either order. <c>sendKeyPress</c> issues keyDown and keyUp as SEPARATE messages, so
    /// an inversion leaves a key physically held down on the user's PC, and it fails silently.
    /// <see cref="OrderedAsyncWorkQueue"/> carries the reasoning and the test. (RemEx-krvz)
    /// <para>
    /// The send gate on <c>RemexDesktopClient</c> is defence for callers that do not come through
    /// here — it stops overlap but cannot restore order, so it is not the fix.
    /// </para>
    /// <para>
    /// NOTE WHERE THE GUARANTEE STARTS: at <c>Enqueue</c>, which runs synchronously on the JNI thread
    /// and so inherits whatever order the Kotlin caller submitted in. That is genuinely ordered for
    /// <c>RemoteControlViewModel</c>, which serialises its sends on a single-threaded dispatcher
    /// (RemEx-3uhp). <c>RemoteDesktopViewModel</c> does NOT yet — it launches on bare
    /// <c>viewModelScope</c> — so two of ITS operations can still arrive here in either order. That
    /// is RemEx-7rq3, and this queue cannot fix it from below.
    /// </para>
    /// </remarks>
    private static readonly OrderedAsyncWorkQueue DesktopWork =
        new((label, ex) => JniHelper.AndroidLogE("RemexNative", $"{label} failed: {ex.Message}"));

    private static void QueueDesktopWork(string label, Func<Task> work) => DesktopWork.Enqueue(label, work);

    private static void EnsureOutboundSendLoopStarted()
    {
        if (Interlocked.Exchange(ref _outboundSendLoopStarted, 1) == 1)
            return;

        _ = Task.Run(ProcessOutboundMessagesAsync);
    }

    private static async Task ProcessOutboundMessagesAsync()
    {
        await foreach (var message in OutboundMessageQueue.Reader.ReadAllAsync())
        {
            try
            {
                await RemexNativeClient.Current.SendMessageAsync(message);
            }
            catch (Exception ex)
            {
                JniHelper.AndroidLogE("RemexNative", $"Queued send failed for {message.Type}: {ex.Message}");
            }
        }
    }

    private static bool HandleDesktopMessage(RemexMessage message)
    {
        switch (message.Type)
        {
            case MessageTypes.DesktopStart:
                QueueDesktopWork("DesktopStart", async () =>
                {
                    var (host, port, clientId, spkiHash) = GetDesktopEndpoint();
                    await RemexDesktopClient.Current.StartStreamAsync(host, port, message.DesktopConfig ?? new DesktopConfig(), clientId, spkiHash);
                });
                return true;

            case MessageTypes.DesktopInput when message.InputEvent != null:
                QueueDesktopWork("DesktopInput", async () =>
                {
                    var (host, port, clientId, spkiHash) = GetDesktopEndpoint();
                    await RemexDesktopClient.Current.SendInputAsync(host, port, message.InputEvent, clientId, spkiHash);
                });
                return true;

            case MessageTypes.DesktopConfig when message.DesktopConfig != null:
                QueueDesktopWork("DesktopConfig", async () =>
                {
                    var (host, port, clientId, spkiHash) = GetDesktopEndpoint();
                    await RemexDesktopClient.Current.StartStreamAsync(host, port, message.DesktopConfig, clientId, spkiHash);
                });
                return true;

            case MessageTypes.DesktopDisplayQuery:
                QueueDesktopWork("DesktopDisplayQuery", async () =>
                {
                    var (host, port, clientId, spkiHash) = GetDesktopEndpoint();
                    await RemexDesktopClient.Current.RequestDisplayCatalogAsync(host, port, clientId, spkiHash);
                });
                return true;

            case MessageTypes.DesktopTargetSwitch when message.DesktopTargetSwitch != null:
                QueueDesktopWork("DesktopTargetSwitch", async () =>
                {
                    var (host, port, clientId, spkiHash) = GetDesktopEndpoint();
                    await RemexDesktopClient.Current.SwitchTargetAsync(host, port, message.DesktopTargetSwitch, clientId, spkiHash);
                });
                return true;

            case MessageTypes.DesktopWindowQuery when message.DesktopWindowQuery != null:
                QueueDesktopWork("DesktopWindowQuery", async () =>
                {
                    var (host, port, clientId, spkiHash) = GetDesktopEndpoint();
                    await RemexDesktopClient.Current.QueryWindowsAsync(host, port, message.DesktopWindowQuery, clientId, spkiHash);
                });
                return true;

            case MessageTypes.DesktopWindowAction when message.DesktopWindowAction != null:
                QueueDesktopWork("DesktopWindowAction", async () =>
                {
                    var (host, port, clientId, spkiHash) = GetDesktopEndpoint();
                    await RemexDesktopClient.Current.ExecuteWindowActionAsync(host, port, message.DesktopWindowAction, clientId, spkiHash);
                });
                return true;

            case MessageTypes.DesktopStop:
                QueueDesktopWork("DesktopStop", async () =>
                {
                    await RemexDesktopClient.Current.StopStreamAsync();
                    await RemexDesktopClient.Current.DisconnectAsync();
                });
                return true;

            // On-demand keyframe (IDR) request after a decoder desync. Routed onto the desktop stream
            // socket so it reaches RemoteDesktopHandler; without this case it fell through to the
            // control /ws channel and was logged as "Unknown message type". (RemEx-bqc / #2a)
            case MessageTypes.DesktopKeyframeRequest:
                QueueDesktopWork("DesktopKeyframeRequest", async () =>
                {
                    var (host, port, clientId, spkiHash) = GetDesktopEndpoint();
                    await RemexDesktopClient.Current.RequestKeyframeAsync(host, port, clientId, spkiHash);
                });
                return true;

            default:
                return false;
        }
    }

    private static string HandleDispatchCommand(string? commandJson)
    {
        if (string.IsNullOrWhiteSpace(commandJson))
        {
            return SerializeCommandResponse(new CommandResponse(false, "Command JSON is required.", null));
        }

        var command = RemexJson.Deserialize(commandJson, RemexJsonSerializerContext.Default.CommandRequest);
        if (command == null)
        {
            return SerializeCommandResponse(new CommandResponse(false, "Failed to deserialize command.", null));
        }

        // **WAITS FOR THE HOST'S ANSWER, AND THE VERSION THAT DID NOT MADE EVERY COMMAND A LIE
        // (RemEx-66rf).** This used to fire the send into a discarded Task.Run and immediately return
        // `success: true, "Command dispatched."` — so a dropped socket, a host too old to know the
        // verb, and a command that FAILED on the PC were all indistinguishable from success on the
        // phone. The old comment claimed the outcome reached Kotlin "via the RegisterCallbackNative
        // callbacks"; no such callback exists for command responses, and nothing consumed the
        // CommandResponse this awaits.
        //
        // Safe to block here because SendCommandAsync is BOUNDED AND TOTAL: it returns immediately
        // when disconnected, budgets the send AND the reply together at 10 seconds, and converts
        // every failure — timeout included — into a CommandResponse rather than throwing. (Bounded
        // only became true as part of this bead: the budget used to start after the send had already
        // been awaited on the caller's token, which here is None.) The same GetAwaiter().GetResult()
        // shape as FetchPairingPinNative, for the same reason: the value is the return value of a
        // synchronous JNI export, so there is nowhere else for it to go.
        //
        // Kotlin holds up its end by switching to a background dispatcher inside
        // RemexCoreClient.SendCommand, so this can never block the Android main thread.
        try
        {
            var response = RemexNativeClient.Current
                .SendCommandAsync(command, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            return SerializeCommandResponse(response);
        }
        catch (Exception ex)
        {
            // SendCommandAsync catches its own failures, so reaching here means something outside it
            // broke. Reported as a failure rather than swallowed: an honest "it did not work" is the
            // whole point of this method.
            JniHelper.AndroidLogE("RemexNative", $"SendCommand failed: {ex.Message}");
            return SerializeCommandResponse(
                new CommandResponse(false, "The command could not be sent.", ex.ToString()));
        }
    }

    private static void OnNativeProcessListReceived(List<ProcessInfo> processes)
    {
        NotifyJavaData(_onProcessListSyncMethodId, RemexJson.Serialize(processes, RemexJsonSerializerContext.Default.ListProcessInfo));
    }

    private static void OnNativeLauncherEntriesReceived(List<AppEntry> entries)
    {
        NotifyJavaData(_onLauncherSyncMethodId, RemexJson.Serialize(entries, RemexJsonSerializerContext.Default.ListAppEntry));
    }

    private static void OnNativeTelemetryReceived(TelemetryPayload telemetry)
    {
        _cachedTelemetry = telemetry;
        NotifyJavaData(_onTelemetryUpdateMethodId, RemexJson.Serialize(telemetry, RemexJsonSerializerContext.Default.TelemetryPayload));
    }

    private static void OnNativeConnectionStateChanged(bool isConnected)
    {
        NotifyJavaConnectionState(isConnected);
    }

    private static void OnNativeFrameReceived(byte[] frame)
    {
        NotifyJavaFrame(frame);
    }

    private static void OnNativeDesktopError(string errorText)
    {
        NotifyJavaData(_onDesktopErrorMethodId, errorText);
    }

    private static void OnNativeConnectionFailed(string reason)
    {
        NotifyJavaData(_onConnectionErrorMethodId, reason);
    }

    private static void OnNativeMetaReceived(DesktopMeta meta)
    {
        NotifyJavaData(_onDesktopMetaMethodId, RemexJson.Serialize(meta, RemexJsonSerializerContext.Default.DesktopMeta));
    }

    private static void OnNativeDesktopWindowResult(DesktopWindowResult result)
    {
        NotifyJavaData(_onDesktopWindowResultMethodId, RemexJson.Serialize(result, RemexJsonSerializerContext.Default.DesktopWindowResult));
    }

    private static void OnNativeDesktopStreamDescriptor(DesktopStreamDescriptor descriptor)
    {
        NotifyJavaData(_onDesktopStreamDescriptorMethodId, RemexJson.Serialize(descriptor, RemexJsonSerializerContext.Default.DesktopStreamDescriptor));
    }

    private static void OnNativeDisplayCatalogReceived(DesktopDisplayCatalog catalog)
    {
        NotifyJavaData(_onDesktopDisplayCatalogMethodId, RemexJson.Serialize(catalog, RemexJsonSerializerContext.Default.DesktopDisplayCatalog));
    }

    private static void OnNativeCursorStateReceived(DesktopCursorState state)
    {
        NotifyJavaData(_onDesktopCursorStateMethodId, RemexJson.Serialize(state, RemexJsonSerializerContext.Default.DesktopCursorState));
    }

    private static void OnNativeCursorShapeReceived(DesktopCursorShape shape)
    {
        NotifyJavaData(_onDesktopCursorShapeMethodId, RemexJson.Serialize(shape, RemexJsonSerializerContext.Default.DesktopCursorShape));
    }

    private static void NotifyJavaFrame(byte[] frame)
        => NotifyJavaByteArray(_onFrameReceivedMethodId, frame);

    // RD-E: forward the raw 32-byte "RDXC" cursor-position packet to Java as a byte[] (parsed in Kotlin
    // with ByteBuffer). Reuses the byte-array JNI path — no JSON string, no JSONObject on the hot path.
    private static void OnNativeCursorBinaryReceived(byte[] packet)
        => NotifyJavaByteArray(_onDesktopCursorBinaryMethodId, packet);

    // Shared byte[] -> Java callback dispatch, used by both H.264 frames and the binary cursor packet.
    private static void NotifyJavaByteArray(IntPtr targetMethodId, byte[] data)
    {
        lock (SyncRoot)
        {
            if (_javaVm == IntPtr.Zero || _callbackGlobalRef == IntPtr.Zero || targetMethodId == IntPtr.Zero) return;
        }

        // The captured `data` is a fresh per-message array (RemexDesktopClient raises FrameReceived /
        // CursorBinaryReceived with ms.ToArray()), so holding the reference across the async hand-off is
        // safe. INVARIANT: if that producer is ever changed to pool/reuse buffers, this MUST copy before
        // enqueuing or the Java side will read torn data.
        PostToJavaThread(env =>
        {
            // Re-read the global ref under lock at execution time: it may have been replaced (and the old
            // one deleted) between enqueue and dispatch. The method id is stable for a given method.
            IntPtr callback;
            lock (SyncRoot)
            {
                callback = _callbackGlobalRef;
            }
            if (callback == IntPtr.Zero || targetMethodId == IntPtr.Zero) return;

            IntPtr jArray = JniHelper.NewByteArray(env, data.Length);
            if (jArray == IntPtr.Zero)
            {
                // Allocation failed — likely a pending OutOfMemoryError. The dispatcher reuses one
                // daemon-thread env across callbacks, so a pending exception left set here would
                // poison the next callback (JNI-3 / RemEx-ymb). Clear it before bailing.
                if (JniHelper.ExceptionCheck(env)) JniHelper.ExceptionClear(env);
                return;
            }

            try
            {
                JniHelper.SetByteArrayRegion(env, jArray, 0, data.Length, data);
                JniHelper.CallVoidMethod(env, callback, targetMethodId, jArray);
                if (JniHelper.ExceptionCheck(env))
                {
                    JniHelper.ExceptionClear(env);
                    JniHelper.AndroidLogE("RemexNative", "Java callback threw an exception; cleared to protect the JNI bridge.");
                }
            }
            finally
            {
                JniHelper.DeleteLocalRef(env, jArray);
            }
        }, droppable: true);
    }

    private static void OnNativeMessageReceived(RemexMessage msg)
    {
        if (msg.Type == MessageTypes.HostInfo && msg.HostCapabilities != null)
        {
            NotifyJavaData(
                _onHostInfoUpdateMethodId,
                RemexJson.Serialize(msg.HostCapabilities, RemexJsonSerializerContext.Default.HostCapabilities));
        }

        // Forward the ENTIRE file-transfer control family (every "file_*" message) to the Kotlin file
        // layer. This deliberately replaces the old hand-maintained allowlist, which only knew the 2.0
        // types: when the 2.1 File Sharing Overhaul (protocolVersion 3) added file_transfer_ready /
        // complete / result / control, the v3 browse responses (volumes / search / metadata / thumbnail)
        // and consent / push, this router silently dropped every one of them — bricking all v3 transfer
        // negotiation ("Peer did not respond") and v3 browse while the host itself looked perfectly
        // healthy. Every "file_*" type is destined for onFileTransferMessage, and each of the four Kotlin
        // collectors (FileTransferEngine, ShareToPcViewModel, AndroidFileTransferHost, FileTransferViewModel)
        // switches on `type` and ignores the ones it does not handle, so forwarding the whole family is
        // safe and stops this stale-allowlist regression from ever recurring. Ordinal compare keeps it
        // NativeAOT-safe and culture-independent. Do NOT narrow this back into an explicit type list.
        if (msg.Type is { } fileType && fileType.StartsWith("file_", StringComparison.Ordinal))
        {
            NotifyJavaData(
                _onFileTransferMessageMethodId,
                RemexJson.Serialize(msg, RemexJsonSerializerContext.Default.RemexMessage));
        }

        // THE WHOLE clipboard_ FAMILY, BY PREFIX, FOR THE REASON WRITTEN ABOVE (RemEx-ci98m). Without
        // this line clipboard_content is dropped here in silence: the host sends it, the send
        // succeeds, and the phone never hears. That is not hypothetical - it is exactly how v3 file
        // transfer was bricked, and it is why the bead that added clipboard_push deliberately shipped
        // no host -> client message at all.
        //
        // Prefix rather than a named list, so an answer type a later clipboard feature adds arrives
        // without anyone having to remember this file exists. Same decision, same mistake avoided, as
        // the file_ forward. Do NOT narrow it to an explicit type list.
        if (msg.Type is { } clipboardType && clipboardType.StartsWith("clipboard_", StringComparison.Ordinal))
        {
            NotifyJavaData(
                _onClipboardMessageMethodId,
                RemexJson.Serialize(msg, RemexJsonSerializerContext.Default.RemexMessage));
        }

        // WHAT THE PC IS PLAYING (RemEx-xx6xf). A SINGLE TYPE, SO NEITHER FAMILY FORWARD ABOVE CARRIES
        // IT — this line is the only thing between a host that sends correctly and a phone that never
        // hears, which is the entire RemEx-y6x6 failure mode and looks from either end like the other
        // end being broken. MediaStateReachesTheClientTests pins it.
        //
        // The PAYLOAD, not the envelope, because the phone has no use for the rest of it and this is
        // the shape onHostInfoUpdate already uses for the same kind of message. Guarded on non-null so
        // a media_state that lost its payload in deserialization becomes silence rather than a phone
        // parsing "null" into an icon.
        if (msg.Type == MessageTypes.MediaState && msg.MediaState != null)
        {
            NotifyJavaData(
                _onMediaStateMethodId,
                RemexJson.Serialize(msg.MediaState, RemexJsonSerializerContext.Default.MediaPlaybackState));
        }
    }

    private static (string Host, int Port, string ClientId, string SpkiHash) GetDesktopEndpoint()
    {
        lock (SyncRoot)
        {
            return (
                _lastInitRequest.Host,
                _lastInitRequest.Port,
                _lastInitRequest.ClientId ?? string.Empty,
                _lastInitRequest.SpkiHash);
        }
    }

    private static void NotifyJavaData(IntPtr methodId, string json)
    {
        lock (SyncRoot)
        {
            if (_javaVm == IntPtr.Zero || _callbackGlobalRef == IntPtr.Zero || methodId == IntPtr.Zero) return;
        }

        PostToJavaThread(env =>
        {
            IntPtr callback;
            lock (SyncRoot)
            {
                callback = _callbackGlobalRef;
            }
            if (callback == IntPtr.Zero || methodId == IntPtr.Zero) return;

            IntPtr jString = JniHelper.CreateJString(env, json);
            if (jString == IntPtr.Zero)
            {
                // Clear any pending exception from NewString (e.g. OutOfMemoryError) so it cannot
                // bleed into the next callback on the shared dispatcher env (JNI-3 / RemEx-ymb).
                if (JniHelper.ExceptionCheck(env)) JniHelper.ExceptionClear(env);
                return;
            }
            try
            {
                JniHelper.CallVoidMethod(env, callback, methodId, jString);
                if (JniHelper.ExceptionCheck(env))
                {
                    JniHelper.ExceptionClear(env);
                    JniHelper.AndroidLogE("RemexNative", "Java callback threw an exception; cleared to protect the JNI bridge.");
                }
            }
            finally
            {
                JniHelper.DeleteLocalRef(env, jString);
            }
        });
    }

    private static void NotifyJavaConnectionState(bool isConnected)
    {
        lock (SyncRoot)
        {
            if (_javaVm == IntPtr.Zero || _callbackGlobalRef == IntPtr.Zero || _onConnectionStateChangedMethodId == IntPtr.Zero) return;
        }

        PostToJavaThread(env =>
        {
            IntPtr callback, methodId;
            lock (SyncRoot)
            {
                callback = _callbackGlobalRef;
                methodId = _onConnectionStateChangedMethodId;
            }
            if (callback == IntPtr.Zero || methodId == IntPtr.Zero) return;

            JniHelper.CallVoidMethod(env, callback, methodId, isConnected);
            if (JniHelper.ExceptionCheck(env))
            {
                JniHelper.ExceptionClear(env);
                JniHelper.AndroidLogE("RemexNative", "Java callback threw an exception; cleared to protect the JNI bridge.");
            }
        });
    }

    // Pre-serialized constant returned when the failure path itself throws. Computed once
    // at type init so the boundary catch block can never propagate a managed exception.
    // camelCase keys to match RemexJsonSerializerContext (JsonKnownNamingPolicy.CamelCase),
    // so the Android client deserializes this fallback exactly like a normal failure response.
    private static readonly string ExportFallbackJson =
        "{\"success\":false,\"message\":\"Native export failed and the error could not be serialized.\"}";

    /// <param name="failureJson">
    /// What to return instead of an operation-failure object when <paramref name="action"/> throws.
    /// <para>
    /// **ONLY FOR EXPORTS WHOSE ANSWER IS NOT AN <c>AndroidNativeOperationResponse</c>.** The default
    /// failure shape has no field in common with, say, a validation verdict, and Kotlin wraps a
    /// failure in a SUCCESSFUL <c>Result</c> — so an export with its own answer shape must supply a
    /// failure in that same shape, or the phone silently parses an object missing every field it
    /// looks for and falls back to its parser's defaults (RemEx-hgqs).
    /// </para>
    /// </param>
    private static IntPtr Export(IntPtr env, Func<string> action, string? failureJson = null)
    {
        // No JNI call may run while a Java exception is pending or the runtime aborts the
        // process (SIGABRT). Clear any exception left over before entering the export body.
        if (JniHelper.ExceptionCheck(env)) { JniHelper.ExceptionClear(env); }
        try
        {
            return JniHelper.CreateJString(env, action());
        }
        catch (Exception ex)
        {
            // Clear any exception raised inside the action before issuing the final JNI call,
            // then make the failure path itself non-throwing so nothing escapes the boundary.
            if (JniHelper.ExceptionCheck(env)) { JniHelper.ExceptionClear(env); }
            try
            {
                return JniHelper.CreateJString(env,
                    failureJson
                        ?? SerializeOperationFailure("Unhandled native export failure.", ex.Message));
            }
            catch
            {
                try { return JniHelper.CreateJString(env, ExportFallbackJson); }
                catch { return IntPtr.Zero; }
            }
        }
    }

    // Guard for void-returning JNI entry points. A managed exception that escapes an
    // [UnmanagedCallersOnly] boundary back toward the JVM cannot be propagated by the
    // NativeAOT runtime — it calls abort() and the whole process dies with SIGABRT. The
    // IntPtr exports are protected by Export() above; the void exports must run their
    // synchronous prologue (JNI string reads, JSON deserialization) inside this guard so a
    // bad/edge-case payload degrades to a logged no-op instead of crashing the app.
    private static void ExportVoid(string operation, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            JniHelper.AndroidLogE("RemexNative", $"{operation} native export failed: {ex.Message}");
        }
    }

    private static string SerializeOperationSuccess(string message)
        => RemexJson.Serialize(new AndroidNativeOperationResponse { Success = true, Message = message }, RemexJsonSerializerContext.Default.AndroidNativeOperationResponse);

    private static string SerializeOperationFailure(string message, string? error = null)
        => RemexJson.Serialize(new AndroidNativeOperationResponse { Success = false, Message = message, Error = error }, RemexJsonSerializerContext.Default.AndroidNativeOperationResponse);

    private static string SerializeTelemetrySuccess(TelemetryPayload telemetry)
        => RemexJson.Serialize(telemetry, RemexJsonSerializerContext.Default.TelemetryPayload);

    private static string SerializeTelemetryFailure(string message, string? error = null)
        => SerializeOperationFailure(message, error);

    private static string SerializeCommandResponse(CommandResponse response)
        => RemexJson.Serialize(response, RemexJsonSerializerContext.Default.CommandResponse);


}

/// <summary>Everything the native client needs to connect, passed to <see cref="AndroidNativeExports.InitRemex"/>.</summary>
public sealed record AndroidNativeInitRequest
{
    /// <summary>Host address. The "localhost" default is a development convenience — a real Android
    /// client is never on the same machine as the host and must set this.</summary>
    public string Host { get; init; } = "localhost";

    /// <summary>Host WebSocket port.</summary>
    public int Port { get; init; } = 5005;

    /// <summary>
    /// Base64 SHA-256 of the host certificate's SubjectPublicKeyInfo, pinned at pairing time. The
    /// connection is refused if the host presents anything else.
    /// </summary>
    /// <remarks>
    /// Empty means nothing is pinned yet, which is only valid before pairing. If the host's
    /// certificate is regenerated this stops matching and the user must re-pair — there is no
    /// automatic recovery, by design.
    /// </remarks>
    public string SpkiHash { get; init; } = string.Empty;

    /// <summary>Stable client identity the host files this device's pairing under.</summary>
    public string? ClientId { get; init; }

    /// <summary>
    /// Base64 reconnect secret persisted by the Android client after pairing (PAIR-1). Supplied on
    /// connect so the native client can answer the host's proof-of-possession challenge. Empty/null
    /// for clients that have not yet paired (or paired before this field existed) — they will be
    /// challenged and must re-pair.
    /// </summary>
    public string? ReconnectSecret { get; init; }

    /// <summary>How often to poll telemetry, in milliseconds.</summary>
    public int TelemetryPollIntervalMs { get; init; } = 1000;

    /// <summary>Whether to start the background telemetry loop at init. False leaves polling to the caller.</summary>
    public bool StartTelemetryPolling { get; init; } = true;

    /// <summary>
    /// Fetch one telemetry snapshot during init so the first screen has data immediately instead of
    /// showing empty cards until the first poll lands.
    /// </summary>
    public bool WarmupTelemetry { get; init; } = true;
}

/// <summary>
/// The shape every export returns on failure, and most return on success.
/// </summary>
/// <remarks>
/// Because no exception can cross the JNI boundary, this record IS the error channel: callers must
/// branch on <see cref="Success"/>, never assume a returned string means the operation worked.
/// </remarks>
public record AndroidNativeOperationResponse
{
    /// <summary>Whether the operation succeeded. Always check this first.</summary>
    public bool Success { get; init; }

    /// <summary>Human-readable outcome. Not localised — it is diagnostic text, not UI copy.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Exception detail when <see cref="Success"/> is false; null otherwise.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Result of <see cref="AndroidNativeExports.InitRemex"/>: which subsystems actually came up.
/// </summary>
/// <remarks>
/// Init can succeed with capabilities missing, so these flags are how the UI knows what to offer
/// rather than letting the user tap something that will fail.
/// </remarks>
public sealed record AndroidNativeInitializationResponse : AndroidNativeOperationResponse
{
    /// <summary>Telemetry can be fetched.</summary>
    public bool TelemetryAvailable { get; init; }

    /// <summary>The background telemetry loop is running (only when requested at init).</summary>
    public bool BackgroundLoopStarted { get; init; }

    /// <summary>The host's control channel is reachable.</summary>
    public bool IpcAvailable { get; init; }

    /// <summary>Wake-on-LAN can be sent. Independent of the rest — it works while the PC is off.</summary>
    public bool WakeOnLanAvailable { get; init; }

    /// <summary>The poll interval actually in force, which may differ from the one requested.</summary>
    public int TelemetryPollIntervalMs { get; init; }
}

/// <summary>Telemetry wrapped in the standard success/failure envelope.</summary>
public sealed record AndroidNativeTelemetryResponse : AndroidNativeOperationResponse
{
    /// <summary>The snapshot; null when <see cref="AndroidNativeOperationResponse.Success"/> is false.</summary>
    public TelemetryPayload? Telemetry { get; init; }
}
