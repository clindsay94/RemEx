using System;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Remex.Core.Messages;
using Remex.Core.Models.IPC;
using Remex.Core.Serialization;
using Remex.Core.Services;
using Remex.Core.Services.Network;

namespace Remex.Core.Native;

public static class AndroidNativeExports
{
    private static readonly object SyncRoot = new();
    private static readonly Func<CommandRequest, CancellationToken, Task<CommandResponse>> DefaultIpcDispatcher =
        static (request, cancellationToken) => RemExLocalIPC.SendCommandAsync(request, cancellationToken);

    private static IWakeOnLanService _wakeOnLanService = new WakeOnLanService();
    private static Func<CommandRequest, CancellationToken, Task<CommandResponse>> _ipcDispatcher = DefaultIpcDispatcher;
    private static ITelemetryService? _telemetryService;
    private static CancellationTokenSource? _telemetryLoopCts;
    private static Task? _telemetryLoopTask;
    private static TelemetryPayload? _cachedTelemetry;
    private static AndroidNativeInitRequest _lastInitRequest = new();

    public static void ConfigureWakeOnLanService(IWakeOnLanService? wakeOnLanService)
    {
        lock (SyncRoot)
        {
            _wakeOnLanService = wakeOnLanService ?? new WakeOnLanService();
        }
    }

    public static void ConfigureTelemetryService(ITelemetryService? telemetryService)
    {
        lock (SyncRoot)
        {
            _telemetryService = telemetryService;
        }
    }

    public static void ConfigureIpcDispatcher(Func<CommandRequest, CancellationToken, Task<CommandResponse>>? ipcDispatcher)
    {
        lock (SyncRoot)
        {
            _ipcDispatcher = ipcDispatcher ?? DefaultIpcDispatcher;
        }
    }

    // JNI Entry Points must accept JNIEnv* (env) and jobject (thiz) as the first two parameters

    [UnmanagedCallersOnly(EntryPoint = "Java_com_clindsay94_remex_RemexCoreClient_InitRemex")]
    public static IntPtr InitRemex(IntPtr env, IntPtr thiz, IntPtr initJsonUtf8)
        => Export(env, () => HandleInitializeBackgroundServices(JNIEnv.ReadJString(env, initJsonUtf8)));

    [UnmanagedCallersOnly(EntryPoint = "Java_com_clindsay94_remex_RemexCoreClient_WakePc")]
    public static IntPtr WakePc(IntPtr env, IntPtr thiz, IntPtr macAddressUtf8, IntPtr broadcastIpUtf8, int port)
        => Export(env, () => HandleSendWakeOnLan(JNIEnv.ReadJString(env, macAddressUtf8), JNIEnv.ReadJString(env, broadcastIpUtf8), port));

    [UnmanagedCallersOnly(EntryPoint = "Java_com_clindsay94_remex_RemexCoreClient_GetTelemetry")]
    public static IntPtr GetTelemetry(IntPtr env, IntPtr thiz)
        => Export(env, HandleRequestTelemetry);

    [UnmanagedCallersOnly(EntryPoint = "Java_com_clindsay94_remex_RemexCoreClient_SendCommand")]
    public static IntPtr SendCommand(IntPtr env, IntPtr thiz, IntPtr commandJsonUtf8)
        => Export(env, () => HandleDispatchIpcCommand(JNIEnv.ReadJString(env, commandJsonUtf8)));

    [UnmanagedCallersOnly(EntryPoint = "Java_com_clindsay94_remex_RemexCoreClient_FreeMemory")]
    public static void FreeMemory(IntPtr env, IntPtr thiz, IntPtr pointer)
    {
        if (pointer != IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "Java_com_clindsay94_remex_RemexCoreClient_StartMdnsDiscovery")]
    public static void StartMdnsDiscovery(IntPtr env, IntPtr thiz)
    {
        // TODO: Wire up MdnsDiscoveryService trigger here
    }

    internal static string HandleInitializeBackgroundServices(string? initJson)
    {
        var initRequest = string.IsNullOrWhiteSpace(initJson)
            ? new AndroidNativeInitRequest()
            : RemexJson.Deserialize(initJson, RemexJsonSerializerContext.Default.AndroidNativeInitRequest) ?? new AndroidNativeInitRequest();

        var telemetryService = GetTelemetryService();

        lock (SyncRoot)
        {
            _lastInitRequest = Normalize(initRequest);
            RestartTelemetryLoopUnsafe(_lastInitRequest, telemetryService);
        }

        if (_lastInitRequest.WarmupTelemetry && telemetryService != null)
        {
            try
            {
                _cachedTelemetry = telemetryService.GetTelemetryAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            catch
            {
                _cachedTelemetry = null;
            }
        }

        var response = new AndroidNativeInitializationResponse
        {
            Success = true,
            Message = "Android native exports initialized.",
            TelemetryAvailable = telemetryService != null,
            BackgroundLoopStarted = telemetryService != null && _lastInitRequest.StartTelemetryPolling,
            IpcAvailable = true,
            WakeOnLanAvailable = true,
            TelemetryPollIntervalMs = _lastInitRequest.TelemetryPollIntervalMs,
        };

        return RemexJson.Serialize(response, RemexJsonSerializerContext.Default.AndroidNativeInitializationResponse);
    }

    internal static string HandleSendWakeOnLan(string? macAddress, string? broadcastIp, int port)
    {
        if (string.IsNullOrWhiteSpace(macAddress))
        {
            return SerializeOperationFailure("Wake-on-LAN requires a MAC address.");
        }

        var service = GetWakeOnLanService();
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

    internal static string HandleRequestTelemetry()
    {
        var telemetry = _cachedTelemetry;
        if (telemetry != null)
        {
            return SerializeTelemetrySuccess(telemetry);
        }

        var telemetryService = GetTelemetryService();
        if (telemetryService == null)
        {
            return SerializeTelemetryFailure("Telemetry provider is not configured.");
        }

        try
        {
            telemetry = telemetryService.GetTelemetryAsync(CancellationToken.None).GetAwaiter().GetResult();
            _cachedTelemetry = telemetry;
            return SerializeTelemetrySuccess(telemetry);
        }
        catch (Exception ex)
        {
            return SerializeTelemetryFailure("Failed to retrieve telemetry.", ex.ToString());
        }
    }

    internal static string HandleDispatchIpcCommand(string? commandJson)
    {
        if (string.IsNullOrWhiteSpace(commandJson))
        {
            return SerializeCommandResponse(new CommandResponse(false, "IPC command JSON is required.", null));
        }

        var command = RemexJson.Deserialize(commandJson, RemexJsonSerializerContext.Default.CommandRequest);
        if (command == null)
        {
            return SerializeCommandResponse(new CommandResponse(false, "Failed to deserialize IPC command.", null));
        }

        try
        {
            var response = GetIpcDispatcher()(command, CancellationToken.None).GetAwaiter().GetResult();
            return SerializeCommandResponse(response);
        }
        catch (Exception ex)
        {
            return SerializeCommandResponse(new CommandResponse(false, "IPC command dispatch failed.", ex.ToString()));
        }
    }

    private static AndroidNativeInitRequest Normalize(AndroidNativeInitRequest request)
        => request with { TelemetryPollIntervalMs = request.TelemetryPollIntervalMs > 0 ? request.TelemetryPollIntervalMs : 1000 };

    private static void RestartTelemetryLoopUnsafe(AndroidNativeInitRequest initRequest, ITelemetryService? telemetryService)
    {
        _telemetryLoopCts?.Cancel();
        _telemetryLoopCts?.Dispose();
        _telemetryLoopTask = null;

        if (!initRequest.StartTelemetryPolling || telemetryService == null)
        {
            _telemetryLoopCts = null;
            return;
        }

        _telemetryLoopCts = new CancellationTokenSource();
        var cancellationToken = _telemetryLoopCts.Token;
        var interval = TimeSpan.FromMilliseconds(initRequest.TelemetryPollIntervalMs);
        _telemetryLoopTask = Task.Run(async () =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    _cachedTelemetry = await telemetryService.GetTelemetryAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                }

                try
                {
                    await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }, cancellationToken);
    }

    private static IWakeOnLanService GetWakeOnLanService()
    {
        lock (SyncRoot)
        {
            return _wakeOnLanService;
        }
    }

    private static ITelemetryService? GetTelemetryService()
    {
        lock (SyncRoot)
        {
            return _telemetryService;
        }
    }

    private static Func<CommandRequest, CancellationToken, Task<CommandResponse>> GetIpcDispatcher()
    {
        lock (SyncRoot)
        {
            return _ipcDispatcher;
        }
    }

    private static IntPtr Export(IntPtr env, Func<string> action)
    {
        try
        {
            return JNIEnv.CreateJString(env, action());
        }
        catch
        {
            return JNIEnv.CreateJString(env, "{\"success\":false,\"message\":\"Unhandled native export failure.\"}");
        }
    }

    private static string SerializeOperationSuccess(string message)
        => RemexJson.Serialize(new AndroidNativeOperationResponse { Success = true, Message = message }, RemexJsonSerializerContext.Default.AndroidNativeOperationResponse);

    private static string SerializeOperationFailure(string message, string? error = null)
        => RemexJson.Serialize(new AndroidNativeOperationResponse { Success = false, Message = message, Error = error }, RemexJsonSerializerContext.Default.AndroidNativeOperationResponse);

    private static string SerializeTelemetrySuccess(TelemetryPayload telemetry)
        => RemexJson.Serialize(new AndroidNativeTelemetryResponse { Success = true, Telemetry = telemetry }, RemexJsonSerializerContext.Default.AndroidNativeTelemetryResponse);

    private static string SerializeTelemetryFailure(string message, string? error = null)
        => RemexJson.Serialize(new AndroidNativeTelemetryResponse { Success = false, Message = message, Error = error }, RemexJsonSerializerContext.Default.AndroidNativeTelemetryResponse);

    private static string SerializeCommandResponse(CommandResponse response)
        => RemexJson.Serialize(response, RemexJsonSerializerContext.Default.CommandResponse);
}

public sealed record AndroidNativeInitRequest
{
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