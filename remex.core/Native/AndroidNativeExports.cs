using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
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
    private static IntPtr _onConnectionErrorMethodId;
    private static IntPtr _onDesktopStreamDescriptorMethodId;
    private static IntPtr _onDesktopDisplayCatalogMethodId;
    private static IntPtr _onDesktopCursorStateMethodId;
    private static IntPtr _onDesktopCursorShapeMethodId;
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
        _onConnectionErrorMethodId = IntPtr.Zero;
        _onDesktopStreamDescriptorMethodId = IntPtr.Zero;
        _onDesktopDisplayCatalogMethodId = IntPtr.Zero;
        _onDesktopCursorStateMethodId = IntPtr.Zero;
        _onDesktopCursorBinaryMethodId = IntPtr.Zero;
        _onDesktopCursorShapeMethodId = IntPtr.Zero;
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
                var onConnectionErrorMethodId = GetRequiredCallbackMethodId(env, clazz, "onConnectionError", "(Ljava/lang/String;)V");
                var onDesktopStreamDescriptorMethodId = GetRequiredCallbackMethodId(env, clazz, "onDesktopStreamDescriptor", "(Ljava/lang/String;)V");
                var onDesktopDisplayCatalogMethodId = GetRequiredCallbackMethodId(env, clazz, "onDesktopDisplayCatalog", "(Ljava/lang/String;)V");
                var onDesktopCursorStateMethodId = GetRequiredCallbackMethodId(env, clazz, "onDesktopCursorState", "(Ljava/lang/String;)V");
                var onDesktopCursorBinaryMethodId = GetRequiredCallbackMethodId(env, clazz, "onDesktopCursorBinary", "([B)V");
                var onDesktopCursorShapeMethodId = GetRequiredCallbackMethodId(env, clazz, "onDesktopCursorShape", "(Ljava/lang/String;)V");

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
                    || onConnectionErrorMethodId == IntPtr.Zero
                    || onDesktopStreamDescriptorMethodId == IntPtr.Zero
                    || onDesktopDisplayCatalogMethodId == IntPtr.Zero
                    || onDesktopCursorStateMethodId == IntPtr.Zero
                    || onDesktopCursorBinaryMethodId == IntPtr.Zero
                    || onDesktopCursorShapeMethodId == IntPtr.Zero)
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
                _onConnectionErrorMethodId = onConnectionErrorMethodId;
                _onDesktopStreamDescriptorMethodId = onDesktopStreamDescriptorMethodId;
                _onDesktopDisplayCatalogMethodId = onDesktopDisplayCatalogMethodId;
                _onDesktopCursorStateMethodId = onDesktopCursorStateMethodId;
                _onDesktopCursorBinaryMethodId = onDesktopCursorBinaryMethodId;
                _onDesktopCursorShapeMethodId = onDesktopCursorShapeMethodId;
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

            _ = Task.Run(async () =>
            {
                try
                {
                    var (host, port, clientId, spkiHash) = GetDesktopEndpoint();
                    await RemexDesktopClient.Current.StartStreamAsync(host, port, config, clientId, spkiHash);
                }
                catch (Exception ex) { JniHelper.AndroidLogE("RemexNative", $"StartDesktopStream failed: {ex.Message}"); }
            });
        });

    /// <summary>Stops the Remote Desktop stream. Safe to call when no stream is running.</summary>
    [UnmanagedCallersOnly(EntryPoint = "Java_com_clindsay94_remex_RemexCoreClient_StopDesktopStreamNative")]
    public static void StopDesktopStream(IntPtr env, IntPtr thiz)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await RemexDesktopClient.Current.StopStreamAsync();
                await RemexDesktopClient.Current.DisconnectAsync();
            }
            catch (Exception ex) { JniHelper.AndroidLogE("RemexNative", $"StopDesktopStream failed: {ex.Message}"); }
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

            _ = Task.Run(async () =>
            {
                try
                {
                    var (host, port, clientId, spkiHash) = GetDesktopEndpoint();
                    await RemexDesktopClient.Current.SendPointerBatchAsync(host, port, batch, clientId, spkiHash);
                }
                catch (Exception ex) { JniHelper.AndroidLogE("RemexNative", $"SendDesktopPointerBatch failed: {ex.Message}"); }
            });
        });

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
    /// <param name="clientVersionPtr">Client version, recorded by the host.</param>
    /// <param name="clientIdPtr">Stable client identity; the key the host files the pairing under.</param>
    /// <returns>JSON <see cref="AndroidNativeOperationResponse"/>.</returns>
    /// <remarks>
    /// Pairing is the ONLY authentication path for a non-loopback client, so a change here can brick
    /// every device with no clear error on either end. Follow with <see cref="FetchPairingPinNative"/>
    /// then <see cref="SubmitPairingPinNative"/>.
    /// </remarks>
    [UnmanagedCallersOnly(EntryPoint = "Java_com_clindsay94_remex_RemexCoreClient_StartPairingNative")]
    public static IntPtr StartPairingNative(IntPtr env, IntPtr thiz, IntPtr hostUrlPtr, IntPtr clientNamePtr, IntPtr clientVersionPtr, IntPtr clientIdPtr)
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
                    using (var tcp = new System.Net.Sockets.TcpClient { NoDelay = true })
                    {
                        var probeTask = tcp.ConnectAsync(uri.Host, uri.Port);
                        var probeWon = probeTask.Wait(TimeSpan.FromSeconds(10));
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

                    // Phase 1: connect (TLS handshake + HTTP/1.1 upgrade). Bounded so a wedged TLS
                    // doesn't hang the JNI thread.
                    using (var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                    {
                        try
                        {
                            ws.ConnectAsync(uri, connectCts.Token).GetAwaiter().GetResult();
                        }
                        catch (OperationCanceledException) when (connectCts.IsCancellationRequested)
                        {
                            return $"ERROR: {PairingErrorCodes.TlsTimeout}: TLS/upgrade timed out after 20s — TCP reached {uri.Host}:{uri.Port} but TLS handshake or WebSocket upgrade did not complete (check host cert and that path '{uri.AbsolutePath}' is mapped)";
                        }
                    }

                    Console.Error.WriteLine("[Pairing] Phase 2 — WebSocket connected. Sending PairingRequest, awaiting PairingResponse (60s budget)");

                    // Phase 2: pairing handshake (send PairingRequest, await PairingResponse).
                    // Generous budget — host generates PIN, derives ECDH session key, computes HMAC, and sends back.
                    // Should be fast (<1s) but allow margin for first-time TLS sessions, slow hardware, etc.
                    using (var handshakeCts = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
                    {
                        client = new PairingClient(ws, log: msg => Console.Error.WriteLine($"[PairingClient] {msg}"))
                        {
                            ClientId = clientId
                        };
                        try
                        {
                            _activePairingResponse = client.StartPairingAsync(clientName, clientVersion, handshakeCts.Token).GetAwaiter().GetResult();
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
                catch (Exception ex)
                {
                    _activePairingClient = null;
                    Console.Error.WriteLine($"[Pairing] StartPairing failed: {ex.GetType().Name}: {ex.Message}");
                    return $"ERROR: {PairingErrorCodes.Unexpected}: {ex.GetType().Name}: {ex.Message}";
                }
                finally
                {
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
    public static IntPtr SubmitPairingPinNative(IntPtr env, IntPtr thiz, IntPtr pinPtr)
    {
        return Export(env, () =>
        {
            // Marshal inside the Export guard (JNI-5 / RemEx-85i).
            var pin = JniHelper.ReadJString(env, pinPtr);

            // Serialize against StartPairing/Clear so the session statics aren't disposed-then-used
            // by a concurrent pairing export (JNI-4 / RemEx-8ay).
            lock (PairingSyncRoot)
            {
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
                    using (var completeCts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                    {
                        try
                        {
                            success = client.CompletePairingAsync(pin, _activePairingResponse, completeCts.Token).GetAwaiter().GetResult();
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
    public static IntPtr FetchPairingPinNative(IntPtr env, IntPtr thiz)
    {
        return Export(env, () =>
        {
            // Serialize against StartPairing/Submit/Clear so the session statics aren't disposed-then-used
            // by a concurrent pairing export (JNI-4 / RemEx-8ay). The 5s budget below caps how long a
            // concurrent manual Submit could wait on this lock.
            lock (PairingSyncRoot)
            {
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
                    using (var fetchCts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                    {
                        try
                        {
                            pinInfo = client.RequestPinAsync(fetchCts.Token).GetAwaiter().GetResult();
                        }
                        catch (OperationCanceledException) when (fetchCts.IsCancellationRequested)
                        {
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
                    // NON-DESTRUCTIVE CONTRACT (§3.3): never ClearActivePairingState() on any path in
                    // this export. Whatever went wrong auto-fetching the PIN, the pairing session must
                    // stay valid so the user can still type the PIN in manually.
                    Console.Error.WriteLine($"[Pairing] FetchPairingPin failed: {ex.GetType().Name}: {ex.Message}");
                    return $"ERROR: {PairingErrorCodes.Unexpected}: {ex.GetType().Name}: {ex.Message}";
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

        // Fire-and-forget: WOL is a UDP broadcast with no acknowledgement.
        // Blocking the JNI thread for I/O is not safe — dispatch to the thread pool.
        // Persistent failures are surfaced to the user via the Android toast/status mechanism
        // that observes RemexNativeClient.Current.ConnectionStateChanged.
        _ = Task.Run(async () =>
        {
            try
            {
                await service.WakeAsync(macAddress, effectiveBroadcastIp, effectivePort);
            }
            catch (Exception ex) { JniHelper.AndroidLogE("RemexNative", $"WakeAsync failed: {ex.Message}"); }
        });

        return SerializeOperationSuccess($"Wake-on-LAN dispatched to {macAddress}.");
    }

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
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var (host, port, clientId, spkiHash) = GetDesktopEndpoint();
                        await RemexDesktopClient.Current.StartStreamAsync(host, port, message.DesktopConfig ?? new DesktopConfig(), clientId, spkiHash);
                    }
                    catch (Exception ex) { JniHelper.AndroidLogE("RemexNative", $"DesktopStart failed: {ex.Message}"); }
                });
                return true;

            case MessageTypes.DesktopInput when message.InputEvent != null:
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var (host, port, clientId, spkiHash) = GetDesktopEndpoint();
                        await RemexDesktopClient.Current.SendInputAsync(host, port, message.InputEvent, clientId, spkiHash);
                    }
                    catch (Exception ex) { JniHelper.AndroidLogE("RemexNative", $"DesktopInput failed: {ex.Message}"); }
                });
                return true;

            case MessageTypes.DesktopConfig when message.DesktopConfig != null:
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var (host, port, clientId, spkiHash) = GetDesktopEndpoint();
                        await RemexDesktopClient.Current.StartStreamAsync(host, port, message.DesktopConfig, clientId, spkiHash);
                    }
                    catch (Exception ex) { JniHelper.AndroidLogE("RemexNative", $"DesktopConfig failed: {ex.Message}"); }
                });
                return true;

            case MessageTypes.DesktopDisplayQuery:
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var (host, port, clientId, spkiHash) = GetDesktopEndpoint();
                        await RemexDesktopClient.Current.RequestDisplayCatalogAsync(host, port, clientId, spkiHash);
                    }
                    catch (Exception ex) { JniHelper.AndroidLogE("RemexNative", $"DesktopDisplayQuery failed: {ex.Message}"); }
                });
                return true;

            case MessageTypes.DesktopTargetSwitch when message.DesktopTargetSwitch != null:
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var (host, port, clientId, spkiHash) = GetDesktopEndpoint();
                        await RemexDesktopClient.Current.SwitchTargetAsync(host, port, message.DesktopTargetSwitch, clientId, spkiHash);
                    }
                    catch (Exception ex) { JniHelper.AndroidLogE("RemexNative", $"DesktopTargetSwitch failed: {ex.Message}"); }
                });
                return true;

            case MessageTypes.DesktopWindowQuery when message.DesktopWindowQuery != null:
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var (host, port, clientId, spkiHash) = GetDesktopEndpoint();
                        await RemexDesktopClient.Current.QueryWindowsAsync(host, port, message.DesktopWindowQuery, clientId, spkiHash);
                    }
                    catch (Exception ex) { JniHelper.AndroidLogE("RemexNative", $"DesktopWindowQuery failed: {ex.Message}"); }
                });
                return true;

            case MessageTypes.DesktopWindowAction when message.DesktopWindowAction != null:
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var (host, port, clientId, spkiHash) = GetDesktopEndpoint();
                        await RemexDesktopClient.Current.ExecuteWindowActionAsync(host, port, message.DesktopWindowAction, clientId, spkiHash);
                    }
                    catch (Exception ex) { JniHelper.AndroidLogE("RemexNative", $"DesktopWindowAction failed: {ex.Message}"); }
                });
                return true;

            case MessageTypes.DesktopStop:
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await RemexDesktopClient.Current.StopStreamAsync();
                        await RemexDesktopClient.Current.DisconnectAsync();
                    }
                    catch (Exception ex) { JniHelper.AndroidLogE("RemexNative", $"DesktopStop failed: {ex.Message}"); }
                });
                return true;

            // On-demand keyframe (IDR) request after a decoder desync. Routed onto the desktop stream
            // socket so it reaches RemoteDesktopHandler; without this case it fell through to the
            // control /ws channel and was logged as "Unknown message type". (RemEx-bqc / #2a)
            case MessageTypes.DesktopKeyframeRequest:
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var (host, port, clientId, spkiHash) = GetDesktopEndpoint();
                        await RemexDesktopClient.Current.RequestKeyframeAsync(host, port, clientId, spkiHash);
                    }
                    catch (Exception ex) { JniHelper.AndroidLogE("RemexNative", $"DesktopKeyframeRequest failed: {ex.Message}"); }
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

        // Dispatch to the thread pool to avoid blocking the JNI calling thread
        // with a synchronous WebSocket round-trip.
        // Command responses and errors are delivered back to Kotlin via the
        // RegisterCallbackNative callbacks (onConnectionStateChanged, onDesktopError, etc.).
        _ = Task.Run(async () =>
        {
            try
            {
                await RemexNativeClient.Current.SendCommandAsync(command, CancellationToken.None);
            }
            catch (Exception ex) { JniHelper.AndroidLogE("RemexNative", $"SendCommand failed: {ex.Message}"); }
        });

        return SerializeCommandResponse(new CommandResponse(true, "Command dispatched.", null));
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

    private static IntPtr Export(IntPtr env, Func<string> action)
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
                    SerializeOperationFailure("Unhandled native export failure.", ex.Message));
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
