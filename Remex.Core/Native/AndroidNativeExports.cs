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

    private static IWakeOnLanService _wakeOnLanService = new WakeOnLanService();
    private static TelemetryPayload? _cachedTelemetry;
    private static AndroidNativeInitRequest _lastInitRequest = new();
    private static ClientWebSocket? _pairingWebSocket;
    private static PairingClient? _activePairingClient;
    private static PairingResponse? _activePairingResponse;
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
                    || onDesktopDisplayCatalogMethodId == IntPtr.Zero)
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
    }

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
    }

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
    }

    [UnmanagedCallersOnly(EntryPoint = "Java_com_clindsay94_remex_RemexCoreClient_FreeMemory")]
    public static void FreeMemory(IntPtr env, IntPtr thiz, IntPtr pointer)
    {
        if (pointer != IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

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
        var hostUrl = JniHelper.ReadJString(env, hostUrlPtr);
        var clientName = JniHelper.ReadJString(env, clientNamePtr);
        var clientVersion = JniHelper.ReadJString(env, clientVersionPtr);
        var clientId = JniHelper.ReadJString(env, clientIdPtr);

        return Export(env, () =>
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
        });
    }

    [UnmanagedCallersOnly(EntryPoint = "Java_com_clindsay94_remex_RemexCoreClient_SubmitPairingPinNative")]
    public static IntPtr SubmitPairingPinNative(IntPtr env, IntPtr thiz, IntPtr pinPtr)
    {
        var pin = JniHelper.ReadJString(env, pinPtr);

        return Export(env, () =>
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
                    var result = $"OK:{_activePairingResponse.HostId}|{_activePairingResponse.CertificateSpkiHashBase64}";
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
                    effectiveInitRequest.ClientId);
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

    private static void NotifyJavaFrame(byte[] frame)
    {
        IntPtr env = IntPtr.Zero;
        IntPtr vm = IntPtr.Zero;
        IntPtr callback = IntPtr.Zero;
        IntPtr methodId = IntPtr.Zero;

        lock (SyncRoot)
        {
            if (_javaVm == IntPtr.Zero || _callbackGlobalRef == IntPtr.Zero || _onFrameReceivedMethodId == IntPtr.Zero) return;
            vm = _javaVm;
            callback = _callbackGlobalRef;
            methodId = _onFrameReceivedMethodId;
        }

        if (JniHelper.AttachCurrentThread(vm, out env, IntPtr.Zero) != 0) return;

        try
        {
            IntPtr jArray = JniHelper.NewByteArray(env, frame.Length);
            if (jArray == IntPtr.Zero) return;

            try
            {
                JniHelper.SetByteArrayRegion(env, jArray, 0, frame.Length, frame);
                JniHelper.CallVoidMethod(env, callback, methodId, jArray);
            }
            finally
            {
                JniHelper.DeleteLocalRef(env, jArray);
            }
        }
        finally
        {
            JniHelper.DetachCurrentThread(vm);
        }
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
        IntPtr env = IntPtr.Zero;
        IntPtr vm = IntPtr.Zero;
        IntPtr callback = IntPtr.Zero;

        lock (SyncRoot)
        {
            if (_javaVm == IntPtr.Zero || _callbackGlobalRef == IntPtr.Zero || methodId == IntPtr.Zero) return;
            vm = _javaVm;
            callback = _callbackGlobalRef;
        }

        if (JniHelper.AttachCurrentThread(vm, out env, IntPtr.Zero) != 0) return;

        try
        {
            IntPtr jString = JniHelper.CreateJString(env, json);
            if (jString == IntPtr.Zero) return;
            try
            {
                JniHelper.CallVoidMethod(env, callback, methodId, jString);
            }
            finally
            {
                JniHelper.DeleteLocalRef(env, jString);
            }
        }
        finally
        {
            JniHelper.DetachCurrentThread(vm);
        }
    }

    private static void NotifyJavaConnectionState(bool isConnected)
    {
        IntPtr env = IntPtr.Zero;
        IntPtr vm = IntPtr.Zero;
        IntPtr callback = IntPtr.Zero;
        IntPtr methodId = IntPtr.Zero;

        lock (SyncRoot)
        {
            if (_javaVm == IntPtr.Zero || _callbackGlobalRef == IntPtr.Zero || _onConnectionStateChangedMethodId == IntPtr.Zero) return;
            vm = _javaVm;
            callback = _callbackGlobalRef;
            methodId = _onConnectionStateChangedMethodId;
        }

        if (JniHelper.AttachCurrentThread(vm, out env, IntPtr.Zero) != 0) return;

        try
        {
            JniHelper.CallVoidMethod(env, callback, methodId, isConnected);
        }
        finally
        {
            JniHelper.DetachCurrentThread(vm);
        }
    }

    private static IntPtr Export(IntPtr env, Func<string> action)
    {
        try
        {
            return JniHelper.CreateJString(env, action());
        }
        catch (Exception ex)
        {
            return JniHelper.CreateJString(env, "{\"success\":false,\"message\":\"Unhandled native export failure.\",\"error\":\"" + ex.Message + "\"}");
        }
    }

    private static string SerializeOperationSuccess(string message)
        => RemexJson.Serialize(new AndroidNativeOperationResponse { Success = true, Message = message }, RemexJsonSerializerContext.Default.AndroidNativeOperationResponse);

    private static string SerializeOperationFailure(string message, string? error = null)
        => RemexJson.Serialize(new AndroidNativeOperationResponse { Success = false, Message = message, Error = error }, RemexJsonSerializerContext.Default.AndroidNativeOperationResponse);

    private static string SerializeTelemetrySuccess(TelemetryPayload telemetry)
        => RemexJson.Serialize(telemetry, RemexJsonSerializerContext.Default.TelemetryPayload);

    private static string SerializeTelemetryFailure(string message, string? error = null)
        => "{\"success\":false,\"message\":\"" + message + "\"}";

    private static string SerializeCommandResponse(CommandResponse response)
        => RemexJson.Serialize(response, RemexJsonSerializerContext.Default.CommandResponse);


}

public sealed record AndroidNativeInitRequest
{
    public string Host { get; init; } = "localhost";
    public int Port { get; init; } = 5005;
    public string SpkiHash { get; init; } = string.Empty;
    public string? ClientId { get; init; }
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
