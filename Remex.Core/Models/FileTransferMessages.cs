using System.Text.Json.Serialization;

namespace Remex.Core.Models;

public sealed record FileTransferStart
{
    [JsonPropertyName("transferId")] public required string TransferId { get; init; }
    [JsonPropertyName("direction")] public required string Direction { get; init; } // "upload" | "download"
    [JsonPropertyName("remotePath")] public required string RemotePath { get; init; }
    [JsonPropertyName("fileName")] public required string FileName { get; init; }
    [JsonPropertyName("totalBytes")] public required long TotalBytes { get; init; }
    [JsonPropertyName("sha256")] public required string Sha256Base64 { get; init; }
}

public sealed record FileTransferChunk
{
    [JsonPropertyName("transferId")] public required string TransferId { get; init; }
    [JsonPropertyName("offset")] public required long Offset { get; init; }
    [JsonPropertyName("dataBase64")] public required string DataBase64 { get; init; }
}

public sealed record FileTransferEnd
{
    [JsonPropertyName("transferId")] public required string TransferId { get; init; }
    [JsonPropertyName("success")] public required bool Success { get; init; }
    [JsonPropertyName("errorMessage")] public string? ErrorMessage { get; init; }
}

public sealed record FileTransferCancel
{
    [JsonPropertyName("transferId")] public required string TransferId { get; init; }
}

public sealed record FileTransferProgress
{
    [JsonPropertyName("transferId")] public required string TransferId { get; init; }
    [JsonPropertyName("bytesTransferred")] public required long BytesTransferred { get; init; }
    [JsonPropertyName("totalBytes")] public required long TotalBytes { get; init; }
}

public sealed record FileBrowseRequest
{
    [JsonPropertyName("requestId")] public required string RequestId { get; init; }
    [JsonPropertyName("path")] public required string Path { get; init; }
}

public sealed record FileBrowseResponse
{
    [JsonPropertyName("requestId")] public required string RequestId { get; init; }
    [JsonPropertyName("path")] public required string Path { get; init; }
    [JsonPropertyName("entries")] public required FileEntry[] Entries { get; init; }
    [JsonPropertyName("errorMessage")] public string? ErrorMessage { get; init; }
}

public sealed record FileEntry
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("isDirectory")] public required bool IsDirectory { get; init; }
    [JsonPropertyName("sizeBytes")] public long SizeBytes { get; init; }
    [JsonPropertyName("modifiedUnixMs")] public long ModifiedUnixMs { get; init; }
}
