using System.Text.Json.Serialization;

namespace Remex.Core.Models;

public sealed record FileRootsRequest;

public sealed record FileRootsResponse
{
    [JsonPropertyName("roots")] public required FileSharedRoot[] Roots { get; init; }
    [JsonPropertyName("errorMessage")] public string? ErrorMessage { get; init; }
}

public sealed record FileSharedRoot
{
    [JsonPropertyName("rootId")] public required string RootId { get; init; }
    [JsonPropertyName("displayName")] public required string DisplayName { get; init; }
    [JsonPropertyName("isWritable")] public bool IsWritable { get; init; }
    [JsonPropertyName("canRename")] public bool CanRename { get; init; }
    [JsonPropertyName("canMove")] public bool CanMove { get; init; }
    [JsonPropertyName("canDelete")] public bool CanDelete { get; init; }
    [JsonPropertyName("canRemoveRoot")] public bool CanRemoveRoot { get; init; }
}

public sealed record FileTransferStart
{
    [JsonPropertyName("transferId")] public required string TransferId { get; init; }
    [JsonPropertyName("direction")] public required string Direction { get; init; } // "upload" | "download"
    [JsonPropertyName("remotePath")] public required string RemotePath { get; init; }
    [JsonPropertyName("remoteRootId")] public string? RemoteRootId { get; init; }
    [JsonPropertyName("remoteRelativePath")] public string? RemoteRelativePath { get; init; }
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
    /// <summary>SHA-256 hash of the uploaded file, sent by the client in the End message
    /// so the hash can be computed incrementally during chunking rather than with a double pass.</summary>
    [JsonPropertyName("sha256")] public string? Sha256Base64 { get; init; }
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
    [JsonPropertyName("path")] public string? Path { get; init; }
    [JsonPropertyName("rootId")] public string? RootId { get; init; }
    [JsonPropertyName("relativePath")] public string? RelativePath { get; init; }
}

public sealed record FileBrowseResponse
{
    [JsonPropertyName("requestId")] public required string RequestId { get; init; }
    [JsonPropertyName("path")] public string? Path { get; init; }
    [JsonPropertyName("rootId")] public string? RootId { get; init; }
    [JsonPropertyName("relativePath")] public string? RelativePath { get; init; }
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

public sealed record FileManageRequest
{
    [JsonPropertyName("requestId")] public required string RequestId { get; init; }
    [JsonPropertyName("rootId")] public required string RootId { get; init; }
    [JsonPropertyName("relativePath")] public required string RelativePath { get; init; }
    [JsonPropertyName("operation")] public required string Operation { get; init; } // "delete" | "rename"
    [JsonPropertyName("newName")] public string? NewName { get; init; }
}

public sealed record FileManageResponse
{
    [JsonPropertyName("requestId")] public required string RequestId { get; init; }
    [JsonPropertyName("success")] public required bool Success { get; init; }
    [JsonPropertyName("errorMessage")] public string? ErrorMessage { get; init; }
}

public sealed record FileHashRequest
{
    [JsonPropertyName("requestId")] public required string RequestId { get; init; }
    [JsonPropertyName("rootId")] public required string RootId { get; init; }
    [JsonPropertyName("relativePath")] public required string RelativePath { get; init; }
}

public sealed record FileHashResponse
{
    [JsonPropertyName("requestId")] public required string RequestId { get; init; }
    [JsonPropertyName("sha256")] public string? Sha256Base64 { get; init; }
    [JsonPropertyName("errorMessage")] public string? ErrorMessage { get; init; }
}

public sealed record FileRootManageRequest
{
    [JsonPropertyName("requestId")] public required string RequestId { get; init; }
    [JsonPropertyName("operation")] public required string Operation { get; init; } // "add" | "remove"
    [JsonPropertyName("sourceRootId")] public string? SourceRootId { get; init; }
    [JsonPropertyName("sourceRelativePath")] public string? SourceRelativePath { get; init; }
    [JsonPropertyName("rootId")] public string? RootId { get; init; }
}

public sealed record FileRootManageResponse
{
    [JsonPropertyName("requestId")] public required string RequestId { get; init; }
    [JsonPropertyName("roots")] public required FileSharedRoot[] Roots { get; init; }
    [JsonPropertyName("errorMessage")] public string? ErrorMessage { get; init; }
}
