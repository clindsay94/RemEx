using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Remex.Core.Guards;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Services;
using Remex.Core.Services.FileTransfer;

namespace Remex.Agent.Services.FileTransfer;

/// <summary>
/// PC-host engine for the v3 binary file-transfer channel (plan §1.1–§1.4, WP4). Owns the transfer
/// negotiation state machine (<c>file_transfer_offer/ready/complete/result/control</c>, control plane on
/// <c>/ws</c>), and the bulk data plane on the dedicated <c>/ws/files</c> binary channel:
/// <see cref="FileFrameEnvelope"/> data frames in, <c>ack</c> frames out (receiver), or data frames out
/// with backpressure driven by inbound acks (sender).
///
/// <para>
/// <b>Receiver</b> (host receives bytes: <c>upload</c> / <c>push</c>): writes a <c>&lt;id&gt;.remexpart</c>
/// staging file plus a sidecar manifest and streams a fresh <see cref="IncrementalHash"/>. On a resume
/// request whose manifest identity matches, the partial is re-hashed and the sender is told to continue
/// from <c>partialLength</c> (plan §1.3 — incremental hash state can't be serialized on the NativeAOT
/// surface, so we re-hash locally). On completion the full-file SHA-256 is the final arbiter: a match is
/// copied into the destination shared root via <see cref="IFileTransferService.OpenForWriteAsync"/> and the
/// staging files are removed; a mismatch (or an incomplete transfer) deletes the partial and reports
/// <c>verified:false</c> — preserving the established "mismatch deletes the file" semantics.
/// </para>
///
/// <para>
/// <b>Sender</b> (host sends bytes: <c>download</c>): reads the host file through
/// <see cref="IFileTransferService.OpenForReadAsync"/> and streams 256 KB data frames, capping outstanding
/// unacked bytes at <see cref="FileTransferLimits.MaxUnackedBytes"/>, then sends
/// <c>file_transfer_complete</c> with the full-file hash for the peer (the receiver-side partial lives on
/// the peer, so host-side download resume is not applicable here).
/// </para>
///
/// <para>This service lives in <c>remex.agent</c> and is NOT NativeAOT-constrained. The staging manifest is
/// host-local state (never a wire message) so it uses the reflection-based <see cref="JsonSerializer"/>,
/// matching the sibling <see cref="FileTransferService"/> / <see cref="FileTrustService"/> stores.</para>
/// </summary>
public sealed class TransferSessionManager : IDisposable
{
    /// <summary>Hard ceiling on a single transfer, matching <c>FileTransferService.MaxUploadBytes</c>.</summary>
    private const long MaxTransferBytes = 5_000_000_000L;

    /// <summary>Orphaned staging partials older than this are swept at startup (plan §1.3).</summary>
    private static readonly TimeSpan OrphanMaxAge = TimeSpan.FromDays(7);

    /// <summary>Upper bound on a single inbound binary frame (payload + JSON header slack).</summary>
    private const int MaxFrameBytes = FileTransferLimits.DataPayloadBytes + (64 * 1024);

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ILogger<TransferSessionManager> _logger;
    private readonly IFileTransferService _fileTransferService;
    private readonly string _stagingDir;

    private readonly ConcurrentDictionary<string, ReceiveSession> _receiveSessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SendSession> _sendSessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, FileChannel> _channels = new(StringComparer.Ordinal);

    public TransferSessionManager(ILogger<TransferSessionManager> logger, IFileTransferService fileTransferService)
        : this(logger, fileTransferService, stagingDir: null)
    {
    }

    /// <summary>Test seam: overrides the staging directory so resume/partial behaviour is exercised hermetically.</summary>
    internal TransferSessionManager(
        ILogger<TransferSessionManager> logger,
        IFileTransferService fileTransferService,
        string? stagingDir)
    {
        _logger = Guard.NotNull(logger);
        _fileTransferService = Guard.NotNull(fileTransferService);

        if (stagingDir is null)
        {
            var legacyFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Remex");
            var baseFolder = RemexDataPaths.ResolveDirectory(legacyFolder);
            _stagingDir = Path.Combine(baseFolder, "transfers", "incoming");
        }
        else
        {
            _stagingDir = stagingDir;
        }

        try
        {
            Directory.CreateDirectory(_stagingDir);
            CleanupOrphans(OrphanMaxAge);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not prepare transfer staging directory {Dir}.", _stagingDir);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Testable receiver core — pure state machine + staging disk I/O, no WebSocket.
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Result of accepting (or resuming) an inbound transfer: whether it was accepted and the byte offset the
    /// sender must (re)start streaming from.
    /// </summary>
    public readonly record struct ReceiveAcceptance(bool Accepted, long StartOffset, string? DeclineReason);

    /// <summary>
    /// Begins (or resumes) receiving an inbound transfer for <paramref name="clientId"/>. Writes the
    /// <c>.remexpart</c> + manifest and opens the partial for append. On <c>resumeRequested</c> with a manifest
    /// whose identity (client + source path + expected size) matches, the partial is re-hashed and
    /// <see cref="ReceiveAcceptance.StartOffset"/> is its length; any mismatch resets to offset 0.
    /// </summary>
    public async Task<ReceiveAcceptance> BeginReceiveAsync(string clientId, FileTransferOffer offer, CancellationToken ct)
    {
        Guard.NotNull(offer);

        if (string.IsNullOrWhiteSpace(offer.TransferId))
            return new ReceiveAcceptance(false, 0, "Missing transferId.");
        if (string.IsNullOrWhiteSpace(offer.DestRoot))
            return new ReceiveAcceptance(false, 0, "A destination shared root is required.");
        if (!Remex.Core.Validation.FilePathValidation.IsValidFileName(offer.FileName, out var nameError))
            return new ReceiveAcceptance(false, 0, nameError);
        if (offer.Size < 0 || offer.Size > MaxTransferBytes)
            return new ReceiveAcceptance(false, 0, $"File size out of range (max {MaxTransferBytes} bytes).");

        var partialPath = PartialPathFor(offer.TransferId);
        var manifestPath = ManifestPathFor(offer.TransferId);
        // An UPLOAD's DestRelativePath is the destination DIRECTORY (not the full path); the file name is
        // carried separately and appended here. (Contrast BeginSendAsync, where a DOWNLOAD's
        // DestRelativePath is the source file's full path.) The Android client was fixed in 2.2.3 to send
        // the directory only — previously it sent 'dir/name', which combined here to 'dir/name/name' and
        // landed every push in a folder named after the file (RemEx-y6x6).
        var hostRelativePath = CombineHostRelative(offer.DestRelativePath, offer.FileName);

        // A re-offer of an already-live transferId (client retry, or a dropped socket where the queue
        // re-sends) must release the prior session's exclusive .remexpart handle BEFORE we touch the
        // partial below. Otherwise the resume re-hash (read) or the FileShare.None open throws
        // IOException "used by another process" and the old handle leaks. (The replace at the tail of
        // this method was too late — the file access above it fails first.)
        if (_receiveSessions.TryRemove(offer.TransferId, out var superseded))
            superseded.DisposeStreamOnly();

        long startOffset = 0;
        IncrementalHash hasher;

        if (offer.ResumeRequested
            && File.Exists(partialPath)
            && TryLoadManifest(manifestPath, out var manifest)
            && manifest is not null
            && string.Equals(manifest.ClientId, clientId, StringComparison.Ordinal)
            && string.Equals(manifest.SourcePath, offer.SourcePath, StringComparison.Ordinal)
            && manifest.ExpectedSize == offer.Size)
        {
            var partialLength = new FileInfo(partialPath).Length;
            if (partialLength <= offer.Size)
            {
                // Re-hash the surviving partial through a fresh IncrementalHash, then continue appending.
                hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await ReHashPartialAsync(partialPath, hasher, ct);
                startOffset = partialLength;
            }
            else
            {
                DeleteStaging(offer.TransferId);
                hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            }
        }
        else
        {
            // Fresh transfer (or a resume that failed identity): discard any stale partial and start clean.
            DeleteStaging(offer.TransferId);
            hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        }

        if (startOffset == 0)
        {
            WriteManifest(manifestPath, new TransferManifest
            {
                TransferId = offer.TransferId,
                ExpectedSize = offer.Size,
                ClientId = clientId,
                SourcePath = offer.SourcePath,
                DestRoot = offer.DestRoot,
                DestRelativePath = offer.DestRelativePath,
                FileName = offer.FileName,
                CreatedUtc = DateTimeOffset.UtcNow,
            });
        }

        var mode = startOffset == 0 ? FileMode.Create : FileMode.Open;
        var stream = new FileStream(partialPath, mode, FileAccess.Write, FileShare.None, 65536, useAsync: true);
        if (startOffset > 0)
            stream.Seek(startOffset, SeekOrigin.Begin);

        var session = new ReceiveSession
        {
            TransferId = offer.TransferId,
            ClientId = clientId,
            ExpectedSize = offer.Size,
            DestRoot = offer.DestRoot,
            HostRelativePath = hostRelativePath,
            PartialPath = partialPath,
            ManifestPath = manifestPath,
            PartialStream = stream,
            Hasher = hasher,
            BytesReceived = startOffset,
            LastAckedOffset = startOffset,
        };

        // Any prior live session for this id was already superseded at the top of this method (before we
        // opened the partial), so this is a clean insert.
        _receiveSessions[offer.TransferId] = session;

        _logger.LogInformation(
            "Accepted inbound transfer {TransferId} from {ClientId} → root '{Root}' ('{Rel}'), startOffset={Offset}.",
            offer.TransferId, clientId, offer.DestRoot, hostRelativePath, startOffset);

        return new ReceiveAcceptance(true, startOffset, null);
    }

    /// <summary>
    /// Appends a data-frame payload at <paramref name="offset"/> (which must equal the current partial length),
    /// advancing the running hash. Returns the new committed byte offset. Throws on a gap/overshoot so the
    /// caller can surface a protocol error and abort.
    /// </summary>
    public async Task<long> WriteChunkAsync(string transferId, long offset, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (!_receiveSessions.TryGetValue(transferId, out var session))
            throw new InvalidOperationException($"No active inbound transfer '{transferId}'.");

        await session.WriteLock.WaitAsync(ct);
        try
        {
            if (session.Completed)
                throw new InvalidOperationException($"Transfer '{transferId}' is already finalized.");
            if (offset != session.BytesReceived)
                throw new InvalidOperationException(
                    $"Out-of-order frame for '{transferId}': expected offset {session.BytesReceived}, got {offset}.");
            if (session.ExpectedSize > 0 && session.BytesReceived + payload.Length > session.ExpectedSize)
                throw new InvalidOperationException($"Transfer '{transferId}' overshot its declared size.");
            if (session.BytesReceived + payload.Length > MaxTransferBytes)
                throw new InvalidOperationException($"Transfer '{transferId}' exceeded the {MaxTransferBytes}-byte cap.");

            await session.PartialStream.WriteAsync(payload, ct);
            session.Hasher.AppendData(payload.Span);
            session.BytesReceived += payload.Length;
            return session.BytesReceived;
        }
        finally
        {
            session.WriteLock.Release();
        }
    }

    /// <summary>
    /// Finalizes an inbound transfer: compares the full-file hash to <paramref name="expectedSha256Base64"/>,
    /// and on a match copies the verified partial into the destination shared root before clearing staging.
    /// A mismatch (or an incomplete transfer, or a destination write failure) deletes the partial and returns
    /// <c>verified:false</c>.
    /// </summary>
    public async Task<FileTransferResult> CompleteReceiveAsync(string transferId, string? expectedSha256Base64, CancellationToken ct)
    {
        if (!_receiveSessions.TryRemove(transferId, out var session))
            return new FileTransferResult { TransferId = transferId, Verified = false, Error = "Unknown transfer." };

        await session.WriteLock.WaitAsync(ct);
        try
        {
            session.Completed = true;

            await session.PartialStream.FlushAsync(ct);
            await session.PartialStream.DisposeAsync();

            var actualBase64 = Convert.ToBase64String(session.Hasher.GetHashAndReset());
            session.Hasher.Dispose();

            var complete = session.ExpectedSize <= 0 || session.BytesReceived == session.ExpectedSize;
            var hashMatches = string.IsNullOrEmpty(expectedSha256Base64)
                || string.Equals(actualBase64, expectedSha256Base64, StringComparison.Ordinal);

            if (!complete || !hashMatches)
            {
                DeleteStaging(transferId);
                var reason = !complete ? "Transfer incomplete." : "SHA-256 mismatch — file corrupted in transit.";
                _logger.LogWarning("Inbound transfer {TransferId} failed verification: {Reason}", transferId, reason);
                return new FileTransferResult
                {
                    TransferId = transferId,
                    Verified = false,
                    Sha256Base64 = actualBase64,
                    Error = reason,
                };
            }

            // Verified: promote the partial into the destination shared root. OpenForWriteAsync enforces the
            // root's writability, the size cap, root-escape safety, and creates parent directories.
            try
            {
                await using (var src = new FileStream(session.PartialPath, FileMode.Open, FileAccess.Read, FileShare.None, 65536, useAsync: true))
                await using (var dst = await _fileTransferService.OpenForWriteAsync(session.DestRoot, session.HostRelativePath, session.ExpectedSize, ct))
                {
                    await src.CopyToAsync(dst, 65536, ct);
                    await dst.FlushAsync(ct);
                }
            }
            catch (Exception ex)
            {
                DeleteStaging(transferId);
                _logger.LogWarning(ex, "Inbound transfer {TransferId} verified but could not be saved to the destination.", transferId);
                return new FileTransferResult
                {
                    TransferId = transferId,
                    Verified = false,
                    Sha256Base64 = actualBase64,
                    Error = $"Verified but could not be saved: {ex.Message}",
                };
            }

            DeleteStaging(transferId);
            _logger.LogInformation("Inbound transfer {TransferId} verified and saved ({Bytes} bytes).", transferId, session.BytesReceived);
            return new FileTransferResult
            {
                TransferId = transferId,
                Verified = true,
                Sha256Base64 = actualBase64,
            };
        }
        finally
        {
            session.WriteLock.Release();
        }
    }

    /// <summary>Cancels an inbound transfer, deleting its partial + manifest (plan: cancel deletes <c>.remexpart</c>).</summary>
    public void CancelReceive(string transferId)
    {
        if (_receiveSessions.TryRemove(transferId, out var session))
            session.DisposeStreamOnly();
        DeleteStaging(transferId);
    }

    /// <summary>
    /// Drops the live session for a dropped socket <b>without</b> deleting staging, so the transfer can resume
    /// later. This is the disconnect path (as opposed to an explicit cancel).
    /// </summary>
    private void SuspendReceive(string transferId)
    {
        if (_receiveSessions.TryRemove(transferId, out var session))
            session.DisposeStreamOnly();
    }

    /// <summary>Sweeps orphaned staging files older than <paramref name="maxAge"/>. Internal for tests.</summary>
    internal void CleanupOrphans(TimeSpan maxAge)
    {
        if (!Directory.Exists(_stagingDir))
            return;

        var cutoff = DateTime.UtcNow - maxAge;
        foreach (var file in Directory.EnumerateFiles(_stagingDir))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                    _logger.LogInformation("Swept orphaned transfer staging file {File}.", Path.GetFileName(file));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Could not sweep staging file {File}.", file);
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // /ws control-plane routing (called by PingPongHandler for the v3 negotiation msgs).
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Handles an inbound <c>file_transfer_offer</c> and replies with <c>file_transfer_ready</c> on <paramref name="controlWs"/>.</summary>
    public async Task HandleOfferAsync(string? clientId, FileTransferOffer offer, WebSocket controlWs, CancellationToken ct)
    {
        if (offer is null)
            return;

        var resolvedClientId = clientId ?? string.Empty;

        try
        {
            if (offer.Mode == FileTransferModes.Download)
            {
                await BeginSendAsync(resolvedClientId, offer, controlWs, ct);
                return;
            }

            // upload | push → host receives.
            var acceptance = await BeginReceiveAsync(resolvedClientId, offer, ct);
            await SendReadyAsync(controlWs, offer.TransferId, acceptance.Accepted, acceptance.StartOffset, acceptance.DeclineReason, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to handle file_transfer_offer {TransferId}.", offer.TransferId);
            await SendReadyAsync(controlWs, offer.TransferId, accepted: false, startOffset: 0, declineReason: ex.Message, ct);
        }
    }

    /// <summary>Handles an inbound <c>file_transfer_complete</c> and replies with <c>file_transfer_result</c>.
    /// <paramref name="isLoopback"/> distinguishes a real remote device from the PC UI's own self-connection,
    /// so only genuine phone pushes are surfaced in the Home activity feed.</summary>
    public async Task HandleCompleteAsync(FileTransferComplete complete, WebSocket controlWs, bool isLoopback, CancellationToken ct)
    {
        if (complete is null)
            return;

        // Capture the destination file name now — CompleteReceiveAsync removes the session below.
        string? receivedName = _receiveSessions.TryGetValue(complete.TransferId, out var pendingSession)
            ? System.IO.Path.GetFileName(pendingSession.HostRelativePath)
            : null;

        var result = await CompleteReceiveAsync(complete.TransferId, complete.Sha256Base64, ct);
        await MessageSerializer.SendAsync(controlWs, new RemexMessage
        {
            Type = MessageTypes.FileTransferResult,
            ProtocolVersion = ProtocolVersionPolicy.Current,
            FileTransferResult = result,
        }, ct);

        // Feed the Home "Recent activity" panel when a real remote device pushes a file onto this PC.
        // Skip loopback: a PC-user upload over the self-connection is already recorded desktop-side, so
        // recording it here too would double-count the same file.
        if (!isLoopback && result.Verified && !string.IsNullOrEmpty(receivedName))
        {
            Remex.Desktop.Services.ActivityService.Instance.Record(
                Remex.Desktop.Services.ActivityKind.FileReceived, receivedName);
        }
    }

    /// <summary>Handles a <c>file_transfer_control</c> action (pause / resume / cancel).</summary>
    public void HandleControl(FileTransferControl control)
    {
        if (control is null)
            return;

        switch (control.Action)
        {
            case FileTransferControlActions.Cancel:
                CancelReceive(control.TransferId);
                if (_sendSessions.TryRemove(control.TransferId, out var send))
                    send.Cancel();
                _logger.LogInformation("Transfer {TransferId} cancelled by peer.", control.TransferId);
                break;

            case FileTransferControlActions.Pause:
                // Pause is advisory for the host sender: cancel the outbound stream but keep the peer's
                // partial for a later resume. Inbound pause is a no-op (the sender simply stops framing).
                if (_sendSessions.TryRemove(control.TransferId, out var paused))
                    paused.Cancel();
                _logger.LogInformation("Transfer {TransferId} paused by peer.", control.TransferId);
                break;

            case FileTransferControlActions.Resume:
                // Resume is driven by a fresh offer (with resumeRequested); nothing to do on the live session.
                break;
        }
    }

    /// <summary>Handles an inbound <c>file_transfer_result</c> — the peer's verification of a host-sent download.</summary>
    public void HandleResult(FileTransferResult result)
    {
        if (result is null)
            return;

        if (_sendSessions.TryRemove(result.TransferId, out var send))
            send.Cancel();

        if (result.Verified)
            _logger.LogInformation("Peer verified host-sent transfer {TransferId}.", result.TransferId);
        else
            _logger.LogWarning("Peer reported host-sent transfer {TransferId} failed: {Error}", result.TransferId, result.Error);
    }

    /// <summary>Handles an unexpected inbound <c>file_transfer_ready</c> (the host never initiates offers).</summary>
    public void HandleReady(FileTransferReady ready)
    {
        if (ready is not null)
            _logger.LogWarning("Received unexpected file_transfer_ready for {TransferId}; the host does not initiate offers.", ready.TransferId);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // /ws/files binary data-plane channel.
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the bidirectional binary channel for a paired client until the socket closes. Inbound
    /// <c>data</c> frames are written to the matching receive session (with periodic acks); inbound
    /// <c>ack</c> frames advance the matching send session's backpressure window. On exit, live receive
    /// sessions for this client are suspended (partial kept for resume) and send sessions cancelled.
    /// </summary>
    public async Task RunChannelAsync(string clientId, WebSocket ws, CancellationToken ct)
    {
        Guard.NotNull(ws);
        var key = clientId ?? string.Empty;
        var channel = new FileChannel(ws);

        // Replace any stale channel for this client (a reconnect before the old socket's loop exited).
        if (_channels.TryGetValue(key, out var existing))
            existing.MarkSuperseded();
        _channels[key] = channel;

        _logger.LogInformation("DIAG /ws/files channel loop STARTED for {ClientId} (wsState={State}).", Remex.Agent.Services.Security.LogRedaction.RedactClientId(key), ws.State);
        var diagDataFrames = 0;
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var frameBytes = await ReceiveBinaryAsync(ws, ct);
                if (frameBytes is null)
                {
                    _logger.LogInformation("DIAG /ws/files recv returned null for {ClientId} after {N} data frame(s) (wsState={State}) — loop exiting.", Remex.Agent.Services.Security.LogRedaction.RedactClientId(key), diagDataFrames, ws.State);
                    break; // socket closed or an oversize/protocol-invalid frame.
                }

                if (!FileFrameCodec.TryRead(frameBytes, out var envelope, out var payload) || envelope is null)
                {
                    _logger.LogWarning("Discarding malformed /ws/files frame ({Len} bytes) from {ClientId}.", frameBytes.Length, Remex.Agent.Services.Security.LogRedaction.RedactClientId(key));
                    continue;
                }

                switch (envelope.Kind)
                {
                    case FileFrameKinds.Data:
                        if (diagDataFrames++ == 0)
                            _logger.LogInformation("DIAG first DATA frame on /ws/files: transfer={Id}, offset={Off}, len={Len}.", envelope.TransferId, envelope.Offset, payload.Length);
                        await HandleInboundDataFrameAsync(channel, envelope, payload.ToArray(), ct);
                        break;

                    case FileFrameKinds.Ack:
                        if (envelope.CommittedOffset is { } committed
                            && _sendSessions.TryGetValue(envelope.TransferId, out var sendSession))
                        {
                            sendSession.OnAck(committed);
                        }
                        break;

                    case FileFrameKinds.Error:
                        _logger.LogWarning("Peer reported error on transfer {TransferId}: {Error}", envelope.TransferId, envelope.Error);
                        CancelReceive(envelope.TransferId);
                        if (_sendSessions.TryRemove(envelope.TransferId, out var erroredSend))
                            erroredSend.Cancel();
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown / socket abort.
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "/ws/files channel error for {ClientId}.", Remex.Agent.Services.Security.LogRedaction.RedactClientId(key));
        }
        finally
        {
            // Only tear down if we are still the registered channel for this client.
            if (_channels.TryGetValue(key, out var current) && ReferenceEquals(current, channel))
                _channels.TryRemove(key, out _);

            // Suspend (keep partials for resume) every receive session belonging to this client; cancel its
            // outbound sends.
            foreach (var kvp in _receiveSessions)
            {
                if (string.Equals(kvp.Value.ClientId, key, StringComparison.Ordinal))
                    SuspendReceive(kvp.Key);
            }
            foreach (var kvp in _sendSessions)
            {
                if (string.Equals(kvp.Value.ClientId, key, StringComparison.Ordinal) && _sendSessions.TryRemove(kvp.Key, out var s))
                    s.Cancel();
            }
        }
    }

    private async Task HandleInboundDataFrameAsync(FileChannel channel, FileFrameEnvelope envelope, byte[] payload, CancellationToken ct)
    {
        try
        {
            var committed = await WriteChunkAsync(envelope.TransferId, envelope.Offset, payload, ct);

            var shouldAck = envelope.Final
                || committed - GetLastAcked(envelope.TransferId) >= FileTransferLimits.AckIntervalBytes;

            if (shouldAck)
            {
                SetLastAcked(envelope.TransferId, committed);
                await channel.SendFrameAsync(new FileFrameEnvelope
                {
                    Kind = FileFrameKinds.Ack,
                    TransferId = envelope.TransferId,
                    CommittedOffset = committed,
                }, ReadOnlyMemory<byte>.Empty, ct);
            }
        }
        catch (InvalidOperationException ex)
        {
            // Protocol violation (gap / overshoot / unknown transfer): tell the peer and drop the partial.
            _logger.LogWarning(ex, "Aborting inbound transfer {TransferId} on a bad data frame.", envelope.TransferId);
            CancelReceive(envelope.TransferId);
            try
            {
                await channel.SendFrameAsync(new FileFrameEnvelope
                {
                    Kind = FileFrameKinds.Error,
                    TransferId = envelope.TransferId,
                    Error = ex.Message,
                }, ReadOnlyMemory<byte>.Empty, ct);
            }
            catch (Exception sendEx)
            {
                _logger.LogDebug(sendEx, "Could not send error frame for {TransferId}.", envelope.TransferId);
            }
        }
    }

    private long GetLastAcked(string transferId)
        => _receiveSessions.TryGetValue(transferId, out var s) ? Interlocked.Read(ref s.LastAckedOffset) : 0;

    private void SetLastAcked(string transferId, long value)
    {
        if (_receiveSessions.TryGetValue(transferId, out var s))
            Interlocked.Exchange(ref s.LastAckedOffset, value);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Host sender (download): stream a host file out with ack-driven backpressure.
    // ─────────────────────────────────────────────────────────────────────────────

    private async Task BeginSendAsync(string clientId, FileTransferOffer offer, WebSocket controlWs, CancellationToken ct)
    {
        string? nameError = null;
        if (string.IsNullOrWhiteSpace(offer.DestRoot)
            || !Remex.Core.Validation.FilePathValidation.IsValidFileName(offer.FileName, out nameError))
        {
            await SendReadyAsync(controlWs, offer.TransferId, false, 0, nameError ?? "A source shared root is required.", ct);
            return;
        }

        if (!_channels.TryGetValue(clientId, out var channel))
        {
            await SendReadyAsync(controlWs, offer.TransferId, false, 0, "The binary file channel is not connected.", ct);
            return;
        }

        // DOWNLOAD path fix (RemEx-ix8d): the peer sends DestRelativePath as the source file's FULL
        // relative path within the root (it already includes the file name) — unlike an UPLOAD, where
        // DestRelativePath is the destination *directory*. So use it directly here; the receive path's
        // CombineHostRelative(DestRelativePath, FileName) would double the name into 'file/file' and fail
        // every download with "File not found in shared root".
        var hostRelativePath = string.IsNullOrWhiteSpace(offer.DestRelativePath)
            ? offer.FileName
            : offer.DestRelativePath;
        Stream source;
        try
        {
            source = await _fileTransferService.OpenForReadAsync(offer.DestRoot, hostRelativePath, ct);
        }
        catch (Exception ex)
        {
            await SendReadyAsync(controlWs, offer.TransferId, false, 0, ex.Message, ct);
            return;
        }

        var size = source.CanSeek ? source.Length : offer.Size;

        // Host-side download resume is not applicable (the receiver-side partial lives on the peer), so we
        // always stream from 0. See the WP4 notes: the client re-requests to resume a download.
        await SendReadyAsync(controlWs, offer.TransferId, accepted: true, startOffset: 0, declineReason: null, ct);

        var sendSession = new SendSession(offer.TransferId, clientId);
        _sendSessions[offer.TransferId] = sendSession;

        // Detach the streaming loop; it outlives the synchronous handling of this offer message.
        _ = Task.Run(() => StreamSenderAsync(channel, sendSession, source, size, controlWs, ct), ct);
    }

    private async Task StreamSenderAsync(FileChannel channel, SendSession session, Stream source, long size, WebSocket controlWs, CancellationToken outerCt)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(outerCt, session.CancellationToken);
        var ct = linked.Token;
        var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(FileTransferLimits.DataPayloadBytes);
        long sentOffset = 0;

        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, FileTransferLimits.DataPayloadBytes), ct)) > 0)
            {
                // Backpressure: never let outstanding unacked bytes exceed the cap.
                while (sentOffset - session.CommittedOffset > FileTransferLimits.MaxUnackedBytes)
                    await session.WaitForAckAsync(ct);

                var isFinal = sentOffset + read >= size;
                await channel.SendFrameAsync(new FileFrameEnvelope
                {
                    Kind = FileFrameKinds.Data,
                    TransferId = session.TransferId,
                    Offset = sentOffset,
                    Length = read,
                    Final = isFinal,
                }, buffer.AsMemory(0, read), ct);

                hasher.AppendData(buffer, 0, read);
                sentOffset += read;
            }

            var sha256 = Convert.ToBase64String(hasher.GetHashAndReset());
            await MessageSerializer.SendAsync(controlWs, new RemexMessage
            {
                Type = MessageTypes.FileTransferComplete,
                ProtocolVersion = ProtocolVersionPolicy.Current,
                FileTransferComplete = new FileTransferComplete { TransferId = session.TransferId, Sha256Base64 = sha256 },
            }, ct);

            _logger.LogInformation("Host-sent transfer {TransferId} streamed {Bytes} bytes, sha256={Sha}.", session.TransferId, sentOffset, sha256);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Host-sent transfer {TransferId} cancelled.", session.TransferId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Host-sent transfer {TransferId} failed.", session.TransferId);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            hasher.Dispose();
            await source.DisposeAsync();
            _sendSessions.TryRemove(session.TransferId, out _);
        }
    }

    private async Task SendReadyAsync(WebSocket controlWs, string transferId, bool accepted, long startOffset, string? declineReason, CancellationToken ct)
    {
        // DIAG (RemEx-ix8d): confirm the ready reply is actually written and time the send — a slow send
        // would fingerprint a per-socket send-gate stall; a decline reason fingerprints the offer path.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await MessageSerializer.SendAsync(controlWs, new RemexMessage
        {
            Type = MessageTypes.FileTransferReady,
            ProtocolVersion = ProtocolVersionPolicy.Current,
            FileTransferReady = new FileTransferReady
            {
                TransferId = transferId,
                Accepted = accepted,
                StartOffset = startOffset,
                DeclineReason = declineReason,
            },
        }, ct);
        _logger.LogInformation(
            "DIAG file_transfer_ready sent for {TransferId}: accepted={Accepted}, decline='{Decline}', sendMs={Ms}.",
            transferId, accepted, declineReason ?? "-", sw.ElapsedMilliseconds);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Helpers.
    // ─────────────────────────────────────────────────────────────────────────────

    private static async Task<byte[]?> ReceiveBinaryAsync(WebSocket ws, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    return null;
                if (ms.Length + result.Count > MaxFrameBytes)
                    return null; // Oversize frame: close the channel rather than buffer unbounded data.
                ms.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        return ms.ToArray();
    }

    private static async Task ReHashPartialAsync(string partialPath, IncrementalHash hasher, CancellationToken ct)
    {
        await using var stream = new FileStream(partialPath, FileMode.Open, FileAccess.Read, FileShare.None, 65536, useAsync: true);
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                hasher.AppendData(buffer, 0, read);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private string PartialPathFor(string transferId) => Path.Combine(_stagingDir, SafeStem(transferId) + ".remexpart");
    private string ManifestPathFor(string transferId) => Path.Combine(_stagingDir, SafeStem(transferId) + ".manifest.json");

    /// <summary>Sanitizes a transfer id into a safe filename stem (ids are GUIDs, but never trust the wire).</summary>
    private static string SafeStem(string transferId)
    {
        Span<char> buffer = stackalloc char[transferId.Length];
        for (var i = 0; i < transferId.Length; i++)
        {
            var c = transferId[i];
            buffer[i] = char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_';
        }
        return new string(buffer);
    }

    private static string CombineHostRelative(string? directory, string fileName)
    {
        var dir = (directory ?? string.Empty).Replace('\\', '/').Trim('/');
        return string.IsNullOrEmpty(dir) ? fileName : dir + "/" + fileName;
    }

    private void DeleteStaging(string transferId)
    {
        TryDelete(PartialPathFor(transferId));
        TryDelete(ManifestPathFor(transferId));
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not delete staging file {Path}.", path);
        }
    }

    private void WriteManifest(string manifestPath, TransferManifest manifest)
    {
        try
        {
            var json = JsonSerializer.Serialize(manifest, ManifestJsonOptions);
            File.WriteAllText(manifestPath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not write transfer manifest {Path}.", manifestPath);
        }
    }

    private bool TryLoadManifest(string manifestPath, out TransferManifest? manifest)
    {
        manifest = null;
        try
        {
            if (!File.Exists(manifestPath))
                return false;
            manifest = JsonSerializer.Deserialize<TransferManifest>(File.ReadAllText(manifestPath), ManifestJsonOptions);
            return manifest is not null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _logger.LogDebug(ex, "Could not read transfer manifest {Path}.", manifestPath);
            return false;
        }
    }

    public void Dispose()
    {
        foreach (var kvp in _receiveSessions)
            kvp.Value.DisposeStreamOnly();
        _receiveSessions.Clear();
        foreach (var kvp in _sendSessions)
            kvp.Value.Cancel();
        _sendSessions.Clear();
    }

    // ── Nested state types ──────────────────────────────────────────────────────

    private sealed class ReceiveSession
    {
        public required string TransferId { get; init; }
        public required string ClientId { get; init; }
        public required long ExpectedSize { get; init; }
        public required string DestRoot { get; init; }
        public required string HostRelativePath { get; init; }
        public required string PartialPath { get; init; }
        public required string ManifestPath { get; init; }
        public required FileStream PartialStream { get; init; }
        public required IncrementalHash Hasher { get; init; }
        public long BytesReceived { get; set; }
        public long LastAckedOffset; // accessed via Interlocked from the channel loop.
        public bool Completed { get; set; }
        public SemaphoreSlim WriteLock { get; } = new(1, 1);

        public void DisposeStreamOnly()
        {
            try { PartialStream.Dispose(); } catch { /* best-effort */ }
            Hasher.Dispose();
        }
    }

    private sealed class SendSession
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly SemaphoreSlim _ackSignal = new(0, int.MaxValue);
        private long _committedOffset;

        public SendSession(string transferId, string clientId)
        {
            TransferId = transferId;
            ClientId = clientId;
        }

        public string TransferId { get; }
        public string ClientId { get; }
        public long CommittedOffset => Interlocked.Read(ref _committedOffset);
        public CancellationToken CancellationToken => _cts.Token;

        public void OnAck(long committedOffset)
        {
            Interlocked.Exchange(ref _committedOffset, committedOffset);
            try { _ackSignal.Release(); } catch (SemaphoreFullException) { /* saturated: sender will re-check */ }
        }

        public Task WaitForAckAsync(CancellationToken ct) => _ackSignal.WaitAsync(ct);

        public void Cancel()
        {
            try { _cts.Cancel(); } catch (ObjectDisposedException) { /* the token source was already disposed by a concurrent teardown, which means the cancel it would have carried has already happened */ }
        }
    }

    private sealed class FileChannel
    {
        private readonly WebSocket _ws;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private volatile bool _superseded;

        public FileChannel(WebSocket ws) => _ws = ws;

        public void MarkSuperseded() => _superseded = true;

        public async Task SendFrameAsync(FileFrameEnvelope envelope, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            if (_superseded)
                return;

            var frame = FileFrameCodec.Wrap(envelope, payload.Span);
            await _sendLock.WaitAsync(ct);
            try
            {
                await _ws.SendAsync(new ArraySegment<byte>(frame), WebSocketMessageType.Binary, endOfMessage: true, ct);
            }
            finally
            {
                _sendLock.Release();
            }
        }
    }

    /// <summary>Sidecar manifest persisted beside each <c>.remexpart</c> for resume identity (plan §1.3).</summary>
    private sealed record TransferManifest
    {
        public required string TransferId { get; init; }
        public required long ExpectedSize { get; init; }
        public required string ClientId { get; init; }
        public string? SourcePath { get; init; }
        public required string DestRoot { get; init; }
        public string? DestRelativePath { get; init; }
        public required string FileName { get; init; }
        public DateTimeOffset CreatedUtc { get; init; }
    }
}
