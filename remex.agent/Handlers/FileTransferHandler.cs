using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
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
    VolumeEnumerator volumeEnumerator,
    SharedRootReadResolver readResolver)
{
    private const int ProgressChunkInterval = 10;

    // Defensive running cap. FileTransferService applies the same limit to
    // start.TotalBytes, but mobile clients send 0 for unknown content URIs, so
    // a malicious / buggy peer could otherwise stream unbounded bytes past the
    // initial check. Kept identical to FileTransferService.MaxUploadBytes.
    private const long DefaultMaxUploadBytes = 5_000_000_000L;

    /// <summary>
    /// Test-only seam (visible to <c>Remex.Agent.Tests</c> via <c>InternalsVisibleTo</c>). The running
    /// upload cap, overridable so a test can reach the branch without streaming 5 GB. Not used by DI —
    /// the default container only binds constructors, so an <c>init</c> property is invisible to host
    /// bootstrapping and production always gets <see cref="DefaultMaxUploadBytes"/>.
    /// </summary>
    /// <remarks>
    /// IT WAS A <c>const</c> (RemEx-9xs1), and the consequence was that the one guard standing between a
    /// peer that declares <c>totalBytes: 0</c> and an unbounded write had NO coverage at all — proving it
    /// would have meant streaming 5 GB through a unit test, so nobody did, and deleting the branch broke
    /// nothing visible.
    /// <para>
    /// An <c>init</c> property rather than a constructor parameter or a mutable static: the container
    /// cannot pick it up by accident, and there is no shared state for xUnit's parallel runner to race
    /// on — the handler is registered <c>AddTransient</c> and resolved per WebSocket connection.
    /// </para>
    /// </remarks>
    internal long MaxUploadBytes { get; init; } = DefaultMaxUploadBytes;

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

        // STILL TRUE, BUT NOT VIA THE file_push_offer HANDSHAKE (RemEx-e11w). This host accepts
        // client-initiated incoming files — that is what the flag claims — but it accepts them the
        // ordinary way, as file_transfer_offer(mode="push") onto a shared writable root. The two-step
        // offer/response consent handshake is DELIBERATELY UNIMPLEMENTED on this side: a phone-initiated
        // push is an upload, so the writable root is the consent and the extra prompt was protecting
        // nothing while reading as though it were. An inbound file_push_offer is therefore not answered
        // at all. Do not "fix" that silence by reviving the handler without re-reading RemEx-e11w.
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
        if (req is null)
        {
            // ANSWER, RATHER THAN GO QUIET (RemEx-rie6). This used to `return` and send nothing at all,
            // so a request whose body did not bind produced pure silence on the socket. RemEx-9i4b fixed
            // the one instance that had been observed; this is the same one-line shape in every sibling
            // that owes a response, and it is reachable from the wire - a client that spells the wrapper
            // differently across a protocol change is all it takes.
            //
            // WHAT THE PEER ACTUALLY DOES WITH THIS VARIES, and the difference is worth knowing before
            // reading too much into it. The requestId is empty because the part of the message that
            // would carry it is the part that is missing, and most phone-side handlers correlate on it:
            // browse, manage, rootManage, search, metadata and thumbnail all drop a response whose id
            // they do not recognise. For those this is protocol correctness and a log line, not a
            // cleared spinner - they fall through to their own 30s timeout, or to a stale affordance
            // where there is no timeout. Only fileVolumesResponse and fileRootsResponse apply whatever
            // arrives, which is why RemEx-9i4b's volumes fix was directly visible to the user.
            //
            // SENDING IT IS SAFE, not merely probably safe, and that is worth stating because "send an
            // uncorrelated response" sounds like it could clobber something. Every pending id on the
            // phone is a String? initialised to null, while optString("requestId") yields "" - and in
            // Kotlin "" != null, so an empty id can never false-match a waiting slot. That includes
            // pendingDestinationRequestId, which is set but never cleared. There is no state to corrupt.
            //
            // The log line is the part that helps regardless. "No response and no log line pointing at
            // the cause" is the shape that bricked v3 file transfer (RemEx-y6x6); this removes the
            // second half of that everywhere, and the first half where the peer will listen.
            logger.LogWarning("Received a file_browse_request with no request body; answering with an error.");
            await MessageSerializer.SendAsync(ws, new RemexMessage
            {
                Type = MessageTypes.FileBrowseResponse,
                FileBrowseResponse = new FileBrowseResponse
                {
                    RequestId = string.Empty,
                    Entries = [],
                    ErrorMessage = "The file_browse_request carried no request body.",
                }
            }, ct);
            return;
        }

        RemexMessage response;
        try
        {
            if (string.IsNullOrWhiteSpace(req.RootId))
                throw new UnauthorizedAccessException("A shared root is required for remote browsing.");

            IReadOnlyList<FileEntry> entries;
            if (await IsConfiguredRootAsync(req.RootId, ct))
            {
                entries = await fileTransferService.BrowseAsync(req.RootId, req.RelativePath ?? string.Empty, ct);
            }
            else
            {
                // Not a configured shared root: treat as a full-device VOLUME browse. Gated on this paired
                // client holding a full-browse consent grant (re-verified here, never trusting RootId), and
                // on RootId being a genuine mounted volume. BrowseVolumeAsync bounds navigation within it.
                var volume = await ResolveConsentedVolumeAsync(req.RootId, clientId, ct);
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

    public async Task HandleFileTransferStartAsync(RemexMessage message, WebSocket ws, string? clientId, CancellationToken ct)
    {
        var start = message.FileTransferStart;
        if (start is null)
        {
            // Answer rather than go quiet - see the note on HandleFileBrowseRequestAsync (RemEx-rie6).
            // The phone keys transfer state by transferId, which is also missing here, so this is a
            // log-and-be-honest case rather than one the UI can attribute to a transfer.
            logger.LogWarning("Received a file_transfer_start with no request body; answering with an error.");
            await MessageSerializer.SendAsync(ws, new RemexMessage
            {
                Type = MessageTypes.FileTransferEnd,
                FileTransferEnd = new FileTransferEnd
                {
                    TransferId = string.Empty,
                    Success = false,
                    ErrorMessage = "The file_transfer_start carried no request body.",
                }
            }, ct);
            return;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(start.RemoteRootId))
                throw new UnauthorizedAccessException("A shared root is required for file transfer operations.");

            var remoteRelativePath = start.RemoteRelativePath ?? string.Empty;
            Stream stream;
            if (start.Direction == "upload")
            {
                // Full browse never exposes write on the volume itself (RemEx-hb1t) — but when the target
                // resolves inside a pinned shared root, re-express the request against that root so the
                // same folder behaves identically however it was reached (RemEx-hb1t.3). Genuinely
                // un-pinned targets still refuse.
                var (writeRootId, writeRelativePath) = await IsConfiguredRootAsync(start.RemoteRootId, ct)
                    ? (start.RemoteRootId, remoteRelativePath)
                    : await RemapUnconfiguredWriteTargetAsync(start.RemoteRootId, remoteRelativePath, ct);
                stream = await fileTransferService.OpenForWriteAsync(writeRootId, writeRelativePath, start.TotalBytes, ct);
            }
            else
            {
                // Download accepts either a pinned shared root or a bare full-device-browsed volume
                // (RemEx-39jw) — read-only, so this is safe to extend, unlike upload above. Same resolver as
                // the v3 sender, so both download paths behave identically (RemEx-hb1t.6).
                stream = await readResolver.OpenForReadAsync(start.RemoteRootId, remoteRelativePath, clientId, ct);
            }

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
        if (req is null)
        {
            // Answer rather than go quiet - see the note on HandleFileBrowseRequestAsync (RemEx-rie6).
            // handleManageResponse correlates through pendingManageOps, so the phone drops this and
            // falls through to its own 30s timeout. NO SHIPPING CLIENT SURFACES IT - the desktop's
            // FileTransferClient correlates every one of these too, and only its roots waiter does not.
            // Sent for protocol correctness and for a future client; the log line is what helps today.
            logger.LogWarning("Received a file_manage_request with no request body; answering with an error.");
            await MessageSerializer.SendAsync(ws, new RemexMessage
            {
                Type = MessageTypes.FileManageResponse,
                FileManageResponse = new FileManageResponse
                {
                    RequestId = string.Empty,
                    Success = false,
                    ErrorMessage = "The file_manage_request carried no request body.",
                }
            }, ct);
            return;
        }

        RemexMessage response;
        try
        {
            // Write-op parity with volume browsing (RemEx-hb1t.3): when the rootId isn't a pinned root,
            // re-map the target(s) onto the pinned root that contains them — or refuse if none does.
            var rootId = req.RootId;
            var relativePath = req.RelativePath;
            var destinationPath = req.DestinationPath;
            if (!await IsConfiguredRootAsync(rootId, ct))
            {
                (rootId, relativePath) = await RemapUnconfiguredWriteTargetAsync(req.RootId, req.RelativePath, ct);
                if (!string.IsNullOrWhiteSpace(destinationPath))
                {
                    // Copy/move destinations are relative to the same rootId — both endpoints must land in
                    // the SAME pinned root, or a volume-mode request could hop across root boundaries.
                    var (destRootId, destRelative) = await RemapUnconfiguredWriteTargetAsync(req.RootId, destinationPath, ct);
                    if (destRootId != rootId)
                        throw new UnauthorizedAccessException(UnpinnedWriteMessage);
                    destinationPath = destRelative;
                }
            }

            // Set only by the copy/move arms, and only when "keep both" actually renamed.
            string? resolvedName = null;

            switch (req.Operation)
            {
                case FileManageOperations.Delete:
                    await fileTransferService.DeleteAsync(rootId, relativePath, ct);
                    break;
                case FileManageOperations.Rename:
                    if (string.IsNullOrWhiteSpace(req.NewName))
                        throw new ArgumentException("NewName is required for rename.");
                    await fileTransferService.RenameAsync(rootId, relativePath, req.NewName, ct);
                    break;
                // ── 2.1 File Sharing Overhaul (protocolVersion 3) — WP3 ──
                case FileManageOperations.Copy:
                    if (string.IsNullOrWhiteSpace(destinationPath))
                        throw new ArgumentException("DestinationPath is required for copy.");
                    resolvedName = await fileTransferService.CopyAsync(
                        rootId, relativePath, destinationPath, req.Overwrite, ct, req.ConflictResolution);
                    break;
                case FileManageOperations.Move:
                    if (string.IsNullOrWhiteSpace(destinationPath))
                        throw new ArgumentException("DestinationPath is required for move.");
                    resolvedName = await fileTransferService.MoveAsync(
                        rootId, relativePath, destinationPath, req.Overwrite, ct, req.ConflictResolution);
                    break;
                case FileManageOperations.Mkdir:
                    // Wire contract: RelativePath is the parent directory (empty = root) and NewName is the
                    // new folder's name — parallels "rename" so the Android/PC mirrors stay mechanical. The
                    // name is validated here (rejects separators / "." / "..") before being combined.
                    if (!FilePathValidation.IsValidFileName(req.NewName, out var nameError))
                        throw new ArgumentException(nameError ?? "Invalid folder name.");
                    var newDirRelativePath = CombineRelative(relativePath, req.NewName!);
                    await fileTransferService.CreateDirectoryAsync(rootId, newDirRelativePath, ct);
                    break;
                default:
                    throw new ArgumentException($"Unknown operation '{req.Operation}'.");
            }

            response = new RemexMessage
            {
                Type = MessageTypes.FileManageResponse,
                FileManageResponse = new FileManageResponse
                {
                    RequestId = req.RequestId,
                    Success = true,

                    // Reported even on success, because a "keep both" that succeeded SILENTLY would
                    // leave the user believing they have report.pdf when the file on disk is
                    // report (2).pdf. Null whenever the requested name was the one used.
                    ResolvedName = resolvedName,
                }
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "FileManage {Operation} failed for {Path}", req.Operation, req.RelativePath);
            response = new RemexMessage
            {
                Type = MessageTypes.FileManageResponse,
                FileManageResponse = new FileManageResponse
                {
                    RequestId = req.RequestId,
                    Success = false,
                    ErrorMessage = ex.Message,

                    // THE SENDER THE CODE WAS WAITING FOR. The bead is explicit that the field must
                    // not exist before something populates it - a declared-but-never-set wire field
                    // is how RemEx-mneb left a handler that could not fire.
                    ErrorCode = (ex as FileConflictException)?.ErrorCode,
                    ConflictingName = (ex as FileConflictException)?.ConflictingName,
                }
            };
        }

        await MessageSerializer.SendAsync(ws, response, ct);
    }

    public async Task HandleFileHashRequestAsync(RemexMessage message, WebSocket ws, string? clientId, CancellationToken ct)
    {
        var req = message.FileHashRequest;
        if (req is null)
        {
            // Answer rather than go quiet - see the note on HandleFileBrowseRequestAsync (RemEx-rie6).
            // RemEx-0e54 was the matching failure on the other side: a hash request that never got an
            // answer hung forever, which is exactly what this branch used to guarantee. That one is closed
            // and the desktop's hash wait is bounded now (HashRequestTimeoutSeconds), so this is about the
            // host being honest rather than about an unbounded wait.
            logger.LogWarning("Received a file_hash_request with no request body; answering with an error.");
            await MessageSerializer.SendAsync(ws, new RemexMessage
            {
                Type = MessageTypes.FileHashResponse,
                FileHashResponse = new FileHashResponse
                {
                    RequestId = string.Empty,
                    ErrorMessage = "The file_hash_request carried no request body.",
                }
            }, ct);
            return;
        }

        RemexMessage response;
        try
        {
            var hash = await IsConfiguredRootAsync(req.RootId, ct)
                ? await fileTransferService.ComputeSha256Async(req.RootId, req.RelativePath, ct)
                : await fileTransferService.ComputeVolumeSha256Async(
                    (await ResolveConsentedVolumeAsync(req.RootId, clientId, ct)).Path, req.RelativePath, ct);
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
        if (req is null)
        {
            // Answer rather than go quiet - see the note on HandleFileBrowseRequestAsync (RemEx-rie6).
            // handleRootManageResponse correlates through pendingRootManageOps; 30s timeout behind it.
            logger.LogWarning("Received a file_root_manage_request with no request body; answering with an error.");
            await MessageSerializer.SendAsync(ws, new RemexMessage
            {
                Type = MessageTypes.FileRootManageResponse,
                FileRootManageResponse = new FileRootManageResponse
                {
                    RequestId = string.Empty,
                    Roots = [],
                    ErrorMessage = "The file_root_manage_request carried no request body.",
                }
            }, ct);
            return;
        }

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
    /// is raised — on the phone that asked when it can render one, otherwise on this (serving) host
    /// (RemEx-220r) — and the response is held until the user decides or the 60-second timeout
    /// auto-denies. It may instead be refused outright, with no prompt and no wait, when the asking
    /// client no longer has a live session. On a grant the mounted volumes are enumerated and returned; on a deny an empty
    /// list with <c>fullBrowseGranted=false</c> is returned (a deny is not an error) — carrying a
    /// <c>denyReason</c> when nobody was asked, so the phone can tell that apart from a refusal its user made
    /// (RemEx-l580). A request whose body did not bind is answered too — with an error response rather
    /// than with nothing at all (RemEx-9i4b).
    /// </summary>
    public async Task HandleFileVolumesRequestAsync(
        RemexMessage message, WebSocket ws, string? clientId, CancellationToken ct)
    {
        var req = message.FileVolumesRequest;
        if (req is null)
        {
            // ANSWER, RATHER THAN GO QUIET (RemEx-9i4b). This used to `return` and send nothing at
            // all - not a response, not an error response, nothing - so a file_volumes_request whose
            // body did not bind produced pure silence on the socket. The phone has no timeout of its
            // own here, so it sat on "Requesting volumes..." indefinitely and the user saw "Peer did
            // not respond" with no log line pointing at the cause: the exact shape that bricked v3
            // file transfer (RemEx-y6x6). It is reachable from the wire - a client that spells the
            // wrapper differently across a protocol change is all it takes - so this is an answer the
            // host owes, not an impossible state.
            //
            // The id is empty because the part of the message that would carry it is the part that is
            // missing. That still reaches the phone, which is the client this branch is for:
            // handleVolumesResponse applies whatever arrives instead of correlating by requestId, so
            // an uncorrelated error clears the spinner and shows the failure. The PC's own
            // FileTransferClient DOES correlate, and would fall through to its own bounded timeout
            // instead - which costs it nothing, because it always sends a body and so cannot reach
            // this branch at all.
            //
            // ErrorMessage, not DenyReason: this is a malformed request, not a refusal. DenyReason is
            // for a host that declined without asking anyone (RemEx-l580), and routing a protocol
            // fault through it would tell the phone a person said no.
            logger.LogWarning("Received a file_volumes_request with no request body; answering with an error.");
            await MessageSerializer.SendAsync(ws, new RemexMessage
            {
                Type = MessageTypes.FileVolumesResponse,
                FileVolumesResponse = new FileVolumesResponse
                {
                    RequestId = string.Empty,
                    Volumes = [],
                    FullBrowseGranted = false,
                    ErrorMessage = "The file_volumes_request carried no request body.",
                }
            }, ct);
            return;
        }

        RemexMessage response;
        try
        {
            if (string.IsNullOrWhiteSpace(clientId))
                throw new UnauthorizedAccessException("A paired client identity is required to browse volumes.");

            // Null unless the host refused without asking anyone; see FileConsentDenyReasons. It stays
            // null on the already-granted path above, which never denies anything (RemEx-l580).
            string? denyReason = null;

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
                denyReason = decision.DenyReason;
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
                    DenyReason = denyReason,
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
    /// — it resolves the matching pending prompt exactly as the local consent UI would. Whichever
    /// responder arrives first (the local dialog or this remote message) wins; an unknown or
    /// already-resolved id is a no-op. There is no reply.
    /// </summary>
    /// <param name="clientId">
    /// The AUTHENTICATED id of the connection this arrived on — not <c>message.ClientId</c>, which is
    /// whatever the sender wrote. Resolution used to key on the consent id alone, which was harmless
    /// only while nothing sent these prompts; now that the host asks a named phone (RemEx-220r), an
    /// unbound response would let any paired device answer somebody else's question.
    /// </param>
    public void HandleFileConsentResponse(RemexMessage message, string? clientId)
    {
        var response = message.FileConsentResponse;
        if (response is null)
            return;

        var applied = fileTrustService.TryResolveRemoteConsent(
            clientId, response.ConsentId, response.Granted, response.Remember);

        if (!applied)
        {
            // Expected and uninteresting in the ordinary case — a prompt that already timed out, or
            // that the PC dialog answered first. Logged at a level that will not drown a real log,
            // but logged, because the other cause is a client answering a question it was not asked.
            logger.LogDebug(
                "Ignored a file consent response for {ConsentId}: no matching prompt for this client.",
                response.ConsentId);
            return;
        }

        logger.LogInformation(
            "Resolved file consent {ConsentId}: granted={Granted}, remember={Remember}.",
            response.ConsentId, response.Granted, response.Remember);
    }

    /// <summary>
    /// Handles a <c>file_search_request</c> (plan §1.2). Runs a bounded recursive name search under the
    /// requested root subtree; results are capped at <see cref="FileTransferLimits.SearchMaxResults"/> and
    /// <c>truncated</c> is set when the cap is hit. Search runs against a configured shared root (already
    /// gated by pairing), the same as browse — it is not consent-gated.
    /// </summary>
    public async Task HandleFileSearchRequestAsync(RemexMessage message, WebSocket ws, string? clientId, CancellationToken ct)
    {
        var req = message.FileSearchRequest;
        if (req is null)
        {
            // Answer rather than go quiet - see the note on HandleFileBrowseRequestAsync (RemEx-rie6).
            // handleSearchResponse correlates (FileTransferViewModel.kt) and has NO timeout behind it,
            // so the phone's search affordance stays stale. Named in the bead as needing a different
            // answer - there is none available here, because the id it would need is the missing part.
            logger.LogWarning("Received a file_search_request with no request body; answering with an error.");
            await MessageSerializer.SendAsync(ws, new RemexMessage
            {
                Type = MessageTypes.FileSearchResponse,
                FileSearchResponse = new FileSearchResponse
                {
                    RequestId = string.Empty,
                    Entries = [],
                    ErrorMessage = "The file_search_request carried no request body.",
                }
            }, ct);
            return;
        }

        RemexMessage response;
        try
        {
            var relativePath = req.RelativePath ?? string.Empty;
            var entries = await IsConfiguredRootAsync(req.RootId, ct)
                ? await fileTransferService.SearchAsync(req.RootId, relativePath, req.Query, req.MaxResults, ct)
                : await fileTransferService.SearchVolumeAsync(
                    (await ResolveConsentedVolumeAsync(req.RootId, clientId, ct)).Path, relativePath, req.Query, req.MaxResults, ct);

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
    public async Task HandleFileMetadataRequestAsync(RemexMessage message, WebSocket ws, string? clientId, CancellationToken ct)
    {
        var req = message.FileMetadataRequest;
        if (req is null)
        {
            // Answer rather than go quiet - see the note on HandleFileBrowseRequestAsync (RemEx-rie6).
            // handleMetadataResponse correlates on pendingPropertiesRequestId; no timeout behind it.
            logger.LogWarning("Received a file_metadata_request with no request body; answering with an error.");
            await MessageSerializer.SendAsync(ws, new RemexMessage
            {
                Type = MessageTypes.FileMetadataResponse,
                FileMetadataResponse = new FileMetadataResponse
                {
                    RequestId = string.Empty,
                    ErrorMessage = "The file_metadata_request carried no request body.",
                }
            }, ct);
            return;
        }

        RemexMessage response;
        try
        {
            var meta = await IsConfiguredRootAsync(req.RootId, ct)
                ? await fileTransferService.GetMetadataAsync(req.RootId, req.RelativePath, ct)
                : await fileTransferService.GetVolumeMetadataAsync(
                    (await ResolveConsentedVolumeAsync(req.RootId, clientId, ct)).Path, req.RelativePath, ct);
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
    public async Task HandleFileThumbnailRequestAsync(RemexMessage message, WebSocket ws, string? clientId, CancellationToken ct)
    {
        var req = message.FileThumbnailRequest;
        if (req is null)
        {
            // Answer rather than go quiet - see the note on HandleFileBrowseRequestAsync (RemEx-rie6).
            // handleThumbnailResponse correlates through pendingThumbnailPaths; no timeout behind it.
            logger.LogWarning("Received a file_thumbnail_request with no request body; answering with an error.");
            await MessageSerializer.SendAsync(ws, new RemexMessage
            {
                Type = MessageTypes.FileThumbnailResponse,
                FileThumbnailResponse = new FileThumbnailResponse
                {
                    RequestId = string.Empty,
                    ErrorMessage = "The file_thumbnail_request carried no request body.",
                }
            }, ct);
            return;
        }

        RemexMessage response;
        try
        {
            var maxDim = req.MaxDim > 0 ? req.MaxDim : FileTransferLimits.ThumbnailDefaultMaxDim;
            var jpegBase64 = await IsConfiguredRootAsync(req.RootId, ct)
                ? await fileTransferService.GetThumbnailBase64Async(req.RootId, req.RelativePath, maxDim, ct)
                : await fileTransferService.GetVolumeThumbnailBase64Async(
                    (await ResolveConsentedVolumeAsync(req.RootId, clientId, ct)).Path, req.RelativePath, maxDim, ct);
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

    /// <inheritdoc cref="SharedRootReadResolver.IsConfiguredRootAsync"/>
    private Task<bool> IsConfiguredRootAsync(string rootId, CancellationToken ct)
        => readResolver.IsConfiguredRootAsync(rootId, ct);

    /// <summary>
    /// Resolves a RootId that is NOT a configured shared root to a genuine, consent-granted mounted volume
    /// (RemEx-39jw). Used by the read-only volume ops (download/hash/search/metadata/thumbnail); full browse
    /// never exposes write/delete (RemEx-hb1t), so callers that need a writable root must reject an
    /// unconfigured RootId instead of calling this.
    ///
    /// <para>Delegates to <see cref="SharedRootReadResolver"/> — the shared decision point the v3 sender in
    /// <see cref="TransferSessionManager"/> also uses. Keeping a second private copy here is what allowed the
    /// two paths to disagree in RemEx-hb1t.6; do not re-inline it.</para>
    /// </summary>
    private Task<FileVolumeInfo> ResolveConsentedVolumeAsync(string rootId, string? clientId, CancellationToken ct)
        => readResolver.ResolveConsentedVolumeAsync(rootId, clientId, ct);

    private const string UnpinnedWriteMessage =
        "This folder isn't one of the PC's shared folders. On the PC, add it to RemEx's shared folders, then try again.";

    /// <summary>
    /// Write-op parity for full-device browsing (RemEx-hb1t.3). The rootId is NOT a configured root: if it
    /// is a genuine mounted volume and the target resolves INSIDE a pinned shared root, re-express the
    /// request as (pinned rootId, re-based relative path) — the exact reference the client could have used
    /// directly, so nothing is widened and the pinned root's permission flags still gate the operation.
    /// No full-browse consent is required for the mapped case (unlike <see cref="ResolveConsentedVolumeAsync"/>,
    /// which grants volume-wide read): the mapped operation grants nothing the pinned rootId doesn't already.
    /// Genuinely un-pinned targets refuse with a plain-English pointer to the shared-folders list.
    /// </summary>
    private async Task<(string RootId, string RelativePath)> RemapUnconfiguredWriteTargetAsync(
        string rootId, string relativePath, CancellationToken ct)
    {
        var volume = volumeEnumerator.Enumerate().FirstOrDefault(v => v.Id == rootId)
            ?? throw new UnauthorizedAccessException($"Unknown shared root '{rootId}'.");
        return await fileTransferService.TryMapVolumePathToConfiguredRootAsync(volume.Path, relativePath, ct)
            ?? throw new UnauthorizedAccessException(UnpinnedWriteMessage);
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
                // NO SLICE. buffer[..read] allocated a fresh array every chunk purely to carry a
                // length that both consumers below already accept as an offset and count - so the
                // copy existed to tell them something they were being told anyway. On the v2 base64
                // path that is one array per chunk for the whole of a large file (RemEx-ygapg).
                state.Hasher.TransformBlock(buffer, 0, read, null, 0);
                state.BytesTransferred += read;
                chunkCount++;

                await MessageSerializer.SendAsync(ws, new RemexMessage
                {
                    Type = MessageTypes.FileTransferChunk,
                    FileTransferChunk = new FileTransferChunk
                    {
                        TransferId = state.TransferId,
                        Offset = state.BytesTransferred - read,
                        DataBase64 = Convert.ToBase64String(buffer, 0, read)
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
