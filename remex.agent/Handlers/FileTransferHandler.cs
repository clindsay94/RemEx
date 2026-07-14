using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Services.FileTransfer;
using Remex.Core.Validation;
using Remex.Agent.Services.FileTransfer;

namespace Remex.Agent.Handlers;

public sealed class FileTransferHandler(
    ILogger<FileTransferHandler> logger,
    IFileTransferService fileTransferService,
    IFileTrustService fileTrustService,
    VolumeEnumerator volumeEnumerator)
{
    private const int ProgressChunkInterval = 10;

    // Defensive running cap. FileTransferService applies the same limit to
    // start.TotalBytes, but mobile clients send 0 for unknown content URIs, so
    // a malicious / buggy peer could otherwise stream unbounded bytes past the
    // initial check. Kept identical to FileTransferService.MaxUploadBytes.
    private const long MaxUploadBytes = 5_000_000_000L;

    private sealed class FileTransferState
    {
        public required string TransferId { get; init; }
        public required string Direction { get; init; }
        public required string RemotePath { get; init; }
        public required string ExpectedSha256 { get; init; }
        public required long TotalBytes { get; init; }
        public required Stream FileStream { get; init; }
        public required SHA256 Hasher { get; init; }
        public long BytesTransferred { get; set; }
        public int ChunkCount { get; set; }
    }

    private readonly ConcurrentDictionary<string, FileTransferState> _activeTransfers = new();

    /// <summary>
    /// The host's static v3 file capabilities, advertised in every <see cref="MessageTypes.FileRootsResponse"/>.
    /// Roots are always fetched first, so this doubles as the capability handshake (plan §1.2): v3 clients read it
    /// to negotiate the binary <c>/ws/files</c> channel, offset resume, and full-device browse, while v2 peers
    /// simply ignore the unknown <c>fileCapabilities</c> field. Static because it never varies per connection —
    /// per-device grants (full browse) are gated separately by <c>FileTrustService</c> at request time.
    /// </summary>
    private static readonly FileCapabilities HostFileCapabilities = new()
    {
        Protocol = ProtocolVersionPolicy.Current,
        Binary = true,
        Resume = true,
        Ops =
        [
            FileManageOperations.Delete, FileManageOperations.Rename, FileManageOperations.Copy,
            FileManageOperations.Move, FileManageOperations.Mkdir, "search",
        ],
        FullBrowse = true,
        Push = true,
    };

    public async Task HandleFileRootsRequestAsync(WebSocket ws, CancellationToken ct)
    {
        try
        {
            var roots = await fileTransferService.ListRootsAsync(ct);
            await MessageSerializer.SendAsync(ws, new RemexMessage
            {
                Type = MessageTypes.FileRootsResponse,
                FileRootsResponse = new FileRootsResponse
                {
                    Roots = [.. roots],
                    FileCapabilities = HostFileCapabilities
                }
            }, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Listing file roots failed.");
            await MessageSerializer.SendAsync(ws, new RemexMessage
            {
                Type = MessageTypes.FileRootsResponse,
                FileRootsResponse = new FileRootsResponse
                {
                    Roots = [],
                    ErrorMessage = ex.Message,
                    FileCapabilities = HostFileCapabilities
                }
            }, ct);
        }
    }

    public async Task HandleFileBrowseRequestAsync(RemexMessage message, WebSocket ws, string? clientId, CancellationToken ct)
    {
        var req = message.FileBrowseRequest;
        if (req is null) return;

        RemexMessage response;
        try
        {
            if (string.IsNullOrWhiteSpace(req.RootId))
                throw new UnauthorizedAccessException("A shared root is required for remote browsing.");

            IReadOnlyList<FileEntry> entries;
            var isConfiguredRoot = (await fileTransferService.ListRootsAsync(ct)).Any(r => r.RootId == req.RootId);
            if (isConfiguredRoot)
            {
                entries = await fileTransferService.BrowseAsync(req.RootId, req.RelativePath ?? string.Empty, ct);
            }
            else
            {
                // Not a configured shared root: treat as a full-device VOLUME browse. Gated on this paired
                // client holding a full-browse consent grant (re-verified here, never trusting RootId), and
                // on RootId being a genuine mounted volume. BrowseVolumeAsync bounds navigation within it.
                if (string.IsNullOrWhiteSpace(clientId) || !await fileTrustService.IsFullBrowseGrantedAsync(clientId, ct))
                    throw new UnauthorizedAccessException("Full-device browsing has not been granted for this device.");
                var volume = volumeEnumerator.Enumerate().FirstOrDefault(v => v.Id == req.RootId)
                    ?? throw new UnauthorizedAccessException($"Unknown shared root '{req.RootId}'.");
                entries = await fileTransferService.BrowseVolumeAsync(volume.Path, req.RelativePath ?? string.Empty, ct);
            }
            response = new RemexMessage
            {
                Type = MessageTypes.FileBrowseResponse,
                FileBrowseResponse = new FileBrowseResponse
                {
                    RequestId = req.RequestId,
                    Path = req.Path,
                    RootId = req.RootId,
                    RelativePath = req.RelativePath ?? string.Empty,
                    Entries = [.. entries]
                }
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Browse failed for root {RootId}, path {Path}", req.RootId, req.RelativePath ?? req.Path);
            response = new RemexMessage
            {
                Type = MessageTypes.FileBrowseResponse,
                FileBrowseResponse = new FileBrowseResponse
                {
                    RequestId = req.RequestId,
                    Path = req.Path,
                    RootId = req.RootId,
                    RelativePath = req.RelativePath ?? string.Empty,
                    Entries = [],
                    ErrorMessage = ex.Message
                }
            };
        }

        await MessageSerializer.SendAsync(ws, response, ct);
    }

    public async Task HandleFileTransferStartAsync(RemexMessage message, WebSocket ws, CancellationToken ct)
    {
        var start = message.FileTransferStart;
        if (start is null) return;

        try
        {
            if (string.IsNullOrWhiteSpace(start.RemoteRootId))
                throw new UnauthorizedAccessException("A shared root is required for file transfer operations.");

            var remoteRelativePath = start.RemoteRelativePath ?? string.Empty;
            Stream stream = start.Direction == "upload"
                ? await fileTransferService.OpenForWriteAsync(start.RemoteRootId, remoteRelativePath, start.TotalBytes, ct)
                : await fileTransferService.OpenForReadAsync(start.RemoteRootId, remoteRelativePath, ct);

            var totalBytes = start.Direction == "download" && stream.CanSeek
                ? stream.Length
                : start.TotalBytes;

            var state = new FileTransferState
            {
                TransferId = start.TransferId,
                Direction = start.Direction,
                RemotePath = stream is FileStream fileStream ? fileStream.Name : start.RemotePath,
                ExpectedSha256 = start.Sha256Base64,
                TotalBytes = totalBytes,
                FileStream = stream,
                Hasher = SHA256.Create()
            };
            _activeTransfers[start.TransferId] = state;

            if (start.Direction == "download")
            {
                // Wrap in a local async lambda so exceptions are caught and surfaced to the
                // client rather than silently swallowed by a fire-and-forget Task.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await StreamDownloadAsync(state, ws, ct);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Unhandled exception in StreamDownloadAsync for {TransferId}", start.TransferId);
                        try
                        {
                            await MessageSerializer.SendAsync(ws, new RemexMessage
                            {
                                Type = MessageTypes.FileTransferEnd,
                                FileTransferEnd = new FileTransferEnd
                                {
                                    TransferId = start.TransferId,
                                    Success = false,
                                    ErrorMessage = $"Download failed: {ex.Message}"
                                }
                            }, CancellationToken.None);
                        }
                        catch (Exception sendEx)
                        {
                            logger.LogWarning(sendEx, "Could not send error response for failed download {TransferId}", start.TransferId);
                        }
                    }
                }, ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "FileTransferStart failed for {TransferId}", start.TransferId);
            await MessageSerializer.SendAsync(ws, new RemexMessage
            {
                Type = MessageTypes.FileTransferEnd,
                FileTransferEnd = new FileTransferEnd
                {
                    TransferId = start.TransferId,
                    Success = false,
                    ErrorMessage = ex.Message
                }
            }, ct);
        }
    }

    public async Task HandleFileTransferChunkAsync(RemexMessage message, WebSocket ws, CancellationToken ct)
    {
        var chunk = message.FileTransferChunk;
        if (chunk is null || !_activeTransfers.TryGetValue(chunk.TransferId, out var state)) return;

        try
        {
            var data = Convert.FromBase64String(chunk.DataBase64);

            if (state.Direction == "upload" && state.BytesTransferred + data.Length > MaxUploadBytes)
            {
                logger.LogWarning(
                    "Upload {TransferId} exceeded {Cap}-byte cap (transferred={Transferred}, incoming={Incoming}); aborting.",
                    chunk.TransferId, MaxUploadBytes, state.BytesTransferred, data.Length);
                await CleanupTransferAsync(chunk.TransferId, deleteFile: true);
                await MessageSerializer.SendAsync(ws, new RemexMessage
                {
                    Type = MessageTypes.FileTransferEnd,
                    FileTransferEnd = new FileTransferEnd
                    {
                        TransferId = chunk.TransferId,
                        Success = false,
                        ErrorMessage = $"Upload exceeded the {MaxUploadBytes / 1_000_000_000.0:0.#} GB cap.",
                    }
                }, ct);
                return;
            }

            await state.FileStream.WriteAsync(data, ct);
            state.Hasher.TransformBlock(data, 0, data.Length, null, 0);
            state.BytesTransferred += data.Length;
            state.ChunkCount++;

            if (state.ChunkCount % ProgressChunkInterval == 0)
            {
                await MessageSerializer.SendAsync(ws, new RemexMessage
                {
                    Type = MessageTypes.FileTransferProgress,
                    FileTransferProgress = new FileTransferProgress
                    {
                        TransferId = chunk.TransferId,
                        BytesTransferred = state.BytesTransferred,
                        TotalBytes = state.TotalBytes
                    }
                }, ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Chunk write failed for {TransferId}", chunk.TransferId);
            await CleanupTransferAsync(chunk.TransferId, deleteFile: true);
        }
    }

    public async Task HandleFileTransferEndAsync(RemexMessage message, WebSocket ws, CancellationToken ct)
    {
        var end = message.FileTransferEnd;
        if (end is null || !_activeTransfers.TryGetValue(end.TransferId, out var state)) return;

        try
        {
            state.Hasher.TransformFinalBlock([], 0, 0);
            var actualHash = Convert.ToBase64String(state.Hasher.Hash!);
            // Accept hash from End message (incremental path) or from Start (legacy path)
            var expectedHash = !string.IsNullOrEmpty(end.Sha256Base64) ? end.Sha256Base64 : state.ExpectedSha256;
            var success = string.IsNullOrEmpty(expectedHash) || actualHash == expectedHash;

            await state.FileStream.FlushAsync(ct);
            await CleanupTransferAsync(end.TransferId, deleteFile: !success);

            await MessageSerializer.SendAsync(ws, new RemexMessage
            {
                Type = MessageTypes.FileTransferEnd,
                FileTransferEnd = new FileTransferEnd
                {
                    TransferId = end.TransferId,
                    Success = success,
                    ErrorMessage = success ? null : "SHA-256 mismatch — file corrupted in transit."
                }
            }, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "FileTransferEnd failed for {TransferId}", end.TransferId);
            await CleanupTransferAsync(end.TransferId, deleteFile: true);
        }
    }

    public async Task HandleFileTransferCancelAsync(RemexMessage message)
    {
        var cancel = message.FileTransferCancel;
        if (cancel is null) return;

        await CleanupTransferAsync(cancel.TransferId, deleteFile: true);
    }

    public async Task HandleFileManageRequestAsync(RemexMessage message, WebSocket ws, CancellationToken ct)
    {
        var req = message.FileManageRequest;
        if (req is null) return;

        RemexMessage response;
        try
        {
            switch (req.Operation)
            {
                case FileManageOperations.Delete:
                    await fileTransferService.DeleteAsync(req.RootId, req.RelativePath, ct);
                    break;
                case FileManageOperations.Rename:
                    if (string.IsNullOrWhiteSpace(req.NewName))
                        throw new ArgumentException("NewName is required for rename.");
                    await fileTransferService.RenameAsync(req.RootId, req.RelativePath, req.NewName, ct);
                    break;
                // ── 2.1 File Sharing Overhaul (protocolVersion 3) — WP3 ──
                case FileManageOperations.Copy:
                    if (string.IsNullOrWhiteSpace(req.DestinationPath))
                        throw new ArgumentException("DestinationPath is required for copy.");
                    await fileTransferService.CopyAsync(req.RootId, req.RelativePath, req.DestinationPath, req.Overwrite, ct);
                    break;
                case FileManageOperations.Move:
                    if (string.IsNullOrWhiteSpace(req.DestinationPath))
                        throw new ArgumentException("DestinationPath is required for move.");
                    await fileTransferService.MoveAsync(req.RootId, req.RelativePath, req.DestinationPath, req.Overwrite, ct);
                    break;
                case FileManageOperations.Mkdir:
                    // Wire contract: RelativePath is the parent directory (empty = root) and NewName is the
                    // new folder's name — parallels "rename" so the Android/PC mirrors stay mechanical. The
                    // name is validated here (rejects separators / "." / "..") before being combined.
                    if (!FilePathValidation.IsValidFileName(req.NewName, out var nameError))
                        throw new ArgumentException(nameError ?? "Invalid folder name.");
                    var newDirRelativePath = CombineRelative(req.RelativePath, req.NewName!);
                    await fileTransferService.CreateDirectoryAsync(req.RootId, newDirRelativePath, ct);
                    break;
                default:
                    throw new ArgumentException($"Unknown operation '{req.Operation}'.");
            }

            response = new RemexMessage
            {
                Type = MessageTypes.FileManageResponse,
                FileManageResponse = new FileManageResponse { RequestId = req.RequestId, Success = true }
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "FileManage {Operation} failed for {Path}", req.Operation, req.RelativePath);
            response = new RemexMessage
            {
                Type = MessageTypes.FileManageResponse,
                FileManageResponse = new FileManageResponse { RequestId = req.RequestId, Success = false, ErrorMessage = ex.Message }
            };
        }

        await MessageSerializer.SendAsync(ws, response, ct);
    }

    public async Task HandleFileHashRequestAsync(RemexMessage message, WebSocket ws, CancellationToken ct)
    {
        var req = message.FileHashRequest;
        if (req is null) return;

        RemexMessage response;
        try
        {
            var hash = await fileTransferService.ComputeSha256Async(req.RootId, req.RelativePath, ct);
            response = new RemexMessage
            {
                Type = MessageTypes.FileHashResponse,
                FileHashResponse = new FileHashResponse { RequestId = req.RequestId, Sha256Base64 = hash }
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "FileHash failed for {Path}", req.RelativePath);
            response = new RemexMessage
            {
                Type = MessageTypes.FileHashResponse,
                FileHashResponse = new FileHashResponse { RequestId = req.RequestId, ErrorMessage = ex.Message }
            };
        }

        await MessageSerializer.SendAsync(ws, response, ct);
    }

    public async Task HandleFileRootManageRequestAsync(RemexMessage message, WebSocket ws, CancellationToken ct)
    {
        var req = message.FileRootManageRequest;
        if (req is null) return;

        RemexMessage response;
        try
        {
            IReadOnlyList<FileSharedRoot> updatedRoots;
            switch (req.Operation)
            {
                case "add":
                    if (string.IsNullOrWhiteSpace(req.SourceRootId) || string.IsNullOrWhiteSpace(req.SourceRelativePath))
                        throw new ArgumentException("SourceRootId and SourceRelativePath are required for add.");
                    updatedRoots = await fileTransferService.AddRootFromPathAsync(req.SourceRootId, req.SourceRelativePath, ct);
                    break;
                case "remove":
                    if (string.IsNullOrWhiteSpace(req.RootId))
                        throw new ArgumentException("RootId is required for remove.");
                    updatedRoots = await fileTransferService.RemoveRootAsync(req.RootId, ct);
                    break;
                default:
                    throw new ArgumentException($"Unknown operation '{req.Operation}'.");
            }

            response = new RemexMessage
            {
                Type = MessageTypes.FileRootManageResponse,
                FileRootManageResponse = new FileRootManageResponse { RequestId = req.RequestId, Roots = [.. updatedRoots] }
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "FileRootManage {Operation} failed", req.Operation);
            response = new RemexMessage
            {
                Type = MessageTypes.FileRootManageResponse,
                FileRootManageResponse = new FileRootManageResponse { RequestId = req.RequestId, Roots = [], ErrorMessage = ex.Message }
            };
        }

        await MessageSerializer.SendAsync(ws, response, ct);
    }

    // ── 2.1 File Sharing Overhaul (protocolVersion 3) — WP2: full-device volume enumeration ──

    /// <summary>
    /// Handles a <c>file_volumes_request</c> (plan §1.2). Full-device browse is consent-gated per paired
    /// device: if <paramref name="clientId"/> does not already hold a full-browse grant, a consent prompt
    /// is raised on this (serving) host and the response is held until the user decides or the 60-second
    /// timeout auto-denies. On a grant the mounted volumes are enumerated and returned; on a deny an empty
    /// list with <c>fullBrowseGranted=false</c> is returned (a deny is not an error).
    /// </summary>
    public async Task HandleFileVolumesRequestAsync(
        RemexMessage message, WebSocket ws, string? clientId, CancellationToken ct)
    {
        var req = message.FileVolumesRequest;
        if (req is null) return;

        RemexMessage response;
        try
        {
            if (string.IsNullOrWhiteSpace(clientId))
                throw new UnauthorizedAccessException("A paired client identity is required to browse volumes.");

            var granted = await fileTrustService.IsFullBrowseGrantedAsync(clientId, ct);
            if (!granted)
            {
                var consent = new FileConsentRequest
                {
                    ConsentId = Guid.NewGuid().ToString("N"),
                    Kind = FileConsentKinds.FullBrowse,
                };
                var decision = await fileTrustService.RequestConsentAsync(clientId, consent, ct);
                granted = decision.Granted;
            }

            var volumes = granted ? volumeEnumerator.Enumerate() : Array.Empty<FileVolumeInfo>();
            response = new RemexMessage
            {
                Type = MessageTypes.FileVolumesResponse,
                FileVolumesResponse = new FileVolumesResponse
                {
                    RequestId = req.RequestId,
                    Volumes = [.. volumes],
                    FullBrowseGranted = granted,
                }
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "File volumes request failed.");
            response = new RemexMessage
            {
                Type = MessageTypes.FileVolumesResponse,
                FileVolumesResponse = new FileVolumesResponse
                {
                    RequestId = req.RequestId,
                    Volumes = [],
                    FullBrowseGranted = false,
                    ErrorMessage = ex.Message,
                }
            };
        }

        await MessageSerializer.SendAsync(ws, response, ct);
    }

    // ── 2.1 File Sharing Overhaul (protocolVersion 3) — WP-jjdb: consent-response + incoming push ──

    /// <summary>
    /// Handles an inbound <c>file_consent_response</c> (plan §1.2 / §2). This is the peer's decision for a
    /// consent prompt this (serving) host previously raised via <see cref="IFileTrustService.RequestConsentAsync"/>
    /// — it resolves the matching pending prompt exactly as the local consent UI would. Resolution is keyed
    /// solely by <see cref="FileConsentResponse.ConsentId"/>, so whichever responder arrives first (the local
    /// dialog or this remote message) wins; an unknown/already-resolved id is a no-op. There is no reply.
    /// </summary>
    public void HandleFileConsentResponse(RemexMessage message)
    {
        var response = message.FileConsentResponse;
        if (response is null)
            return;

        fileTrustService.ResolveConsent(response.ConsentId, response.Granted, response.Remember);
        logger.LogInformation(
            "Resolved file consent {ConsentId}: granted={Granted}, remember={Remember}.",
            response.ConsentId, response.Granted, response.Remember);
    }

    /// <summary>
    /// Handles an inbound <c>file_push_offer</c> (plan §1.2 / §2): a paired client offering to push one or
    /// more files to this PC. Incoming pushes are consent-gated per device — if <paramref name="clientId"/>
    /// does not already hold an auto-accept-incoming grant, a consent prompt is raised on this host and held
    /// until the user decides or the 60-second timeout auto-denies. On acceptance the host assigns a fresh
    /// transfer id per offered file (index-aligned to <see cref="FilePushOffer.Files"/>) and returns them in
    /// the <c>file_push_response</c>; the client then negotiates each file with a
    /// <c>file_transfer_offer(mode="push")</c> carrying its assigned id. On denial (or timeout) the response
    /// is <c>accepted=false</c> with no ids (a deny is not an error).
    /// </summary>
    public async Task HandleFilePushOfferAsync(
        RemexMessage message, WebSocket ws, string? clientId, CancellationToken ct)
    {
        var offer = message.FilePushOffer;
        if (offer is null) return;

        RemexMessage response;
        try
        {
            var consent = new FileConsentRequest
            {
                ConsentId = Guid.NewGuid().ToString("N"),
                Kind = FileConsentKinds.IncomingPush,
                Detail = DescribePushFiles(offer.Files),
            };

            var decision = await fileTrustService.RequestConsentAsync(clientId ?? string.Empty, consent, ct);

            string[]? transferIds = null;
            if (decision.Granted)
            {
                // Receiver-assigned ids, index-aligned to the offered files. The client echoes each id back
                // on its follow-up file_transfer_offer(mode="push") so the receive session lines up 1:1.
                transferIds = new string[offer.Files.Length];
                for (var i = 0; i < transferIds.Length; i++)
                    transferIds[i] = Guid.NewGuid().ToString("N");
            }

            response = new RemexMessage
            {
                Type = MessageTypes.FilePushResponse,
                ProtocolVersion = ProtocolVersionPolicy.Current,
                FilePushResponse = new FilePushResponse
                {
                    PushId = offer.PushId,
                    Accepted = decision.Granted,
                    TransferIds = transferIds,
                }
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "File push offer {PushId} failed.", offer.PushId);
            response = new RemexMessage
            {
                Type = MessageTypes.FilePushResponse,
                ProtocolVersion = ProtocolVersionPolicy.Current,
                FilePushResponse = new FilePushResponse
                {
                    PushId = offer.PushId,
                    Accepted = false,
                }
            };
        }

        await MessageSerializer.SendAsync(ws, response, ct);
    }

    /// <summary>
    /// Builds the human-readable <see cref="FileConsentRequest.Detail"/> for an incoming-push prompt: the
    /// offered file names (capped) plus the total size. Kept data-forward (names + a locale-neutral size)
    /// so the localized chrome lives in the consent dialog, not in this wire summary.
    /// </summary>
    private static string DescribePushFiles(FilePushFile[] files)
    {
        if (files is null || files.Length == 0)
            return string.Empty;

        const int maxNames = 5;
        var names = files.Take(maxNames).Select(f => f.Name);
        var joined = string.Join(", ", names);
        if (files.Length > maxNames)
            joined += ", …";

        var totalBytes = files.Sum(f => f.Size);
        return $"{joined} ({FormatBytes(totalBytes)})";
    }

    /// <summary>Formats a byte count into a compact, locale-neutral size string (e.g. "12.4 MB").</summary>
    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";

        string[] units = ["KB", "MB", "GB", "TB"];
        double size = bytes;
        var unit = -1;
        do
        {
            size /= 1024;
            unit++;
        }
        while (size >= 1024 && unit < units.Length - 1);

        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture, $"{size:0.#} {units[unit]}");
    }

    /// <summary>
    /// Handles a <c>file_search_request</c> (plan §1.2). Runs a bounded recursive name search under the
    /// requested root subtree; results are capped at <see cref="FileTransferLimits.SearchMaxResults"/> and
    /// <c>truncated</c> is set when the cap is hit. Search runs against a configured shared root (already
    /// gated by pairing), the same as browse — it is not consent-gated.
    /// </summary>
    public async Task HandleFileSearchRequestAsync(RemexMessage message, WebSocket ws, CancellationToken ct)
    {
        var req = message.FileSearchRequest;
        if (req is null) return;

        RemexMessage response;
        try
        {
            var entries = await fileTransferService.SearchAsync(
                req.RootId, req.RelativePath ?? string.Empty, req.Query, req.MaxResults, ct);

            var effectiveCap = req.MaxResults <= 0
                ? FileTransferLimits.SearchMaxResults
                : Math.Min(req.MaxResults, FileTransferLimits.SearchMaxResults);

            response = new RemexMessage
            {
                Type = MessageTypes.FileSearchResponse,
                FileSearchResponse = new FileSearchResponse
                {
                    RequestId = req.RequestId,
                    Entries = [.. entries],
                    Truncated = entries.Count >= effectiveCap,
                }
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "File search failed for root {RootId}, path {Path}", req.RootId, req.RelativePath);
            response = new RemexMessage
            {
                Type = MessageTypes.FileSearchResponse,
                FileSearchResponse = new FileSearchResponse
                {
                    RequestId = req.RequestId,
                    Entries = [],
                    Truncated = false,
                    ErrorMessage = ex.Message,
                }
            };
        }

        await MessageSerializer.SendAsync(ws, response, ct);
    }

    /// <summary>
    /// Handles a <c>file_metadata_request</c> (plan §1.2): detailed metadata for a single file/directory
    /// (size, timestamps, item count, MIME type, read-only).
    /// </summary>
    public async Task HandleFileMetadataRequestAsync(RemexMessage message, WebSocket ws, CancellationToken ct)
    {
        var req = message.FileMetadataRequest;
        if (req is null) return;

        RemexMessage response;
        try
        {
            var meta = await fileTransferService.GetMetadataAsync(req.RootId, req.RelativePath, ct);
            response = new RemexMessage
            {
                Type = MessageTypes.FileMetadataResponse,
                FileMetadataResponse = new FileMetadataResponse
                {
                    RequestId = req.RequestId,
                    Size = meta.Size,
                    CreatedUtc = meta.CreatedUtc,
                    ModifiedUtc = meta.ModifiedUtc,
                    IsDirectory = meta.IsDirectory,
                    ItemCount = meta.ItemCount,
                    MimeType = meta.MimeType,
                    ReadOnly = meta.ReadOnly,
                }
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "File metadata failed for root {RootId}, path {Path}", req.RootId, req.RelativePath);
            response = new RemexMessage
            {
                Type = MessageTypes.FileMetadataResponse,
                FileMetadataResponse = new FileMetadataResponse
                {
                    RequestId = req.RequestId,
                    ErrorMessage = ex.Message,
                }
            };
        }

        await MessageSerializer.SendAsync(ws, response, ct);
    }

    /// <summary>
    /// Handles a <c>file_thumbnail_request</c> (plan §1.2): a small base64 JPEG for an image (images only in
    /// v1). A missing thumbnail (unsupported type / undecodable) returns a null <c>jpegBase64</c>, not an error.
    /// </summary>
    public async Task HandleFileThumbnailRequestAsync(RemexMessage message, WebSocket ws, CancellationToken ct)
    {
        var req = message.FileThumbnailRequest;
        if (req is null) return;

        RemexMessage response;
        try
        {
            var maxDim = req.MaxDim > 0 ? req.MaxDim : FileTransferLimits.ThumbnailDefaultMaxDim;
            var jpegBase64 = await fileTransferService.GetThumbnailBase64Async(req.RootId, req.RelativePath, maxDim, ct);
            response = new RemexMessage
            {
                Type = MessageTypes.FileThumbnailResponse,
                FileThumbnailResponse = new FileThumbnailResponse
                {
                    RequestId = req.RequestId,
                    JpegBase64 = jpegBase64,
                }
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "File thumbnail failed for root {RootId}, path {Path}", req.RootId, req.RelativePath);
            response = new RemexMessage
            {
                Type = MessageTypes.FileThumbnailResponse,
                FileThumbnailResponse = new FileThumbnailResponse
                {
                    RequestId = req.RequestId,
                    ErrorMessage = ex.Message,
                }
            };
        }

        await MessageSerializer.SendAsync(ws, response, ct);
    }

    /// <summary>
    /// Combines a parent relative path and a (already validated) child name into a single forward-slash
    /// relative path. An empty/whitespace parent means the child sits directly under the root.
    /// </summary>
    private static string CombineRelative(string? parent, string name)
        => string.IsNullOrWhiteSpace(parent)
            ? name
            : $"{parent.TrimEnd('/', '\\')}/{name}";

    public async Task CleanupAllTransfersAsync()
    {
        foreach (var id in _activeTransfers.Keys.ToList())
            await CleanupTransferAsync(id, deleteFile: true);
    }

    private async Task CleanupTransferAsync(string transferId, bool deleteFile)
    {
        if (!_activeTransfers.TryRemove(transferId, out var state)) return;

        try { await state.FileStream.DisposeAsync(); } catch { /* best-effort */ }
        state.Hasher.Dispose();

        if (deleteFile && state.Direction == "upload" && File.Exists(state.RemotePath))
        {
            try { File.Delete(state.RemotePath); }
            catch (Exception ex) { logger.LogWarning(ex, "Failed to delete partial upload: {Path}", state.RemotePath); }
        }
    }

    private async Task StreamDownloadAsync(FileTransferState state, WebSocket ws, CancellationToken ct)
    {
        const int chunkSize = 65536;
        var buffer = new byte[chunkSize];
        int read;
        int chunkCount = 0;

        try
        {
            while ((read = await state.FileStream.ReadAsync(buffer.AsMemory(0, chunkSize), ct)) > 0)
            {
                var slice = buffer[..read];
                state.Hasher.TransformBlock(slice, 0, read, null, 0);
                state.BytesTransferred += read;
                chunkCount++;

                await MessageSerializer.SendAsync(ws, new RemexMessage
                {
                    Type = MessageTypes.FileTransferChunk,
                    FileTransferChunk = new FileTransferChunk
                    {
                        TransferId = state.TransferId,
                        Offset = state.BytesTransferred - read,
                        DataBase64 = Convert.ToBase64String(slice)
                    }
                }, ct);

                if (chunkCount % ProgressChunkInterval == 0)
                {
                    await MessageSerializer.SendAsync(ws, new RemexMessage
                    {
                        Type = MessageTypes.FileTransferProgress,
                        FileTransferProgress = new FileTransferProgress
                        {
                            TransferId = state.TransferId,
                            BytesTransferred = state.BytesTransferred,
                            TotalBytes = state.TotalBytes
                        }
                    }, ct);
                }
            }

            state.Hasher.TransformFinalBlock([], 0, 0);
            var hash = Convert.ToBase64String(state.Hasher.Hash!);

            await MessageSerializer.SendAsync(ws, new RemexMessage
            {
                Type = MessageTypes.FileTransferEnd,
                FileTransferEnd = new FileTransferEnd
                {
                    TransferId = state.TransferId,
                    Success = true,
                    Sha256Base64 = hash,
                }
            }, ct);

            logger.LogInformation("Download complete for {TransferId}, sha256={Hash}", state.TransferId, hash);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Download cancelled for {TransferId}", state.TransferId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Download failed for {TransferId}", state.TransferId);
            try
            {
                await MessageSerializer.SendAsync(ws, new RemexMessage
                {
                    Type = MessageTypes.FileTransferEnd,
                    FileTransferEnd = new FileTransferEnd
                    {
                        TransferId = state.TransferId,
                        Success = false,
                        ErrorMessage = ex.Message
                    }
                }, ct);
            }
            catch { /* connection already gone */ }
        }
        finally
        {
            await CleanupTransferAsync(state.TransferId, deleteFile: false);
        }
    }
}
