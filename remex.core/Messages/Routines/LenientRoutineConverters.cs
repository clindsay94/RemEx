using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Remex.Core.Routines;

namespace Remex.Core.Messages.Routines;

// WHY THESE EXIST (routines spec T18, RemEx-pp0rt.3). System.Text.Json throws on a value of the wrong
// JSON type ("port": "nine"). Inside a RemexMessage that throw makes MessageSerializer.Deserialize return
// a null envelope, and a null envelope makes PingPongHandler's receive loop treat it as a disconnect -
// one malformed routine would end the whole session, for every feature, and a buggy or hostile phone
// could do it on every reconnect. So routine JSON is read in layers that each fail SMALL:
//
//   * a routine the list cannot read becomes a placeholder Routine { IsMalformed = true } in place,
//     so the host rejects just that routine (invalid_field) with a per-routine result;
//   * a step request whose step cannot be read gets RoutineStep { IsMalformed = true };
//   * a whole payload that cannot be read becomes a null slot on a NON-null envelope, which the
//     handler answers with a result instead of dropping the socket.
//
// NativeAOT-safe: nested reads go through options.GetTypeInfo, which resolves from the source-generated
// RemexJsonSerializerContext the options were built from - no reflection. Every T used here must be
// [JsonSerializable] in that context. The Kotlin mirror treats a wrong-typed field the same way (the
// routine is malformed), and the shared invalid.invalid_field fixtures hold the two sides to it.

/// <summary>
/// Reads one routines payload slot of <see cref="RemexMessage"/>; an unreadable payload becomes null.
/// </summary>
public sealed class LenientRoutinePayloadConverter<T> : JsonConverter<T>
    where T : class
{
    public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return LenientJson.TryDeserialize(document.RootElement, LenientJson.TypeInfo<T>(options));
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, value, LenientJson.TypeInfo<T>(options));
}

/// <summary>
/// Reads a routine list element by element; an unreadable routine becomes a malformed placeholder in
/// its own position, carrying its <c>id</c> when that much was readable.
/// </summary>
public sealed class LenientRoutineListConverter : JsonConverter<List<Routine>>
{
    public override List<Routine>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var typeInfo = LenientJson.TypeInfo<Routine>(options);
        var routines = new List<Routine>(document.RootElement.GetArrayLength());
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var routine = element.ValueKind == JsonValueKind.Object
                ? LenientJson.TryDeserialize(element, typeInfo)
                : null;

            routines.Add(routine ?? new Routine
            {
                Id = LenientJson.TryGetString(element, "id"),
                IsMalformed = true,
                // Clone: the document is disposed when this method returns.
                RawJson = element.Clone(),
            });
        }

        return routines;
    }

    /// <remarks>
    /// A malformed routine is written back as the JSON it arrived as, never as the placeholder: the
    /// placeholder has lost every field, and writing it would silently replace the user's routine with
    /// an empty shell. One with no original JSON (built in code) cannot be written at all.
    /// </remarks>
    public override void Write(Utf8JsonWriter writer, List<Routine> value, JsonSerializerOptions options)
    {
        var typeInfo = LenientJson.TypeInfo<Routine>(options);
        writer.WriteStartArray();
        foreach (var routine in value)
        {
            if (routine.IsMalformed)
            {
                if (routine.RawJson is not { } raw)
                {
                    throw new JsonException(
                        $"Refusing to write malformed routine '{routine.Id}' with no original JSON: it would replace the user's routine with an empty placeholder.");
                }

                raw.WriteTo(writer);
                continue;
            }

            JsonSerializer.Serialize(writer, routine, typeInfo);
        }

        writer.WriteEndArray();
    }
}

/// <summary>
/// A list whose elements may not be JSON null (RemEx-pp0rt.3). A null element makes the read fail,
/// so the enclosing routines payload becomes a null slot (rejected), matching the Kotlin reader. The
/// alternative - a C# list that silently carries a null the Kotlin side refuses - would let the two
/// mirrors disagree about the same message.
/// </summary>
public sealed class NoNullElementsListConverter<T> : JsonConverter<List<T>>
    where T : class
{
    public override List<T>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException($"Expected an array of {typeof(T).Name}.");
        }

        var typeInfo = LenientJson.TypeInfo<T>(options);
        var items = new List<T>(document.RootElement.GetArrayLength());
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.Null)
            {
                throw new JsonException($"A {typeof(T).Name} list may not contain null.");
            }

            items.Add(element.Deserialize(typeInfo) ?? throw new JsonException($"A {typeof(T).Name} list may not contain null."));
        }

        return items;
    }

    public override void Write(Utf8JsonWriter writer, List<T> value, JsonSerializerOptions options)
    {
        var typeInfo = LenientJson.TypeInfo<T>(options);
        writer.WriteStartArray();
        foreach (var item in value)
        {
            JsonSerializer.Serialize(writer, item, typeInfo);
        }

        writer.WriteEndArray();
    }
}

/// <summary>
/// Reads a single step; an unreadable step becomes a malformed placeholder carrying its <c>type</c>
/// when that much was readable.
/// </summary>
public sealed class LenientRoutineStepConverter : JsonConverter<RoutineStep>
{
    public override RoutineStep? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var element = document.RootElement;
        var step = element.ValueKind == JsonValueKind.Object
            ? LenientJson.TryDeserialize(element, LenientJson.TypeInfo<RoutineStep>(options))
            : null;

        return step ?? new RoutineStep
        {
            Type = LenientJson.TryGetString(element, "type"),
            IsMalformed = true,
        };
    }

    public override void Write(Utf8JsonWriter writer, RoutineStep value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, value, LenientJson.TypeInfo<RoutineStep>(options));
}

internal static class LenientJson
{
    public static JsonTypeInfo<T> TypeInfo<T>(JsonSerializerOptions options)
        => (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));

    public static T? TryDeserialize<T>(JsonElement element, JsonTypeInfo<T> typeInfo)
        where T : class
    {
        try
        {
            return element.Deserialize(typeInfo);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            // Wrong JSON type, number out of range, and the like. Deliberately swallowed: the caller
            // turns this into a rejectable value, which is the whole point of this file.
            return null;
        }
    }

    public static string? TryGetString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
