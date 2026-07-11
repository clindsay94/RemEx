using System.Text.Json.Serialization;

namespace Remex.Core.Models;

public sealed record FileRootsRequest;

public sealed record FileRootsResponse
{
    [JsonPropertyName("roots")] public required FileSharedRoot[] Roots { get; init; }
    [JsonPropertyName("errorMessage")] public string? ErrorMessage { get; init; }
    /// <summary>
    /// v3 file-transfer capabilities advertised by the host. Null for v2 responses / v2 peers,
    /// which safely ignore this additive field. Roots are always fetched first, so this doubles
    /// as the capability handshake — no separate negotiation message is needed.
    /// </summary>
    [JsonPropertyName("fileCapabilities")] public FileCapabilities? FileCapabilities { get; init; }
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
    [JsonPropertyName("operation")] public required string Operation { get; init; } // "delete" | "rename" | "copy" | "move" | "mkdir"
    [JsonPropertyName("newName")] public string? NewName { get; init; }
    /// <summary>Destination relative path (within the same root) for "copy"/"move" operations (v3).</summary>
    [JsonPropertyName("destinationPath")] public string? DestinationPath { get; init; }
    /// <summary>When true, "copy"/"move" overwrite an existing destination file (v3). Older peers omit this and it defaults to false.</summary>
    [JsonPropertyName("overwrite")] public bool Overwrite { get; init; }
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

// ──────────────────────────────────────────────────────────────────────────────
// 2.1 File Sharing Overhaul — protocolVersion 3 contracts (see docs plan §1.2).
// Every record below is additive and nullable on RemexMessage; v2 peers ignore
// them and keep the legacy base64 file path. Every new type MUST have a matching
// [JsonSerializable] entry in RemexJsonSerializerContext or the NativeAOT Android
// link breaks.
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>
/// v3 file-transfer capabilities advertised by the host inside <see cref="FileRootsResponse"/>.
/// </summary>
public sealed record FileCapabilities
{
    /// <summary>The host's file-transfer protocol version (3 for the binary channel).</summary>
    [JsonPropertyName("protocol")] public required int Protocol { get; init; }
    /// <summary>True when the dedicated binary <c>/ws/files</c> channel is available.</summary>
    [JsonPropertyName("binary")] public bool Binary { get; init; }
    /// <summary>True when offset-based resume is supported.</summary>
    [JsonPropertyName("resume")] public bool Resume { get; init; }
    /// <summary>Supported file-manager operations, e.g. ["delete","rename","copy","move","mkdir","search"].</summary>
    [JsonPropertyName("ops")] public required string[] Ops { get; init; }
    /// <summary>True when the host can serve full-device (volume) browsing once consent is granted.</summary>
    [JsonPropertyName("fullBrowse")] public bool FullBrowse { get; init; }
    /// <summary>True when the host accepts pushed (client-initiated) incoming files.</summary>
    [JsonPropertyName("push")] public bool Push { get; init; }
}

// ── v3 transfer negotiation (replaces base64 start/chunk/end for v3 peers) ──

/// <summary>Sender → receiver: proposes a transfer. Receiver replies with <see cref="FileTransferReady"/>.</summary>
public sealed record FileTransferOffer
{
    [JsonPropertyName("transferId")] public required string TransferId { get; init; }
    [JsonPropertyName("mode")] public required string Mode { get; init; } // "download" | "upload" | "push"
    [JsonPropertyName("sourcePath")] public string? SourcePath { get; init; }
    [JsonPropertyName("destRoot")] public string? DestRoot { get; init; }
    [JsonPropertyName("destRelativePath")] public string? DestRelativePath { get; init; }
    [JsonPropertyName("fileName")] public required string FileName { get; init; }
    [JsonPropertyName("size")] public required long Size { get; init; }
    /// <summary>When true, the receiver should attempt offset-based resume from a matching partial.</summary>
    [JsonPropertyName("resumeRequested")] public bool ResumeRequested { get; init; }
}

/// <summary>Receiver → sender: accept/decline decision plus the byte offset to (re)start from.</summary>
public sealed record FileTransferReady
{
    [JsonPropertyName("transferId")] public required string TransferId { get; init; }
    [JsonPropertyName("accepted")] public required bool Accepted { get; init; }
    /// <summary>Byte offset the sender must stream from. 0 for a fresh transfer; &gt;0 on a resume match.</summary>
    [JsonPropertyName("startOffset")] public long StartOffset { get; init; }
    [JsonPropertyName("declineReason")] public string? DeclineReason { get; init; }
}

/// <summary>Sender → receiver: all bytes sent; carries the full-file SHA-256 for final verification.</summary>
public sealed record FileTransferComplete
{
    [JsonPropertyName("transferId")] public required string TransferId { get; init; }
    [JsonPropertyName("sha256")] public required string Sha256Base64 { get; init; }
}

/// <summary>Receiver → sender: final verification outcome (matches the mismatch-deletes-file semantics).</summary>
public sealed record FileTransferResult
{
    [JsonPropertyName("transferId")] public required string TransferId { get; init; }
    [JsonPropertyName("verified")] public required bool Verified { get; init; }
    [JsonPropertyName("sha256")] public string? Sha256Base64 { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
}

/// <summary>Either side: pause/resume/cancel control for an in-flight transfer.</summary>
public sealed record FileTransferControl
{
    [JsonPropertyName("transferId")] public required string TransferId { get; init; }
    [JsonPropertyName("action")] public required string Action { get; init; } // "pause" | "resume" | "cancel"
}

// ── Browse / metadata ──

/// <summary>Client → host: enumerate mounted volumes/drives (full-device browse). Consent-gated.</summary>
public sealed record FileVolumesRequest
{
    [JsonPropertyName("requestId")] public required string RequestId { get; init; }
}

/// <summary>A single mounted volume/drive exposed once full-browse consent is granted.</summary>
public sealed record FileVolumeInfo
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("label")] public required string Label { get; init; }
    [JsonPropertyName("path")] public required string Path { get; init; }
    [JsonPropertyName("totalBytes")] public long TotalBytes { get; init; }
    [JsonPropertyName("freeBytes")] public long FreeBytes { get; init; }
    /// <summary>Volume kind, e.g. "fixed" | "removable" | "network" | "root".</summary>
    [JsonPropertyName("kind")] public required string Kind { get; init; }
}

public sealed record FileVolumesResponse
{
    [JsonPropertyName("requestId")] public required string RequestId { get; init; }
    [JsonPropertyName("volumes")] public required FileVolumeInfo[] Volumes { get; init; }
    /// <summary>True when this paired client currently holds a full-browse grant.</summary>
    [JsonPropertyName("fullBrowseGranted")] public bool FullBrowseGranted { get; init; }
    [JsonPropertyName("errorMessage")] public string? ErrorMessage { get; init; }
}

/// <summary>Client → host: bounded recursive search under a root subtree.</summary>
public sealed record FileSearchRequest
{
    [JsonPropertyName("requestId")] public required string RequestId { get; init; }
    [JsonPropertyName("rootId")] public required string RootId { get; init; }
    [JsonPropertyName("relativePath")] public string? RelativePath { get; init; }
    [JsonPropertyName("query")] public required string Query { get; init; }
    /// <summary>Requested result cap; the host clamps to <see cref="FileTransferLimits.SearchMaxResults"/>.</summary>
    [JsonPropertyName("maxResults")] public int MaxResults { get; init; }
}

/// <summary>A single search hit, carrying its path relative to the searched root.</summary>
public sealed record FileSearchEntry
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("relativePath")] public required string RelativePath { get; init; }
    [JsonPropertyName("isDirectory")] public bool IsDirectory { get; init; }
    [JsonPropertyName("sizeBytes")] public long SizeBytes { get; init; }
    [JsonPropertyName("modifiedUnixMs")] public long ModifiedUnixMs { get; init; }
}

public sealed record FileSearchResponse
{
    [JsonPropertyName("requestId")] public required string RequestId { get; init; }
    [JsonPropertyName("entries")] public required FileSearchEntry[] Entries { get; init; }
    /// <summary>True when results were capped and more matches exist.</summary>
    [JsonPropertyName("truncated")] public bool Truncated { get; init; }
    [JsonPropertyName("errorMessage")] public string? ErrorMessage { get; init; }
}

/// <summary>Client → host: request detailed metadata for a single file/directory.</summary>
public sealed record FileMetadataRequest
{
    [JsonPropertyName("requestId")] public required string RequestId { get; init; }
    [JsonPropertyName("rootId")] public required string RootId { get; init; }
    [JsonPropertyName("relativePath")] public required string RelativePath { get; init; }
}

/// <summary>
/// Domain metadata for a single item, returned by <see cref="Services.FileTransfer.IFileTransferService"/>.
/// The handler wraps this into a <see cref="FileMetadataResponse"/> (adding the correlation id). This is
/// server-side only and is intentionally NOT wire-serialized on its own.
/// </summary>
public sealed record FileMetadata
{
    public required long Size { get; init; }
    /// <summary>Creation time as Unix milliseconds UTC.</summary>
    public required long CreatedUtc { get; init; }
    /// <summary>Last-modified time as Unix milliseconds UTC.</summary>
    public required long ModifiedUtc { get; init; }
    public required bool IsDirectory { get; init; }
    /// <summary>Immediate child count for directories; null for files.</summary>
    public int? ItemCount { get; init; }
    public string? MimeType { get; init; }
    public bool ReadOnly { get; init; }
}

public sealed record FileMetadataResponse
{
    [JsonPropertyName("requestId")] public required string RequestId { get; init; }
    [JsonPropertyName("size")] public long Size { get; init; }
    /// <summary>Creation time as Unix milliseconds UTC.</summary>
    [JsonPropertyName("createdUtc")] public long CreatedUtc { get; init; }
    /// <summary>Last-modified time as Unix milliseconds UTC.</summary>
    [JsonPropertyName("modifiedUtc")] public long ModifiedUtc { get; init; }
    [JsonPropertyName("isDirectory")] public bool IsDirectory { get; init; }
    /// <summary>Immediate child count for directories; null for files.</summary>
    [JsonPropertyName("itemCount")] public int? ItemCount { get; init; }
    [JsonPropertyName("mimeType")] public string? MimeType { get; init; }
    [JsonPropertyName("readOnly")] public bool ReadOnly { get; init; }
    [JsonPropertyName("errorMessage")] public string? ErrorMessage { get; init; }
}

/// <summary>Client → host: request a small JPEG thumbnail (images/videos only, capability-flagged).</summary>
public sealed record FileThumbnailRequest
{
    [JsonPropertyName("requestId")] public required string RequestId { get; init; }
    [JsonPropertyName("rootId")] public required string RootId { get; init; }
    [JsonPropertyName("relativePath")] public required string RelativePath { get; init; }
    /// <summary>Requested maximum edge length in pixels (default <see cref="FileTransferLimits.ThumbnailDefaultMaxDim"/>).</summary>
    [JsonPropertyName("maxDim")] public int MaxDim { get; init; }
}

public sealed record FileThumbnailResponse
{
    [JsonPropertyName("requestId")] public required string RequestId { get; init; }
    /// <summary>Base64 JPEG, ≤ <see cref="FileTransferLimits.ThumbnailMaxBytes"/> encoded. Null when unavailable.</summary>
    [JsonPropertyName("jpegBase64")] public string? JpegBase64 { get; init; }
    [JsonPropertyName("errorMessage")] public string? ErrorMessage { get; init; }
}

// ── Consent / push ──

/// <summary>Serving device → requester: a consent prompt for sensitive access.</summary>
public sealed record FileConsentRequest
{
    [JsonPropertyName("consentId")] public required string ConsentId { get; init; }
    [JsonPropertyName("kind")] public required string Kind { get; init; } // "full_browse" | "incoming_push"
    /// <summary>Human-readable detail (file names / total size) for the prompt.</summary>
    [JsonPropertyName("detail")] public string? Detail { get; init; }
}

/// <summary>Requester → serving device: the user's consent decision.</summary>
public sealed record FileConsentResponse
{
    [JsonPropertyName("consentId")] public required string ConsentId { get; init; }
    [JsonPropertyName("granted")] public required bool Granted { get; init; }
    /// <summary>When true, persist the grant so future requests auto-accept.</summary>
    [JsonPropertyName("remember")] public bool Remember { get; init; }
}

/// <summary>A single file described in a <see cref="FilePushOffer"/>.</summary>
public sealed record FilePushFile
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("size")] public long Size { get; init; }
}

/// <summary>Sender → receiver: offer to push one or more files (share-sheet / "send to").</summary>
public sealed record FilePushOffer
{
    [JsonPropertyName("pushId")] public required string PushId { get; init; }
    [JsonPropertyName("files")] public required FilePushFile[] Files { get; init; }
}

/// <summary>Receiver → sender: push acceptance plus the receiver-assigned transfer ids (index-aligned to files).</summary>
public sealed record FilePushResponse
{
    [JsonPropertyName("pushId")] public required string PushId { get; init; }
    [JsonPropertyName("accepted")] public required bool Accepted { get; init; }
    [JsonPropertyName("transferIds")] public string[]? TransferIds { get; init; }
}

// ── Shared vocabularies and limits (single source of truth for both sides of the wire) ──

/// <summary>
/// Local, persisted lifecycle of a queued transfer (see plan §1.4). Persisted to the transfer
/// queue JSON on each device and surfaced in the queue UI.
/// </summary>
public enum TransferState
{
    Queued,
    Negotiating,
    Active,
    Paused,
    Verifying,
    Done,
    Failed,
    Cancelled,
}

/// <summary><see cref="FileTransferOffer.Mode"/> string values.</summary>
public static class FileTransferModes
{
    public const string Download = "download";
    public const string Upload = "upload";
    public const string Push = "push";
}

/// <summary><see cref="FileTransferControl.Action"/> string values.</summary>
public static class FileTransferControlActions
{
    public const string Pause = "pause";
    public const string Resume = "resume";
    public const string Cancel = "cancel";
}

/// <summary><see cref="FileManageRequest.Operation"/> string values.</summary>
public static class FileManageOperations
{
    public const string Delete = "delete";
    public const string Rename = "rename";
    public const string Copy = "copy";
    public const string Move = "move";
    public const string Mkdir = "mkdir";
}

/// <summary><see cref="FileConsentRequest.Kind"/> string values.</summary>
public static class FileConsentKinds
{
    public const string FullBrowse = "full_browse";
    public const string IncomingPush = "incoming_push";
}

/// <summary>Numeric protocol limits shared by the PC host and Android mirror.</summary>
public static class FileTransferLimits
{
    /// <summary>Maximum search hits returned regardless of the client's requested cap (plan §1.2).</summary>
    public const int SearchMaxResults = 200;
    /// <summary>Default thumbnail maximum edge length in pixels.</summary>
    public const int ThumbnailDefaultMaxDim = 128;
    /// <summary>Maximum encoded thumbnail size in bytes (≈96 KB).</summary>
    public const int ThumbnailMaxBytes = 96 * 1024;
    /// <summary>Raw payload bytes per binary data frame (plan §1.1).</summary>
    public const int DataPayloadBytes = 256 * 1024;
    /// <summary>The receiver acks committedOffset at least every this many bytes and on final.</summary>
    public const int AckIntervalBytes = 4 * 1024 * 1024;
    /// <summary>The sender caps outstanding unacked bytes at this many (backpressure).</summary>
    public const int MaxUnackedBytes = 8 * 1024 * 1024;
}
