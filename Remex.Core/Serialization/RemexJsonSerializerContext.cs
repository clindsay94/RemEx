using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Models.IPC;
using Remex.Core.Native;

namespace Remex.Core.Serialization;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppEntry))]
[JsonSerializable(typeof(AndroidNativeInitRequest))]
[JsonSerializable(typeof(AndroidNativeInitializationResponse))]
[JsonSerializable(typeof(AndroidNativeOperationResponse))]
[JsonSerializable(typeof(AndroidNativeTelemetryResponse))]
[JsonSerializable(typeof(CardState))]
[JsonSerializable(typeof(CommandRequest))]
[JsonSerializable(typeof(CommandResponse))]
[JsonSerializable(typeof(CustomizationSettings))]
[JsonSerializable(typeof(DashboardProfile))]
[JsonSerializable(typeof(DesktopConfig))]
[JsonSerializable(typeof(DesktopMeta))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(HostCapabilities))]
[JsonSerializable(typeof(InputEvent))]
[JsonSerializable(typeof(List<AppEntry>))]
[JsonSerializable(typeof(List<CardState>))]
[JsonSerializable(typeof(List<ProcessInfo>))]
[JsonSerializable(typeof(List<SensorReading>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(MonitorInfo))]
[JsonSerializable(typeof(ProcessInfo))]
[JsonSerializable(typeof(RemexMessage))]
[JsonSerializable(typeof(SensorReading))]
[JsonSerializable(typeof(TelemetryPayload))]
public partial class RemexJsonSerializerContext : JsonSerializerContext
{
}

public static class RemexJson
{
    public static JsonSerializerOptions Compact { get; } = new(RemexJsonSerializerContext.Default.Options);

    public static JsonTypeInfo<T> TypeInfo<T>()
        => (JsonTypeInfo<T>)RemexJsonSerializerContext.Default.GetTypeInfo(typeof(T))!;

    public static string Serialize<T>(T value, JsonTypeInfo<T> typeInfo)
        => JsonSerializer.Serialize(value, typeInfo);

    public static string SerializeIndented<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        JsonSerializer.Serialize(writer, value, typeInfo);
        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static byte[] SerializeToUtf8Bytes<T>(T value, JsonTypeInfo<T> typeInfo)
        => JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);

    public static async Task SerializeIndentedAsync<T>(Stream stream, T value, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken = default)
    {
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        JsonSerializer.Serialize(writer, value, typeInfo);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static T? Deserialize<T>(string json, JsonTypeInfo<T> typeInfo)
        => JsonSerializer.Deserialize(json, typeInfo);

    public static T? Deserialize<T>(ReadOnlySpan<byte> utf8Json, JsonTypeInfo<T> typeInfo)
        => JsonSerializer.Deserialize(utf8Json, typeInfo);

    public static ValueTask<T?> DeserializeAsync<T>(Stream utf8Json, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken = default)
        => JsonSerializer.DeserializeAsync(utf8Json, typeInfo, cancellationToken);
}