using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Threading;
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

    private static IWakeOnLanService _wakeOnLanService = new WakeOnLanService();
    private static TelemetryPayload? _cachedTelemetry;
    private static AndroidNativeInitRequest _lastInitRequest = new();

    static AndroidNativeExports()
    {
        RemexNativeClient.Current.TelemetryReceived += OnNativeTelemetryReceived;
        RemexNativeClient.Current.ConnectionStateChanged += OnNativeConnectionStateChanged;
        RemexNativeClient.Current.LauncherEntriesReceived += OnNativeLauncherEntriesReceived;
        RemexNativeClient.Current.ProcessListReceived += OnNativeProcessListReceived;
        RemexNativeClient.Current.MessageReceived += OnNativeMessageReceived;

        RemexDesktopClient.Current.FrameReceived += OnNativeFrameReceived;
        RemexDesktopClient.Current.ErrorReceived += OnNativeDesktopError;
        RemexDesktopClient.Current.MetaReceived += OnNativeMetaReceived;
    }

    [UnmanagedCallersOnly(EntryPoint = "Java_com_clindsay94_remex_RemexCoreClient_RegisterCallbackNative")]
    public static void RegisterCallbackNative(IntPtr env, IntPtr thiz, IntPtr callbackObj)
    {
        lock (SyncRoot)
        {
            if (_javaVm == IntPtr.Zero)
            {
                JniHelper.GetJavaVM(env, out _javaVm);
            }

            if (_callbackGlobalRef != IntPtr.Zero)
            {
                JniHelper.DeleteGlobalRef(env, _callbackGlobalRef);
            }

            if (callbackObj == IntPtr.Zero)
            {
                _callbackGlobalRef = IntPtr.Zero;
                _onTelemetryUpdateMethodId = IntPtr.Zero;
                _onConnectionStateChangedMethodId = IntPtr.Zero;
                _onLauncherSyncMethodId = IntPtr.Zero;
                _onProcessListSyncMethodId = IntPtr.Zero;
                _onFrameReceivedMethodId = IntPtr.Zero;
                _onHostInfoUpdateMethodId = IntPtr.Zero;
                _onDesktopErrorMethodId = IntPtr.Zero;
                return;
            }

            _callbackGlobalRef = JniHelper.NewGlobalRef(env, callbackObj);
            var clazz = JniHelper.GetObjectClass(env, _callbackGlobalRef);
            _onTelemetryUpdateMethodId = JniHelper.GetMethodID(env, clazz, "onTelemetryUpdate", "(Ljava/lang/String;)V");
            _onConnectionStateChangedMethodId = JniHelper.GetMethodID(env, clazz, "onConnectionStateChanged", "(Z)V");
            _onLauncherSyncMethodId = JniHelper.GetMethodID(env, clazz, "onLauncherSync", "(Ljava/lang/String;)V");
            _onProcessListSyncMethodId = JniHelper.GetMethodID(env, clazz, "onProcessListSync", "(Ljava/lang/String;)V");
            _onFrameReceivedMethodId = JniHelper.GetMethodID(env, clazz, "onFrameReceived", "([B)V");
            _onHostInfoUpdateMethodId = JniHelper.GetMethodID(env, clazz, "onHostInfoUpdate", "(Ljava/lang/String;)V");
            _onDesktopErrorMethodId = JniHelper.GetMethodID(env, clazz, "onDesktopError", "(Ljava/lang/String;)V");
            _onDesktopMetaMethodId = JniHelper.GetMethodID(env, clazz, "onDesktopMeta", "(Ljava/lang/String;)V");
            
            // Clean up the local class ref
            JniHelper.DeleteLocalRef(env, clazz);
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
                var (host, port, accessKey) = GetDesktopEndpoint();
                await RemexDesktopClient.Current.StartStreamAsync(host, port, config, accessKey);
            }
            catch { }
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
            catch { }
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

    [UnmanagedCallersOnly(EntryPoint = "Java_com_clindsay94_remex_RemexCoreClient_StartMdnsDiscoveryNative")]
    public static void StartMdnsDiscovery(IntPtr env, IntPtr thiz)
    {
    }

    private static string HandleInitialize(string? initJson)
    {
        var initRequest = string.IsNullOrWhiteSpace(initJson)
            ? new AndroidNativeInitRequest()
            : RemexJson.Deserialize(initJson, RemexJsonSerializerContext.Default.AndroidNativeInitRequest) ?? new AndroidNativeInitRequest();

        lock (SyncRoot)
        {
            _lastInitRequest = initRequest;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await RemexNativeClient.Current.ConnectAsync(initRequest.Host, initRequest.Port, initRequest.AccessKey);
            }
            catch { }
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

        try
        {
            service.WakeAsync(macAddress, effectiveBroadcastIp, effectivePort).GetAwaiter().GetResult();
            return SerializeOperationSuccess($"Wake-on-LAN packet sent to {macAddress}.");
        }
        catch (Exception ex)
        {
            return SerializeOperationFailure($"Failed to send Wake-on-LAN packet to {macAddress}.", ex.ToString());
        }
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

        _ = Task.Run(async () =>
        {
            try
            {
                await RemexNativeClient.Current.SendMessageAsync(message);
            }
            catch { }
        });

        return SerializeOperationSuccess("Message dispatched.");
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
                        var (host, port, accessKey) = GetDesktopEndpoint();
                        await RemexDesktopClient.Current.StartStreamAsync(host, port, message.DesktopConfig ?? new DesktopConfig(), accessKey);
                    }
                    catch { }
                });
                return true;

            case MessageTypes.DesktopInput when message.InputEvent != null:
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var (host, port, accessKey) = GetDesktopEndpoint();
                        await RemexDesktopClient.Current.SendInputAsync(host, port, message.InputEvent, accessKey);
                    }
                    catch { }
                });
                return true;

            case MessageTypes.DesktopConfig when message.DesktopConfig != null:
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var (host, port, accessKey) = GetDesktopEndpoint();
                        await RemexDesktopClient.Current.StartStreamAsync(host, port, message.DesktopConfig, accessKey);
                    }
                    catch { }
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
                    catch { }
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

        try
        {
            var response = RemexNativeClient.Current.SendCommandAsync(command, CancellationToken.None).GetAwaiter().GetResult();
            return SerializeCommandResponse(response);
        }
        catch (Exception ex)
        {
            return SerializeCommandResponse(new CommandResponse(false, "Command dispatch failed.", ex.ToString()));
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

    private static void OnNativeMetaReceived(DesktopMeta meta)
    {
        NotifyJavaData(_onDesktopMetaMethodId, RemexJson.Serialize(meta, RemexJsonSerializerContext.Default.DesktopMeta));
    }

    private static void NotifyJavaFrame(byte[] frame)
    {
        IntPtr env = IntPtr.Zero;
        lock (SyncRoot)
        {
            if (_javaVm == IntPtr.Zero || _callbackGlobalRef == IntPtr.Zero || _onFrameReceivedMethodId == IntPtr.Zero) return;
            if (JniHelper.AttachCurrentThread(_javaVm, out env, IntPtr.Zero) != 0) return;
        }

        IntPtr jArray = IntPtr.Zero;
        try
        {
            jArray = JniHelper.NewByteArray(env, frame.Length);
            JniHelper.SetByteArrayRegion(env, jArray, 0, frame.Length, frame);
            JniHelper.CallVoidMethod(env, _callbackGlobalRef, _onFrameReceivedMethodId, jArray);
        }
        finally 
        {
            if (jArray != IntPtr.Zero)
            {
                JniHelper.DeleteLocalRef(env, jArray);
            }
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
    }

    private static (string Host, int Port, string AccessKey) GetDesktopEndpoint()
    {
        lock (SyncRoot)
        {
            return (_lastInitRequest.Host, _lastInitRequest.Port, _lastInitRequest.AccessKey);
        }
    }

    private static void NotifyJavaData(IntPtr methodId, string json)
    {
        IntPtr env = IntPtr.Zero;
        lock (SyncRoot)
        {
            if (_javaVm == IntPtr.Zero || _callbackGlobalRef == IntPtr.Zero || methodId == IntPtr.Zero) return;
            if (JniHelper.AttachCurrentThread(_javaVm, out env, IntPtr.Zero) != 0) return;
        }

        IntPtr jString = IntPtr.Zero;
        try
        {
            jString = JniHelper.CreateJString(env, json);
            JniHelper.CallVoidMethod(env, _callbackGlobalRef, methodId, jString);
        }
        finally 
        {
            if (jString != IntPtr.Zero)
            {
                JniHelper.DeleteLocalRef(env, jString);
            }
        }
    }

    private static void NotifyJavaConnectionState(bool isConnected)
    {
        IntPtr env = IntPtr.Zero;
        lock (SyncRoot)
        {
            if (_javaVm == IntPtr.Zero || _callbackGlobalRef == IntPtr.Zero || _onConnectionStateChangedMethodId == IntPtr.Zero) return;
            if (JniHelper.AttachCurrentThread(_javaVm, out env, IntPtr.Zero) != 0) return;
        }

        try
        {
            JniHelper.CallVoidMethod(env, _callbackGlobalRef, _onConnectionStateChangedMethodId, isConnected);
        }
        finally { }
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
    public string AccessKey { get; init; } = string.Empty;
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
