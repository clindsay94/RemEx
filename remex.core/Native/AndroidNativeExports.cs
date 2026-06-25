using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Models.IPC;
using Remex.Core.Serialization;
using Remex.Core.Services;
using Remex.Core.Services.Network;

namespace Remex.Core.Native;

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
        RemexDesktopClient.Current.CursorShapeReceived += OnNativeCursorShapeReceived;

        EnsureOutboundSendLoopStarted();
    }

    // In .NET 10 Android NativeAOT, the built-in JNI export `Java_net_dot_android_crypto_DotnetProxyTrustManager_verifyRemoteCertificate`
    // is exported by `libSystem.Security.Cryptography.Native.Android.a`. However, when Android's JVM invokes it via `verifyRemoteCertificate`,
    // it can fail to map if the JVM expects a different signature or if the method isn't registered dynamically by `JNI_OnLoad`.
    // Since Remex completely bypasses the OS trust manager via `ws.Options.RemoteCertificateValidationCallback = ... => true` and validates
    // the SPKI hash manually in `SubmitPairingPin` / `PinnedHostStore`, we can safely return true here to satisfy the Android
    // SslStream TLS handshake state machine which is forced to call this Java proxy.
    [UnmanagedCallersOnly(EntryPoint = "Java_net_dot_android_crypto_DotnetProxyTrustManager_verifyRemoteCertificate")]
    public static bool Java_net_dot_android_crypto_DotnetProxyTrustManager_verifyRemoteCertificate(IntPtr env, IntPtr clazz, long handle)
    {
        return true;
    }

    [UnmanagedCallersOnly(EntryPoint = "Java_net_dot_android_crypto_DotnetProxyTrustManager_1X509TrustManager_verifyRemoteCertificate")]
    public static bool Java_net_dot_android_crypto_DotnetProxyTrustManager_1X509TrustManager_verifyRemoteCertificate(IntPtr env, IntPtr thiz, long handle)
    {
        return true;
    }

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

    [UnmanagedCallersOnly(EntryPoint = "Java_com_clindsay94_remex_RemexCoreClient_InitRemexNative")]
    public static IntPtr InitRemex(IntPtr env, IntPtr thiz, IntPtr initJsonUtf8)
        => Export(env, () => HandleInitialize(JniHelper.ReadJString(env, initJsonUtf8)));

    [UnmanagedCallersOnly(EntryPoint = "Java_com_clindsay94_remex_RemexCoreClient_WakePcNative")]
    public static IntPtr WakePc(IntPtr env, IntPtr thiz, IntPtr macAddressUtf8, IntPtr broadcastIpUtf8, int port)
        => Export(env, () => HandleSendWakeOnLan(JniHelper.ReadJString(env, macAddressUtf8), JniHelper.ReadJString(env, broadcastIpUtf8), port));

    [UnmanagedCallersOnly(EntryPoint = "Java_com_clindsay94_remex_RemexCoreClient_GetTelemetryNative")]
    public static IntPtr GetTelemetry(IntPtr env, IntPtr thiz)
        => Export(env, HandleRequestTelemetry);

    [UnmanagedCallersOnly(EntryPoint = "Java_com_clindsay94_remex_RemexCoreClient_SendMessageNative")]
    public static IntPtr SendMessage(IntPtr env, IntPtr thiz, IntPtr messageJsonUtf8)
        => Export(env, () => HandleDispatchMessage(JniHelper.ReadJString(env, messageJsonUtf8)));

    [UnmanagedCallersOnly(EntryPoint = "Java_com_clindsay94_remex_RemexCoreClient_SendCommandNative")]
    public static IntPtr SendCommand(IntPtr env, IntPtr thiz, IntPtr commandJsonUtf8)
        => Export(env, () => HandleDispatchCommand(JniHelper.ReadJString(env, commandJsonUtf8)));

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
                    var (host, port, clientId, accessKey) = GetDesktopEndpoint();
                    await RemexDesktopClient.Current.StartStreamAsync(host, port, config, clientId, accessKey);
                }
                catch (Exception ex) { JniHelper.AndroidLogE("RemexNative", $"StartDesktopStream failed: {ex.Message}"); }
            });
        });

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
                    var (host, port, clientId, accessKey) = GetDesktopEndpoint();
                    await RemexDesktopClient.Current.SendPointerBatchAsync(host, port, batch, clientId, accessKey);
                }
                catch (Exception ex) { JniHelper.AndroidLogE("RemexNative", $"SendDesktopPointerBatch failed: {ex.Message}"); }
            });
        });

    private static void ClearActivePairingState()
    {
        if (_pairingWebSocket != null)
        {
            try { _pairingWebSocket.Dispose(); } catch { }
            _pairingWebSocket = null;
        }

        _activePairingClient = null;
        _activePairingResponse = null;
    }

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
                        return "ERROR: Host URL is required";
                    if (string.IsNullOrEmpty(clientName))
                        return "ERROR: Client name is required";
                    if (string.IsNullOrEmpty(clientVersion))
                        return "ERROR: Client version is required";
                    if (string.IsNullOrWhiteSpace(clientId))
                        return "ERROR: Client ID is required";

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
                        return $"ERROR: Invalid host URL '{hostUrl}': {ufx.Message}";
                    }

                    Console.Error.WriteLine($"[Pairing] Phase 0 — TCP probe {uri.Host}:{uri.Port} (10s budget)");
                    using (var tcp = new System.Net.Sockets.TcpClient { NoDelay = true })
                    {
                        var probeTask = tcp.ConnectAsync(uri.Host, uri.Port);
                        var probeWon = probeTask.Wait(TimeSpan.FromSeconds(10));
                        if (!probeWon)
                        {
                            return $"ERROR: TCP probe to {uri.Host}:{uri.Port} timed out after 10s — host unreachable, firewall, or wrong IP/port";
                        }
                        if (probeTask.IsFaulted)
                        {
                            var inner = probeTask.Exception?.GetBaseException();
                            return $"ERROR: TCP probe to {uri.Host}:{uri.Port} refused — {inner?.GetType().Name}: {inner?.Message}";
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
                            return $"ERROR: TLS/upgrade timed out after 20s — TCP reached {uri.Host}:{uri.Port} but TLS handshake or WebSocket upgrade did not complete (check host cert and that path '{uri.AbsolutePath}' is mapped)";
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
                            try { ws.Dispose(); } catch { }
                            return "ERROR: Pairing handshake timed out — host did not return PairingResponse within 60s";
                        }
                    }

                    if (_activePairingResponse == null)
                    {
                        _activePairingClient = null;
                        try { ws.Dispose(); } catch { }
                        return "ERROR: Host responded but PairingResponse payload was missing";
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
                    return $"ERROR: {ex.GetType().Name}: {ex.Message}";
                }
                finally
                {
                    // If we created a socket but didn't promote it to _pairingWebSocket, dispose it now.
                    if (ws != null)
                    {
                        try { ws.Dispose(); } catch { }
                    }
                }
            }
        });
    }

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
                        return "ERROR: PIN is required";
                    if (_pairingWebSocket == null || _activePairingResponse == null)
                        return "ERROR: No active pairing session";
                    if (_activePairingClient == null)
                        return "ERROR: Pairing session lost client key state";

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
                            return "ERROR: PIN submission timed out — host did not confirm within 30s";
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
                    return "ERROR: Pairing verification failed (incorrect PIN or session expired)";
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Pairing] SubmitPairingPin failed: {ex.GetType().Name}: {ex.Message}");
                    ClearActivePairingState();
                    return $"ERROR: {ex.GetType().Name}: {ex.Message}";
                }
            }
        });
    }

    [UnmanagedCallersOnly(EntryPoint = "Java_com_clindsay94_remex_RemexCoreClient_GetPinnedHostHashNative")]
    public static IntPtr GetPinnedHostHashNative(IntPtr env, IntPtr thiz, IntPtr hostIdPtr)
    {
        var hostId = JniHelper.ReadJString(env, hostIdPtr);
        return Export(env, () => _pinnedHashes.TryGetValue(hostId ?? "", out var hash) ? hash : "");
    }

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
    {
        lock (SyncRoot)
        {
            if (_javaVm == IntPtr.Zero || _callbackGlobalRef == IntPtr.Zero || _onFrameReceivedMethodId == IntPtr.Zero) return;
        }

        // The captured `frame` is a fresh per-frame array (RemexDesktopClient raises
        // FrameReceived with ms.ToArray()), so holding the reference across the async
        // hand-off is safe. INVARIANT: if that producer is ever changed to pool/reuse
        // buffers, this MUST copy before enqueuing or the Java side will read torn frames.
        PostToJavaThread(env =>
        {
            // Re-read under lock at execution time: the global ref may have been replaced
            // (and the old one deleted) between enqueue and dispatch.
            IntPtr callback, methodId;
            lock (SyncRoot)
            {
                callback = _callbackGlobalRef;
                methodId = _onFrameReceivedMethodId;
            }
            if (callback == IntPtr.Zero || methodId == IntPtr.Zero) return;

            IntPtr jArray = JniHelper.NewByteArray(env, frame.Length);
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
                JniHelper.SetByteArrayRegion(env, jArray, 0, frame.Length, frame);
                JniHelper.CallVoidMethod(env, callback, methodId, jArray);
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

        if (msg.Type is MessageTypes.FileRootsResponse or MessageTypes.FileBrowseResponse or MessageTypes.FileTransferChunk
                       or MessageTypes.FileTransferProgress or MessageTypes.FileTransferEnd
                       or MessageTypes.FileManageResponse or MessageTypes.FileHashResponse or MessageTypes.FileRootManageResponse)
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

public sealed record AndroidNativeInitRequest
{
    public string Host { get; init; } = "localhost";
    public int Port { get; init; } = 5005;
    public string SpkiHash { get; init; } = string.Empty;
    public string? ClientId { get; init; }

    /// <summary>
    /// Base64 reconnect secret persisted by the Android client after pairing (PAIR-1). Supplied on
    /// connect so the native client can answer the host's proof-of-possession challenge. Empty/null
    /// for clients that have not yet paired (or paired before this field existed) — they will be
    /// challenged and must re-pair.
    /// </summary>
    public string? ReconnectSecret { get; init; }

    public int TelemetryPollIntervalMs { get; init; } = 1000;
    public bool StartTelemetryPolling { get; init; } = true;
    public bool WarmupTelemetry { get; init; } = true;
}

public record AndroidNativeOperationResponse
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? Error { get; init; }
}

public sealed record AndroidNativeInitializationResponse : AndroidNativeOperationResponse
{
    public bool TelemetryAvailable { get; init; }
    public bool BackgroundLoopStarted { get; init; }
    public bool IpcAvailable { get; init; }
    public bool WakeOnLanAvailable { get; init; }
    public int TelemetryPollIntervalMs { get; init; }
}

public sealed record AndroidNativeTelemetryResponse : AndroidNativeOperationResponse
{
    public TelemetryPayload? Telemetry { get; init; }
}
