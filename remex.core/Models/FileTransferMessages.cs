using System.Text.Json.Serialization;

namespace Remex.Core.Models;

/// <summary>Client → host: list the folders this host has shared. Always the first call of a session.</summary>
public sealed record FileRootsRequest;

/// <summary>
/// Host → client: the shared folders, and — because roots are always fetched first — the v3
/// capability handshake, which is why there is no separate negotiation message.
/// </summary>
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

/// <summary>
/// One shared folder and what this client may do inside it. The permission flags are the host's
/// answer, not a request: a client must respect them rather than attempt an operation and rely on
/// the refusal.
/// </summary>
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

/// <summary>
/// v2 transfer opener, carrying the whole file's identity up front. Superseded by
/// <see cref="FileTransferOffer"/> in v3 — both flows still exist, and this is the one older clients
/// use.
/// </summary>
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

/// <summary>One base64 slice of a v2 transfer, positioned by its own offset so order is explicit.</summary>
public sealed record FileTransferChunk
{
    [JsonPropertyName("transferId")] public required string TransferId { get; init; }
    [JsonPropertyName("offset")] public required long Offset { get; init; }
    [JsonPropertyName("dataBase64")] public required string DataBase64 { get; init; }
}

/// <summary>
/// Final message of a v2 transfer. Carries the sender's hash so the receiver can verify what it
/// assembled, and reports failure as <c>success: false</c> rather than by dropping the connection.
/// </summary>
public sealed record FileTransferEnd
{
    [JsonPropertyName("transferId")] public required string TransferId { get; init; }
    [JsonPropertyName("success")] public required bool Success { get; init; }
    [JsonPropertyName("errorMessage")] public string? ErrorMessage { get; init; }
    /// <summary>SHA-256 hash of the uploaded file, sent by the client in the End message
    /// so the hash can be computed incrementally during chunking rather than with a double pass.</summary>
    [JsonPropertyName("sha256")] public string? Sha256Base64 { get; init; }
}

/// <summary>Either side aborting a transfer in progress.</summary>
public sealed record FileTransferCancel
{
    [JsonPropertyName("transferId")] public required string TransferId { get; init; }
}

/// <summary>Progress ping for the UI. Advisory only — never the signal that a transfer finished.</summary>
public sealed record FileTransferProgress
{
    [JsonPropertyName("transferId")] public required string TransferId { get; init; }
    [JsonPropertyName("bytesTransferred")] public required long BytesTransferred { get; init; }
    [JsonPropertyName("totalBytes")] public required long TotalBytes { get; init; }
}

/// <summary>
/// Client → host: list one directory. Addressed as <c>rootId</c> + <c>relativePath</c>; the bare
/// <c>path</c> is the older form kept for compatibility.
/// </summary>
public sealed record FileBrowseRequest
{
    [JsonPropertyName("requestId")] public required string RequestId { get; init; }
    [JsonPropertyName("path")] public string? Path { get; init; }
    [JsonPropertyName("rootId")] public string? RootId { get; init; }
    [JsonPropertyName("relativePath")] public string? RelativePath { get; init; }
}

/// <summary>
/// Host → client: directory contents, echoing the location so a late reply cannot be mistaken for
/// the current folder. Errors arrive as <c>errorMessage</c> with the request still correlated.
/// </summary>
public sealed record FileBrowseResponse
{
    [JsonPropertyName("requestId")] public required string RequestId { get; init; }
    [JsonPropertyName("path")] public string? Path { get; init; }
    [JsonPropertyName("rootId")] public string? RootId { get; init; }
    [JsonPropertyName("relativePath")] public string? RelativePath { get; init; }
    [JsonPropertyName("entries")] public required FileEntry[] Entries { get; init; }
    [JsonPropertyName("errorMessage")] public string? ErrorMessage { get; init; }
}

/// <summary>One file or folder in a listing. Sizes are bytes; times are Unix milliseconds UTC.</summary>
public sealed record FileEntry
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("isDirectory")] public required bool IsDirectory { get; init; }
    [JsonPropertyName("sizeBytes")] public long SizeBytes { get; init; }
    [JsonPropertyName("modifiedUnixMs")] public long ModifiedUnixMs { get; init; }
}

/// <summary>
/// Client → host: delete, rename, copy, move or mkdir — see <see cref="FileManageOperations"/>.
/// Destructive by definition, so the host re-checks permissions rather than trusting that the client
/// only offered the operations it was told about.
/// </summary>
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

    /// <summary>
    /// What to do if the destination name is taken — one of <see cref="FileConflictResolutions"/>,
    /// or null to fail with <see cref="FileTransferErrorCodes.DestinationExists"/> (RemEx-6vd8).
    /// </summary>
    /// <remarks>
    /// Additive and optional, so no <c>protocolVersion</c> bump: a host that predates this ignores
    /// the field and behaves exactly as before, which is the same thing a null means.
    /// <para>
    /// Carried ON THE RETRY rather than answered in a separate round trip, so the resolution and the
    /// operation are one message — a two-step exchange could race another client writing the same
    /// name between the question and the answer.
    /// </para>
    /// </remarks>
    [JsonPropertyName("conflictResolution")] public string? ConflictResolution { get; init; }
}

/// <summary>Host → client: whether the manage operation succeeded.</summary>
public sealed record FileManageResponse
{
    [JsonPropertyName("requestId")] public required string RequestId { get; init; }
    [JsonPropertyName("success")] public required bool Success { get; init; }
    [JsonPropertyName("errorMessage")] public string? ErrorMessage { get; init; }

    /// <summary>
    /// A machine-readable reason, one of <see cref="FileTransferErrorCodes"/>, or null (RemEx-6vd8).
    /// </summary>
    /// <remarks>
    /// ALONGSIDE <see cref="ErrorMessage"/>, NEVER INSTEAD OF IT. The prose stays the thing a person
    /// reads; the code is the thing a client branches on, because string-matching English breaks the
    /// moment the wording improves and cannot work at all once the host is localized.
    /// </remarks>
    [JsonPropertyName("errorCode")] public string? ErrorCode { get; init; }

    /// <summary>
    /// The name that actually collided, so the UI can name the file it is asking about.
    /// </summary>
    /// <remarks>
    /// The bare name rather than the path: a phone sheet asking "report.pdf already exists" has no
    /// use for the host's directory layout.
    /// </remarks>
    [JsonPropertyName("conflictingName")] public string? ConflictingName { get; init; }

    /// <summary>
    /// The name the host actually used, set only when it differs from the one that was asked for.
    /// </summary>
    /// <remarks>
    /// **THE HOST CHOOSES THE NAME AND THEN SAYS SO.** A "keep both" that succeeded silently would
    /// leave the user believing they have "report.pdf" when the file on disk is "report (2).pdf" —
    /// and the client cannot compute the answer itself, because only the host knows what else is in
    /// that directory and whether its own filesystem is case-sensitive.
    /// </remarks>
    [JsonPropertyName("resolvedName")] public string? ResolvedName { get; init; }
}

/// <summary>Client → host: hash a file, so a transfer can be verified or skipped when unchanged.</summary>
public sealed record FileHashRequest
{
    [JsonPropertyName("requestId")] public required string RequestId { get; init; }
    [JsonPropertyName("rootId")] public required string RootId { get; init; }
    [JsonPropertyName("relativePath")] public required string RelativePath { get; init; }
}

/// <summary>Host → client: base64 SHA-256, or null with an <c>errorMessage</c>.</summary>
public sealed record FileHashResponse
{
    [JsonPropertyName("requestId")] public required string RequestId { get; init; }
    [JsonPropertyName("sha256")] public string? Sha256Base64 { get; init; }
    [JsonPropertyName("errorMessage")] public string? ErrorMessage { get; init; }
}

/// <summary>
/// Client → host: add or remove a SHARED ROOT — changing what is shared at all, not the contents of
/// a folder. Removing one revokes this client's access to that whole subtree.
/// </summary>
public sealed record FileRootManageRequest
{
    [JsonPropertyName("requestId")] public required string RequestId { get; init; }
    [JsonPropertyName("operation")] public required string Operation { get; init; } // "add" | "remove"
    [JsonPropertyName("sourceRootId")] public string? SourceRootId { get; init; }
    [JsonPropertyName("sourceRelativePath")] public string? SourceRelativePath { get; init; }
    [JsonPropertyName("rootId")] public string? RootId { get; init; }
}

/// <summary>Host → client: the full root list after the change, so clients never patch it locally.</summary>
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
    /// <summary>Supported file-manager operations, e.g. ["delete","rename","copy","move","mkdir","search","manifest"].</summary>
    /// <remarks><c>"manifest"</c> means the host answers <c>file_manifest_request</c>, i.e. folder transfer works.</remarks>
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

/// <summary>
/// Host → client: the machine's drives, for full-device browsing. Only meaningful once the user has
/// granted a full-browse consent, which <c>fullBrowseGranted</c> reports.
/// </summary>
public sealed record FileVolumesResponse
{
    [JsonPropertyName("requestId")] public required string RequestId { get; init; }
    [JsonPropertyName("volumes")] public required FileVolumeInfo[] Volumes { get; init; }
    /// <summary>True when this paired client currently holds a full-browse grant.</summary>
    [JsonPropertyName("fullBrowseGranted")] public bool FullBrowseGranted { get; init; }
    [JsonPropertyName("errorMessage")] public string? ErrorMessage { get; init; }
    /// <summary>
    /// Why the host refused without anyone being asked, one of <see cref="FileConsentDenyReasons"/>,
    /// or null when a person actually decided (RemEx-l580).
    /// </summary>
    /// <remarks>
    /// SEPARATE FROM <see cref="ErrorMessage"/> BECAUSE A DENY IS NOT AN ERROR. The desktop client
    /// throws <c>FileTransferHostException</c> on any non-empty <c>errorMessage</c>, so reporting an
    /// unreachable phone through that field would turn a correctly-handled refusal into a host fault
    /// on the peer — and the prose it carries is untranslatable English besides.
    /// </remarks>
    [JsonPropertyName("denyReason")] public string? DenyReason { get; init; }
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

/// <summary>Host → client: search hits, flagged <c>truncated</c> when the host capped the results.</summary>
public sealed record FileSearchResponse
{
    [JsonPropertyName("requestId")] public required string RequestId { get; init; }
    [JsonPropertyName("entries")] public required FileSearchEntry[] Entries { get; init; }
    /// <summary>True when results were capped and more matches exist.</summary>
    [JsonPropertyName("truncated")] public bool Truncated { get; init; }
    [JsonPropertyName("errorMessage")] public string? ErrorMessage { get; init; }
}

// ── Folder transfer (RemEx-q3twg) ──
// A folder transfer is NOT a new kind of transfer. The host enumerates the subtree once, in pages,
// and the client then enqueues the ordinary per-file transfers it already performs. Everything the
// queue, resume, conflict and consent machinery knows stays exactly as it was; the only new thing
// on the wire is the listing below.

/// <summary>
/// Client → host: enumerate the whole subtree under <c>rootId</c> + <c>relativePath</c>, one page at
/// a time. The reply is a flat, pre-order list — the client never recurses.
/// </summary>
/// <remarks>
/// <para>
/// <b>PAGED, AND THE CURSOR IS OPAQUE.</b> A folder can hold more entries than one message should
/// carry, so the host answers with at most <see cref="FileTransferLimits.ManifestMaxEntriesPerPage"/>
/// entries plus a <see cref="FileManifestResponse.NextCursor"/>. The client sends that value back
/// verbatim to get the following page and stops when it comes back null. It must not parse, compare
/// or synthesize a cursor: the format is the host's, and the two hosts (PC and Android) do not have
/// to agree on it.
/// </para>
/// <para>
/// <b>THE HOST KEEPS NO PER-REQUEST STATE.</b> The cursor encodes the resume position, so pages
/// survive a reconnect and a host restart, and an abandoned enumeration costs nothing. That is the
/// reason it is a path-shaped token rather than a session id.
/// </para>
/// </remarks>
public sealed record FileManifestRequest
{
    [JsonPropertyName("requestId")] public required string RequestId { get; init; }
    [JsonPropertyName("rootId")] public required string RootId { get; init; }
    /// <summary>Subtree to enumerate, relative to the root. Null or empty means the whole root.</summary>
    [JsonPropertyName("relativePath")] public string? RelativePath { get; init; }
    /// <summary>
    /// Opaque continuation token from the previous page's <see cref="FileManifestResponse.NextCursor"/>.
    /// Null or empty requests the first page.
    /// </summary>
    [JsonPropertyName("cursor")] public string? Cursor { get; init; }
    /// <summary>
    /// Requested entries per page; the host clamps to
    /// <see cref="FileTransferLimits.ManifestMaxEntriesPerPage"/> and treats &lt;= 0 as
    /// <see cref="FileTransferLimits.ManifestDefaultEntriesPerPage"/>.
    /// </summary>
    [JsonPropertyName("maxEntries")] public int MaxEntries { get; init; }
}

/// <summary>
/// One node of an enumerated subtree. Directories are listed in their own right so an empty folder
/// still arrives and can be recreated on the receiving side.
/// </summary>
/// <remarks>
/// <b><see cref="RelativePath"/> IS ROOT-RELATIVE, NOT SUBTREE-RELATIVE</b>, matching
/// <see cref="FileSearchEntry.RelativePath"/> — so it can be handed straight back to a download,
/// hash or metadata request without the client rebuilding it. The local destination is what needs
/// the subtree-relative form, and the client derives that by stripping
/// <see cref="FileManifestResponse.RelativePath"/> from the front. Storing both on the wire was
/// considered and dropped: at a hundred thousand entries the duplicate string is the larger cost,
/// and the strip is one line in each client.
/// </remarks>
public sealed record FileManifestEntry
{
    /// <summary>Path relative to the ROOT (forward slashes), directly usable as a transfer source.</summary>
    [JsonPropertyName("relativePath")] public required string RelativePath { get; init; }
    [JsonPropertyName("isDirectory")] public bool IsDirectory { get; init; }
    /// <summary>Bytes; always 0 for a directory.</summary>
    [JsonPropertyName("sizeBytes")] public long SizeBytes { get; init; }
    [JsonPropertyName("modifiedUnixMs")] public long ModifiedUnixMs { get; init; }
}

/// <summary>
/// Host → client: one page of an enumerated subtree, echoing the location so a late reply cannot be
/// mistaken for the folder the user has since moved to. Errors arrive as <c>errorMessage</c> with
/// the request still correlated, the same as browse and search.
/// </summary>
public sealed record FileManifestResponse
{
    [JsonPropertyName("requestId")] public required string RequestId { get; init; }
    /// <summary>Echo of the requested root.</summary>
    [JsonPropertyName("rootId")] public string? RootId { get; init; }
    /// <summary>Echo of the requested subtree, so the client can strip it to build local paths.</summary>
    [JsonPropertyName("relativePath")] public string? RelativePath { get; init; }
    [JsonPropertyName("entries")] public required FileManifestEntry[] Entries { get; init; }
    /// <summary>Token for the next page, or null when this page completed the subtree.</summary>
    [JsonPropertyName("nextCursor")] public string? NextCursor { get; init; }
    /// <summary>
    /// Total files in the WHOLE subtree, present on the first page only (null on continuations,
    /// which is how a client tells "not counted here" from "counted zero"). See
    /// <see cref="TotalsComplete"/>.
    /// </summary>
    [JsonPropertyName("totalFiles")] public long? TotalFiles { get; init; }
    /// <summary>Total directories in the whole subtree; first page only. See <see cref="TotalFiles"/>.</summary>
    [JsonPropertyName("totalDirectories")] public long? TotalDirectories { get; init; }
    /// <summary>Total bytes of all files in the whole subtree; first page only. See <see cref="TotalFiles"/>.</summary>
    [JsonPropertyName("totalBytes")] public long? TotalBytes { get; init; }
    /// <summary>
    /// False when the count pass hit <see cref="FileTransferLimits.ManifestCountBudgetEntries"/> and
    /// the totals are therefore LOWER BOUNDS. A progress denominator built on them must be shown as
    /// approximate rather than as a fixed target.
    /// </summary>
    [JsonPropertyName("totalsComplete")] public bool TotalsComplete { get; init; }
    /// <summary>
    /// True when enumeration stopped at <see cref="FileTransferLimits.ManifestMaxTotalEntries"/> and
    /// the subtree is only partly described. <see cref="NextCursor"/> is null in that case — there is
    /// no more to fetch — so this is the only signal that entries are missing.
    /// </summary>
    [JsonPropertyName("truncated")] public bool Truncated { get; init; }
    [JsonPropertyName("errorMessage")] public string? ErrorMessage { get; init; }
}

/// <summary>
/// One page of an enumerated subtree as produced by
/// <see cref="Services.FileTransfer.IFileTransferService"/>. The handler wraps this into a
/// <see cref="FileManifestResponse"/> (adding the correlation id and echoing the location), the same
/// split <see cref="FileMetadata"/> uses. Server-side only, and deliberately NOT wire-serialized.
/// </summary>
public sealed record FileManifestPage
{
    public required IReadOnlyList<FileManifestEntry> Entries { get; init; }
    /// <summary>Opaque continuation token, or null when the subtree is fully described.</summary>
    public string? NextCursor { get; init; }
    /// <summary>Whole-subtree totals; null on continuation pages. See <see cref="FileManifestResponse.TotalFiles"/>.</summary>
    public long? TotalFiles { get; init; }
    public long? TotalDirectories { get; init; }
    public long? TotalBytes { get; init; }
    public bool TotalsComplete { get; init; }
    public bool Truncated { get; init; }
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
    /// <summary>Size in bytes. Zero for a directory rather than a computed subtree total.</summary>
    public required long Size { get; init; }
    /// <summary>Creation time as Unix milliseconds UTC.</summary>
    public required long CreatedUtc { get; init; }
    /// <summary>Last-modified time as Unix milliseconds UTC.</summary>
    public required long ModifiedUtc { get; init; }
    /// <summary>Whether this item is a directory.</summary>
    public required bool IsDirectory { get; init; }
    /// <summary>Immediate child count for directories; null for files.</summary>
    public int? ItemCount { get; init; }
    /// <summary>Best-effort MIME type, usually from the extension. Null when it cannot be determined.</summary>
    public string? MimeType { get; init; }
    /// <summary>
    /// The item's own read-only attribute. Independent of whether the client may WRITE it — that is
    /// governed by the shared root's permissions, so a writable root can still contain read-only files.
    /// </summary>
    public bool ReadOnly { get; init; }
}

/// <summary>Host → client: the metadata for one item, wrapped with its correlation id.</summary>
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

/// <summary>Host → client: a base64 JPEG preview, or null when one cannot be produced.</summary>
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
    /// <summary>
    /// Absolute Unix-epoch milliseconds at which the serving device auto-denies this request, so a
    /// remote prompt can show a countdown and dismiss itself instead of collecting an answer nobody
    /// is still listening for (RemEx-6mxu). Stamped by the serving device when it routes the prompt
    /// over the wire; null from a host that predates the field, in which case the renderer has no
    /// deadline to show — do not substitute a locally invented one, because the whole point is that
    /// it is the SERVING device's clock that decides, and the two are not the same clock.
    /// </summary>
    [JsonPropertyName("expiresAtUnixMs")] public long? ExpiresAtUnixMs { get; init; }
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
    /// <summary>
    /// Why the receiver refused without anyone being asked, one of <see cref="FileConsentDenyReasons"/>,
    /// or null when a person actually decided (RemEx-l580).
    /// </summary>
    [JsonPropertyName("denyReason")] public string? DenyReason { get; init; }
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
/// <remarks>
/// **A DISPLAY HINT, NOT AN AUTHORIZATION INPUT (RemEx-e11w).** <see cref="Upload"/> and
/// <see cref="Push"/> describe the same operation from two points of view — the phone sending a file
/// to a shared writable root — and the write authorization deliberately does not read this field:
/// <c>ResolveForWrite</c> decides on the resolved path alone (size cap, IsWritable, path-escape).
/// <para>
/// **DO NOT AUTHORIZE ON THIS VALUE, AND NEVER SPLIT <see cref="Upload"/> FROM <see cref="Push"/>.**
/// It is client-chosen text off the wire, so a permission gate here refuses every real share-sheet
/// send while an attacker changes one word and walks past it. That was tried in RemEx-9xs1 and
/// reverted; RemEx-e11w then settled it by deleting the separate push consent path rather than
/// repairing it.
/// </para>
/// <para>
/// **<see cref="Download"/> IS DIFFERENT, and reading it is correct.** It names the DIRECTION the
/// bytes travel, not a permission level, and two live call sites route on it —
/// <c>TransferSessionManager.HandleOfferAsync</c> (send vs receive) and
/// <c>TransferQueueService.DirectionOf</c> (Outbound vs Inbound). Neither is a gate and neither is
/// the thing the paragraph above forbids; removing them would make the PC try to receive a file the
/// phone asked to download.
/// </para>
/// </remarks>
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

/// <summary>
/// Machine-readable reasons a consent-gated request was refused WITHOUT the user being asked
/// (RemEx-l580). Carried on <see cref="FileVolumesResponse.DenyReason"/> and
/// <see cref="FilePushResponse.DenyReason"/>.
/// </summary>
/// <remarks>
/// <para>
/// **A REFUSAL THE USER NEVER SAW USED TO BE INDISTINGUISHABLE FROM ONE THEY MADE.** Both came back
/// as <c>fullBrowseGranted=false</c> / <c>accepted=false</c> and nothing else, so the phone that
/// asked seconds ago could only show a flat no — for a state the person could actually have fixed.
/// A code is what lets the client tell the two apart; prose cannot be branched on and cannot be
/// localized on the phone.
/// </para>
/// <para>
/// **NULL IS THE ANSWER FOR EVERY DENY SOMEBODY MADE**, including one that was delivered and timed
/// out. That is what keeps a code meaningful: if every refusal carried one, a user who just tapped
/// Deny would be told their PC could not reach them.
/// </para>
/// <para>
/// ADDITIVE AND OPTIONAL, so no <c>protocolVersion</c> bump — same shape as
/// <see cref="FileTransferErrorCodes"/>. An older client ignores the field and shows what it shows
/// today; a newer client against an older host sees null and does the same.
/// </para>
/// </remarks>
public static class FileConsentDenyReasons
{
    /// <summary>
    /// The host could not put the question to the device that asked it.
    /// </summary>
    /// <remarks>
    /// ONE CODE FOR BOTH UNREACHABLE PATHS — the asking client had no live session when the prompt
    /// was routed, and the prompt failed to send after it was. They are separate log lines on the
    /// host because the distinction is diagnostic, and one code on the wire because the client's
    /// answer to both is the same: the phone is not reachable, reconnect and ask again. Splitting
    /// them would offer a client two branches it would have to write identically.
    /// </remarks>
    public const string ClientUnreachable = "client_unreachable";
}

/// <summary>Numeric protocol limits shared by the PC host and Android mirror.</summary>
public static class FileTransferLimits
{
    /// <summary>Maximum search hits returned regardless of the client's requested cap (plan §1.2).</summary>
    public const int SearchMaxResults = 200;
    /// <summary>Maximum manifest entries the host will put in a single page (RemEx-q3twg).</summary>
    public const int ManifestMaxEntriesPerPage = 1000;
    /// <summary>Page size used when the client asks for none.</summary>
    public const int ManifestDefaultEntriesPerPage = 500;
    /// <summary>
    /// Hard ceiling on entries an enumeration will describe across ALL its pages. Past this the host
    /// stops and sets <c>truncated</c> with a null cursor: a folder that large is a mistake to fan out
    /// into individual transfers, and the client must say so rather than start.
    /// </summary>
    public const int ManifestMaxTotalEntries = 200_000;
    /// <summary>
    /// Entries the first-page count pass will visit before giving up and reporting the totals as lower
    /// bounds (<c>totalsComplete: false</c>). Counting is metadata-only, so this is far cheaper than the
    /// transfers it precedes - but it is still a full walk, and it must not be unbounded.
    /// </summary>
    public const int ManifestCountBudgetEntries = 200_000;
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
