using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
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
/// promoted into the destination shared root via <see cref="IFileTransferService.PromoteStagedFileAsync"/>
/// — a rename when the two share a volume — and the
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
    private const long DefaultMaxTransferBytes = 5_000_000_000L;

    /// <summary>
    /// Test-only seam (visible to <c>Remex.Agent.Tests</c> via <c>InternalsVisibleTo</c>). The running
    /// transfer cap, overridable so a test can reach the branch without streaming 5 GB. Not used by DI —
    /// the default container only binds constructors, so an <c>init</c> property is invisible to host
    /// bootstrapping and production always gets <see cref="DefaultMaxTransferBytes"/>.
    /// </summary>
    /// <remarks>
    /// WHY THIS IS THE CAP THAT MATTERS, and not a duplicate of the offer-time check (RemEx-9xs1).
    /// <see cref="BeginReceiveAsync"/> refuses a declared size outside the range, but a declared size of
    /// ZERO is legitimate — a phone reports it for a content URI whose length it cannot read — and zero
    /// disables the declared-size bound below it (<c>ExpectedSize &gt; 0 &amp;&amp; …</c>) as well as the
    /// completion check in <c>CompleteReceiveAsync</c>. So for a zero-size offer this line is the ONLY
    /// thing standing between a peer and an unbounded write into the staging directory. It had no
    /// coverage while it was a <c>const</c>, which is exactly how a guard like this gets deleted by a
    /// tidy-up: nothing goes red.
    /// </remarks>
    internal long MaxTransferBytes { get; init; } = DefaultMaxTransferBytes;

    /// <summary>
    /// How long <see cref="PushFileAsync"/> waits for the phone's <c>file_transfer_ready</c>
    /// (RemEx-gfx3n).
    /// </summary>
    /// <remarks>
    /// <para>
    /// SHORTER THAN THE OFFER TIMEOUT ON PURPOSE, and the two are not interchangeable. The 70 seconds
    /// in <see cref="FilePushOriginator"/> covers a HUMAN reading a consent prompt; this one starts
    /// after that consent already arrived, so it is only waiting for the phone to open a channel and
    /// say it is ready. Thirty seconds is generous for a round trip and short enough that a phone
    /// which consented and then died does not hold the file handle for over a minute.
    /// </para>
    /// <para>
    /// The same test-only seam as <see cref="MaxTransferBytes"/> above, for the same reason: the
    /// default container binds constructors only, so this is invisible to DI and production always
    /// gets the thirty seconds. Its purpose is that a test can reach the no-ready timeout branch —
    /// which must return false AND release the file handle — without spending half a minute per
    /// assertion.
    /// </para>
    /// </remarks>
    internal TimeSpan ReadyTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long <see cref="WaitForFinalAckAsync"/> will wait for one further ack before giving a
    /// host-sent transfer up (RemEx-zd8ws).
    /// </summary>
    /// <remarks>
    /// <para>
    /// AN IDLE WINDOW, NOT A DEADLINE, and the distinction is the whole design. Up to
    /// <see cref="FileTransferLimits.MaxUnackedBytes"/> (8 MB) can still be in flight when the send loop
    /// ends, so a total budget would have to be sized for 8 MB draining over the slowest link a user
    /// might have — and any number large enough to be safe there is far too large to be a useful
    /// backstop. Every ack instead refreshes the window: a transfer that is still moving is never killed
    /// no matter how slow it is, and a peer that has genuinely stopped talking is given up on after one
    /// quiet minute.
    /// </para>
    /// <para>
    /// The same test-only seam as <see cref="ReadyTimeout"/> above, for the same reason: a test must be
    /// able to reach the gave-up branch without spending a minute per assertion.
    /// </para>
    /// </remarks>
    internal TimeSpan AckDrainIdleTimeout { get; init; } = TimeSpan.FromSeconds(60);

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
    private readonly SharedRootReadResolver _readResolver;
    private readonly string _stagingDir;

    private readonly ConcurrentDictionary<string, ReceiveSession> _receiveSessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SendSession> _sendSessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, FileChannel> _channels = new(StringComparer.Ordinal);

    public TransferSessionManager(
        ILogger<TransferSessionManager> logger,
        IFileTransferService fileTransferService,
        SharedRootReadResolver readResolver)
        : this(logger, fileTransferService, readResolver, stagingDir: null)
    {
    }

    /// <summary>Test seam: overrides the staging directory so resume/partial behaviour is exercised hermetically.</summary>
    internal TransferSessionManager(
        ILogger<TransferSessionManager> logger,
        IFileTransferService fileTransferService,
        SharedRootReadResolver readResolver,
        string? stagingDir)
    {
        _logger = Guard.NotNull(logger);
        _fileTransferService = Guard.NotNull(fileTransferService);
        _readResolver = Guard.NotNull(readResolver);

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
        // ...but only for the client that owns it (RemEx-juas). Superseding took whatever id the
        // offer named, so a caller could re-own another client's live transfer simply by offering
        // the same id: the victim's stream handle was disposed and the id re-keyed to the attacker's
        // destination. RemEx-0719 then stops the victim's own data frames from landing, because the
        // session no longer belongs to it - so without this check that fix turned a hijack into a
        // reliable way to break the transfer instead.
        //
        // Refused rather than superseded, and the offer is declined: a re-offer from a different
        // client is not a retry, and there is no reading of it where continuing is right.
        if (_receiveSessions.TryGetValue(offer.TransferId, out var live)
            && !string.Equals(live.ClientId, clientId, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Refusing a file_transfer_offer from {ClientId}: transfer {TransferId} is already live for a different client.",
                Remex.Agent.Services.Security.LogRedaction.RedactClientId(clientId),
                offer.TransferId);
            return new ReceiveAcceptance(false, 0, "That transfer id is in use by another client.");
        }

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
        // SEQUENTIALSCAN ALONGSIDE ASYNCHRONOUS, NOT INSTEAD OF IT. A transfer walks the file once
        // start to end and never seeks back. The documented effect of the hint is read-ahead, which
        // this WRITE handle gets nothing from - what it gets is the other half, the cache manager not
        // retaining pages behind the cursor, so receiving a large file does not evict what else the
        // machine had cached. Stated narrowly because the read sites get both and this one does not.
        // Asynchronous is what the useAsync overload was setting and is still required: dropping it
        // turns every await here into a blocking call on a thread-pool thread (RemEx-ygapg).
        var stream = new FileStream(
            partialPath, mode, FileAccess.Write, FileShare.None, 65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
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
    /// and on a match promotes the verified partial into the destination shared root before clearing
    /// staging. Promotion is a rename on the same volume, so the partial is normally already gone by the
    /// time staging is cleared; only a cross-volume destination copies.
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

            // Verified: promote the partial into the destination shared root. PromoteStagedFileAsync
            // enforces the root's writability, the size cap, root-escape safety, and creates parent
            // directories — the same checks OpenForWriteAsync applies, because both go through the one
            // ResolveForWrite. On the same volume this is a rename rather than a re-read and rewrite
            // (RemEx-fq6f).
            try
            {
                await _fileTransferService.PromoteStagedFileAsync(
                    session.DestRoot, session.HostRelativePath, session.ExpectedSize, session.PartialPath, ct);
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
    public async Task HandleCompleteAsync(
        FileTransferComplete complete, WebSocket controlWs, bool isLoopback, string channelKey, CancellationToken ct)
    {
        if (complete is null)
            return;

        // Same ownership rule as HandleControl (RemEx-juas). Completing someone else's transfer runs
        // the hash verification and the promotion out of staging into the destination root - so an
        // unbound caller could force a half-received file to be promoted, or fail its verification
        // and have the partial deleted, on a transfer it has nothing to do with.
        if (IsForeignTransfer(channelKey, complete.TransferId))
        {
            _logger.LogWarning(
                "Refusing a file_transfer_complete from {ClientId}: transfer {TransferId} belongs to a different client.",
                Remex.Agent.Services.Security.LogRedaction.RedactClientId(channelKey),
                complete.TransferId);
            return;
        }

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

            // The activity feed is the RECORD; this is the ANNOUNCEMENT. A received file used to
            // produce nothing visible at all, and close-to-tray is on by default, so the window it
            // would have appeared in is usually not on screen when the file lands (RemEx-5wc2).
            var localization = Remex.Desktop.Services.LocalizationService.Instance;
            Remex.Desktop.Services.NotificationService.Instance.Notify(
                Remex.Desktop.Services.NotificationImportance.Outcome,
                localization["Notification_FileReceived_Title"],
                string.Format(localization["Notification_FileReceived_Message"], receivedName));
        }
    }

    /// <summary>Handles a <c>file_transfer_control</c> action (pause / resume / cancel).</summary>
    /// <param name="channelKey">
    /// The identity of the connection that sent this. Empty for a connection that has proved none.
    /// A loopback /ws connection is one such - RemEx-4215 freezes it at no identity - but it is NOT
    /// the only one: a remote device that paired with its clientId field omitted also settles at null
    /// and shares the same blank key. Both are covered here for the same reason, and neither owns any
    /// paired client's transfer.
    /// </param>
    public void HandleControl(FileTransferControl control, string channelKey)
    {
        if (control is null)
            return;

        // OWNERSHIP (RemEx-juas). RemEx-0719 bound the BINARY channel so a frame could not act on a
        // transfer belonging to another client. This is the same capability on the JSON control
        // plane, and it was the cheaper attack of the two: HandleControl took no identity at all, and
        // PingPongHandler seeds `isPaired = isLoopback`, so an unelevated local process could open
        // ws://127.0.0.1/ws and cancel a phone's in-flight transfer with ONE message - no pairing, no
        // PIN, no binary channel, nothing claimed.
        //
        // IsForeignTransfer is the rule RemEx-0719 established, reused rather than re-derived. It
        // also covers the case that made that bead's fix incomplete on the first pass: a transfer
        // suspended by a dropped socket has no live session but keeps its resumable partial, and its
        // owner is recorded in the staging manifest.
        if (IsForeignTransfer(channelKey, control.TransferId))
        {
            _logger.LogWarning(
                "Refusing a file_transfer_control {Action} from {ClientId}: transfer {TransferId} belongs to a different client.",
                control.Action,
                Remex.Agent.Services.Security.LogRedaction.RedactClientId(channelKey),
                control.TransferId);
            return;
        }

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
    /// <param name="channelKey">The identity of the connection that sent this; see <see cref="HandleControl"/>.</param>
    public void HandleResult(FileTransferResult result, string channelKey)
    {
        if (result is null)
            return;

        // Same ownership rule as HandleControl (RemEx-juas). This one cancels a send session, so an
        // unbound caller could end another client's download by declaring it verified - or failed.
        if (IsForeignTransfer(channelKey, result.TransferId))
        {
            _logger.LogWarning(
                "Refusing a file_transfer_result from {ClientId}: transfer {TransferId} belongs to a different client.",
                Remex.Agent.Services.Security.LogRedaction.RedactClientId(channelKey),
                result.TransferId);
            return;
        }

        if (_sendSessions.TryRemove(result.TransferId, out var send))
            send.Cancel();

        if (result.Verified)
            _logger.LogInformation("Peer verified host-sent transfer {TransferId}.", result.TransferId);
        else
            _logger.LogWarning("Peer reported host-sent transfer {TransferId} failed: {Error}", result.TransferId, result.Error);
    }

    /// <summary>
    /// Pending <c>file_transfer_ready</c> replies, keyed by the transfer id the host offered.
    /// </summary>
    /// <remarks>
    /// The host DOES initiate offers now (RemEx-y7my) - it pushes screenshots - so a ready is no
    /// longer unexpected. It remains unexpected for any id nobody is waiting on, which is what an
    /// out-of-band or late reply looks like.
    /// </remarks>
    /// <summary>A push awaiting its peer's <c>file_transfer_ready</c>, and WHO it is waiting on.</summary>
    /// <remarks>
    /// THE OWNER IS THE POINT (RemEx-5dq3). This map used to hold a bare
    /// <see cref="TaskCompletionSource{T}"/>, so the ready that satisfied a pending push was never
    /// checked against anyone — <see cref="PushFileAsync"/> had the client id in hand at the moment it
    /// registered and simply discarded it. RemEx-juas bound the four other control-plane entry points
    /// that key on a transfer id and left this one, on the reasoning that no owner was available; that
    /// was half right. What is true is that <c>IsForeignTransfer</c> cannot answer here — a pending
    /// push has no receive session, no send session and no staging manifest yet, because the ready IS
    /// the handshake that precedes all three, so a guard written that way would have been a no-op. The
    /// owner had to be recorded rather than derived.
    /// </remarks>
    private readonly ConcurrentDictionary<string, PendingPush> _pendingReady = new(StringComparer.Ordinal);

    private sealed record PendingPush(string ClientId, TaskCompletionSource<FileTransferReady> Ready);

    /// <summary>Handles an inbound <c>file_transfer_ready</c> answering an offer the host made.</summary>
    public void HandleReady(FileTransferReady ready, string channelKey)
    {
        if (ready is null)
            return;

        if (!_pendingReady.TryGetValue(ready.TransferId, out var waiting))
        {
            _logger.LogWarning("Received unexpected file_transfer_ready for {TransferId}; nothing is waiting on it.", ready.TransferId);
            return;
        }

        // THE PEER WE OFFERED IT TO, NOT WHOEVER ANSWERS FIRST (RemEx-5dq3). Without this an unbound
        // connection — loopback is the reachable one, frozen to the empty key since RemEx-4215 — could
        // send file_transfer_ready { accepted: false } for a transfer id it merely knows and win the
        // race against the phone that already consented. PushFileAsync returns false, and the user
        // watches a transfer they agreed to simply not arrive: the "they accepted, and nothing came"
        // failure the comments on that path already warn about. accepted:true early lands in the same
        // place by a different route, through the channel lookup that follows.
        if (!string.Equals(waiting.ClientId, channelKey, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Refusing a file_transfer_ready from {ClientId}: push {TransferId} is waiting on a different client.",
                Remex.Agent.Services.Security.LogRedaction.RedactClientId(channelKey),
                ready.TransferId);
            return;
        }

        waiting.Ready.TrySetResult(ready);
    }

    /// <summary>
    /// Sends a file this host chose to push, to a peer that has already consented (RemEx-y7my).
    /// </summary>
    /// <remarks>
    /// <para>
    /// **THE CONSENT IS NOT ASKED FOR HERE, AND MUST ALREADY EXIST.** The caller negotiates
    /// <c>file_push_offer</c> first and only reaches this with an id the RECEIVER minted and granted.
    /// The phone deliberately raises no second prompt for the transfer offer, so anything that reached
    /// this point unconsented would move bytes silently.
    /// </para>
    /// <para>
    /// Reuses the same streaming loop as a download - backpressure, hashing, acks - because the only
    /// thing that differs about a push is who spoke first.
    /// </para>
    /// </remarks>
    /// <param name="offeredSize">
    /// The size the CONSENT PROMPT SHOWED, not one measured again here (RemEx-ccqb). The caller owes
    /// it a file that has finished being written: this aborts rather than sending a file whose length
    /// no longer agrees, so pointing it at something still growing would refuse every push.
    /// </param>
    public async Task<bool> PushFileAsync(
        string clientId, string transferId, string absolutePath, string fileName, long offeredSize,
        WebSocket controlWs, CancellationToken ct)
    {
        var ready = new TaskCompletionSource<FileTransferReady>(TaskCreationOptions.RunContinuationsAsynchronously);

        // REGISTERED BEFORE THE OFFER GOES OUT. The peer may answer the instant it lands, and a reply
        // that arrives before anything is waiting is dropped as unmatched - which would strand a
        // transfer the user already agreed to.
        var pending = new PendingPush(clientId, ready);
        _pendingReady[transferId] = pending;

        FileStream? source = null;
        try
        {
            try
            {
                source = File.OpenRead(absolutePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Cannot push {TransferId}: the file could not be opened.", transferId);
                return false;
            }

            // **THE OFFERED SIZE IS WHAT GOES ON THE WIRE, AND A FILE THAT NO LONGER MATCHES IT IS
            // NOT SENT.** The number sent used to be measured HERE, independently of the one the
            // consent prompt was built from - two derivations from a file that could change in
            // between, which is the same mistake the name above carries a warning about and which
            // once cost a prompt showing one name while the transfer carried another.
            //
            // The file is still looked at twice; what changed is that the second look no longer
            // decides anything, it only checks. Note the two measurements are not interchangeable
            // even for an unchanged file: FileInfo.Length reads the cached directory entry while
            // FileStream.Length queries the handle, and on Windows the directory entry can lag while
            // another process holds the file open for writing.
            //
            // Deriving it twice also made the receiver unable to check it: it has no way to tell a
            // grown file from a peer inflating the number after consent, so it had to trust whatever
            // the transfer offer claimed. Sending one number the user actually saw is what lets the
            // phone refuse the other case (RemEx-ccqb).
            //
            // Aborting rather than re-offering: the user agreed to receive a specific thing, and this
            // is no longer that thing. Asking again is the caller's business, not ours.
            if (source.Length != offeredSize)
            {
                _logger.LogWarning(
                    "Not pushing {TransferId}: {FileName} is now {ActualSize} bytes, but {OfferedSize} "
                        + "was offered and agreed to.",
                    transferId, fileName, source.Length, offeredSize);
                return false;
            }

            await MessageSerializer.SendAsync(
                controlWs,
                new RemexMessage
                {
                    Type = MessageTypes.FileTransferOffer,
                    FileTransferOffer = new FileTransferOffer
                    {
                        TransferId = transferId,
                        Mode = "push",
                        FileName = fileName,
                        Size = offeredSize,
                    },
                },
                ct);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(ReadyTimeout);

            FileTransferReady reply;
            try
            {
                reply = await ready.Task.WaitAsync(deadline.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning("The peer never acknowledged the push of {TransferId}.", transferId);
                return false;
            }

            if (!reply.Accepted)
            {
                _logger.LogInformation(
                    "The peer declined the push of {TransferId}: {Reason}.", transferId, reply.DeclineReason);
                return false;
            }

            // THE CHANNEL IS LOOKED UP HERE, NOT BEFORE THE OFFER, and the ordering is the fix. The
            // phone opens /ws/files lazily IN RESPONSE to this offer - its handler calls
            // ensureBinaryChannel() before delegating - and handlePushOffer never opens it. Checking
            // first meant that on any session where the user had not already used the Files tab, the
            // push bailed AFTER they had answered the consent prompt: they accepted, and nothing came.
            // By the time a ready arrives the channel is guaranteed up, because the phone awaited it.
            if (!_channels.TryGetValue(clientId ?? string.Empty, out var channel))
            {
                _logger.LogWarning(
                    "Cannot push {TransferId}: the peer acknowledged but its binary channel is not connected.",
                    transferId);
                LogChannelMiss(clientId, "push");

                // **THE PEER IS TOLD, BECAUSE IT IS HOLDING SOMETHING (RemEx-cc30z).** Returning here
                // used to send nothing at all - no cancel, no result - and the peer had just answered
                // accepted=true, which means it registered a receive session, a sink and a staging
                // .remexpart it will not clear for seven days. From the user's side a file they
                // approved simply never arrives, and the only record is this log line on the machine
                // they are not looking at.
                //
                // Cancel rather than a decline: they already accepted, so there is nothing left to
                // decline - what is needed is for them to let go. Their handler releases the push
                // grant, unregisters the sink, deletes the partial and closes the session, which is
                // exactly the cleanup that was being skipped. Symmetric with the download path, which
                // has always answered its own channel miss rather than going quiet.
                //
                // Best-effort on purpose: the control socket may be dying too, and that is the likely
                // reason the binary channel is gone. Failing to send the cancel must not replace a
                // silent abandonment with a thrown one, so it is caught and logged - the transfer is
                // already lost either way, and the peer's 7-day sweep remains the backstop.
                try
                {
                    await SendControlAsync(controlWs, transferId, FileTransferControlActions.Cancel, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // SEPARATED FROM THE FAULTS BELOW, AND WITHOUT THE EXCEPTION OBJECT. A cancelled
                    // token here is an ordinary shutdown, not a fault - MessageSerializer's send gate
                    // throws before it writes anything - so a stack trace at Warning would put a
                    // routine teardown in the log at the same weight as a broken socket. The peer is
                    // not told on this path, which is a real gap and the reason it is stated rather
                    // than merged into the line below: the process is going away, so its 7-day sweep
                    // is the backstop.
                    _logger.LogDebug("Shutting down before {TransferId} could be released at the peer.", transferId);
                }
                catch (Exception ex) when (ex is WebSocketException or InvalidOperationException)
                {
                    // ObjectDisposedException derives from InvalidOperationException, which is the
                    // exception a control socket that has already gone away actually throws - the
                    // likeliest reason the binary channel is missing in the first place.
                    _logger.LogWarning(
                        ex, "Could not tell the peer to release {TransferId} after the channel miss.", transferId);
                }

                return false;
            }

            var session = new SendSession(transferId, clientId ?? string.Empty);
            _sendSessions[transferId] = session;

            // Handed off: the streaming task owns the stream from here and disposes it in its finally.
            var streaming = source;
            source = null;
            _ = Task.Run(() => StreamSenderAsync(channel, session, streaming, offeredSize, controlWs, ct), CancellationToken.None);
            return true;
        }
        catch (Exception ex) when (ex is WebSocketException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "The connection dropped while offering the push of {TransferId}.", transferId);
            return false;
        }
        finally
        {
            // COMPARE AND REMOVE, NOT REMOVE BY KEY (RemEx-6e3mn). Removing by id alone means this
            // finally evicts whatever is registered under it — so a SECOND push that had taken the
            // slot would be stranded until its own 30s deadline, by the first one's cleanup. The
            // KeyValuePair overload only removes when the value still matches, and PendingPush is a
            // record, so equality is the owner plus the TCS reference: it removes ours or nothing.
            //
            // Unreachable today, because transfer ids are receiver-minted GUIDs — filed and fixed
            // because RemEx-5dq3 turned the value into a record and made this a one-line change, not
            // because a collision is expected.
            _pendingReady.TryRemove(new KeyValuePair<string, PendingPush>(transferId, pending));

            // DISPOSED ON EVERY PATH THAT DID NOT HAND IT OFF, including the ones that throw past the
            // typed catches - a cancelled token being the ordinary one. Nulling it on hand-off is what
            // keeps this from closing a stream the streaming task is still reading.
            source?.Dispose();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // /ws/files binary data-plane channel.
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Cuts the file channel of a client whose pairing has been revoked (RemEx-6nkht). Returns true
    /// when there was one to cut.
    /// </summary>
    /// <remarks>
    /// ONLY THE SOCKET. The receive and send sessions are NOT torn down here, because
    /// <see cref="RunChannelAsync"/>'s <c>finally</c> already does exactly that when the loop exits —
    /// suspending receives so partials survive, cancelling sends — and doing it twice from two
    /// threads is how a partial gets deleted out from under a suspend. Aborting the socket is what
    /// makes that <c>finally</c> run.
    /// </remarks>
    public bool DisconnectClient(string? clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return false;
        if (!_channels.TryGetValue(clientId, out var channel)) return false;

        _logger.LogInformation(
            "Cutting the file channel for {ClientId}: pairing revoked.",
            Remex.Agent.Services.Security.LogRedaction.RedactClientId(clientId));
        channel.Abort();
        return true;
    }

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

        // ONE buffer for the whole channel, reused frame after frame, instead of a fresh array per
        // frame. Safe because every consumer of a frame is awaited before the next receive can
        // overwrite it — see the data-frame case below, and WriteChunkAsync, which writes the stream
        // awaited and hashes synchronously and retains nothing (RemEx-8su9).
        var receiveBuffer = ArrayPool<byte>.Shared.Rent(MaxFrameBytes);
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var frameLength = await ReceiveBinaryAsync(ws, receiveBuffer, ct);
                if (frameLength < 0)
                {
                    _logger.LogInformation("DIAG /ws/files recv returned null for {ClientId} after {N} data frame(s) (wsState={State}) — loop exiting.", Remex.Agent.Services.Security.LogRedaction.RedactClientId(key), diagDataFrames, ws.State);
                    break; // socket closed or an oversize/protocol-invalid frame.
                }

                // AsMemory, NOT receiveBuffer[..frameLength]: the range indexer on an ARRAY allocates
                // and copies, which would have quietly reinstated the per-frame allocation this
                // change exists to remove — and it compiles either way.
                var frameBytes = receiveBuffer.AsMemory(0, frameLength);
                if (!FileFrameCodec.TryRead(frameBytes, out var envelope, out var payload) || envelope is null)
                {
                    _logger.LogWarning("Discarding malformed /ws/files frame ({Len} bytes) from {ClientId}.", frameLength, Remex.Agent.Services.Security.LogRedaction.RedactClientId(key));
                    continue;
                }

                // OWNERSHIP, BEFORE THE SWITCH SO IT CANNOT BE FORGOTTEN BY A FUTURE FRAME KIND
                // (RemEx-0719). Every case below keys on envelope.TransferId ALONE, so the id was a
                // bearer capability: hold the number, control the transfer. Nothing checked that the
                // session belonged to the socket presenting the frame.
                //
                // RemEx-4u0d stopped a loopback caller CLAIMING A PAIRED ID, which closed channel
                // displacement. It deliberately still admits a loopback connection with a blank or
                // unknown id, and such a connection needs no identity at all to get here - only a
                // transferId. So it could send an Error frame naming a phone's in-flight transfer
                // and cancel it, or a Data frame at the right offset and write its bytes into the
                // victim's partial file. That is the injection half RemEx-4u0d did not close.
                //
                // What kept it unreachable was transferId entropy (UUID.randomUUID / Guid.NewGuid)
                // and the fact that ids do not currently escape the connection that owns them. That
                // is a fact about today's code, not an invariant - one future change that logs a
                // transferId or puts it in a diagnostic export would make this live, and that change
                // would look harmless.
                //
                // THIS BINDS THE BINARY CHANNEL ONLY. The /ws JSON control plane still keys on
                // transferId alone: HandleControl takes no clientId at all, so an identity-less
                // loopback /ws connection can still cancel a transfer with one message, and
                // BeginReceiveAsync re-owns an existing id without checking the current holder. Found
                // in review of this change and filed separately - do not read this comment as saying
                // the capability is gone everywhere.
                if (IsForeignTransfer(key, envelope.TransferId))
                {
                    _logger.LogWarning(
                        "Refusing a /ws/files {Kind} frame from {ClientId}: transfer {TransferId} belongs to a different client.",
                        envelope.Kind,
                        Remex.Agent.Services.Security.LogRedaction.RedactClientId(key),
                        envelope.TransferId);
                    continue;
                }

                switch (envelope.Kind)
                {
                    case FileFrameKinds.Data:
                        if (diagDataFrames++ == 0)
                            _logger.LogInformation("DIAG first DATA frame on /ws/files: transfer={Id}, offset={Off}, len={Len}.", envelope.TransferId, envelope.Offset, payload.Length);
                        // No ToArray. The payload is a view into receiveBuffer, and this await is
                        // what makes that safe: the loop cannot receive the next frame — and so
                        // cannot overwrite the buffer — until the handler has finished with it.
                        await HandleInboundDataFrameAsync(channel, envelope, payload, ct);
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
            // Returned here, not inside the loop: the buffer is live for the whole channel, and the
            // loop is the only thing that ever reads or writes it. Nothing has escaped by this point
            // because every frame's consumer was awaited before the next iteration.
            ArrayPool<byte>.Shared.Return(receiveBuffer);

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

    /// <summary>
    /// True when <paramref name="transferId"/> is known AND belongs to a client other than
    /// <paramref name="channelKey"/>. Used to refuse a frame presented on someone else's socket
    /// (RemEx-0719).
    /// </summary>
    /// <remarks>
    /// A live session answers first. When there is none, the STAGING MANIFEST does - and that second
    /// lookup is not a nicety, it closes the window this guard would otherwise miss entirely.
    /// <see cref="SuspendReceive"/> removes the session on a dropped socket but deliberately KEEPS the
    /// partial so the transfer can resume. In that window the id looks unknown - and
    /// <see cref="CancelReceive"/> is NOT a no-op for an unknown id, because its DeleteStaging call
    /// runs UNCONDITIONALLY, by id, whether or not a session was found. So an Error frame from any
    /// channel would delete a phone's resumable partial in exactly the window that matters most; a
    /// dropped mobile socket is routine, not exotic. The manifest already records the owning ClientId
    /// and the resume path at BeginReceiveAsync already validates it, so the answer was on disk the
    /// whole time. This was found by the end-to-end test below, not by reasoning.
    ///
    /// The manifest is read ONLY when no live session exists, so an in-flight transfer costs no file
    /// I/O per frame - the read happens on the rare-or-hostile path.
    ///
    /// A GENUINELY unknown id - no session and no manifest - is still not foreign, deliberately. It
    /// already dies downstream (WriteChunkAsync throws "No active inbound transfer", which
    /// HandleInboundDataFrameAsync catches; the ack and error cases TryGet and no-op; DeleteStaging
    /// finds nothing), and treating it as foreign would change behaviour for late-arriving and
    /// post-completion frames while claiming to be a security fix.
    ///
    /// The guarantee is "a PAIRED client's transfer is protected from every other channel key", not
    /// "keys are isolated from each other". Two identity-less local callers both present the blank
    /// key RemEx-4u0d deliberately still admits, so they are indistinguishable here and could reach
    /// each other's transfers. That matters only if a second local consumer of this endpoint ever
    /// exists; today there is none.
    ///
    /// Both directions are consulted: a receive session is what a Data frame writes into, and a send
    /// session is what an Ack advances and an Error cancels.
    /// </remarks>
    internal bool IsForeignTransfer(string channelKey, string transferId)
    {
        if (_receiveSessions.TryGetValue(transferId, out var receiving))
        {
            return !string.Equals(receiving.ClientId, channelKey, StringComparison.Ordinal);
        }

        if (_sendSessions.TryGetValue(transferId, out var sending))
        {
            return !string.Equals(sending.ClientId, channelKey, StringComparison.Ordinal);
        }

        // No live session: fall back to the staging manifest, which is the owner record that survives
        // a dropped socket. See the remarks - this is the suspended-transfer window.
        if (TryLoadManifest(ManifestPathFor(transferId), out var manifest) && manifest is not null)
        {
            return !string.Equals(manifest.ClientId, channelKey, StringComparison.Ordinal);
        }

        return false;
    }

    /// <summary>
    /// Writes one inbound data frame's payload to the staging partial and acks when due.
    /// </summary>
    /// <remarks>
    /// <paramref name="payload"/> is a VIEW into the channel's reusable receive buffer, not a private
    /// copy. That is safe only because this method is awaited by the receive loop before it reads
    /// again, and because nothing below retains the memory: <see cref="WriteChunkAsync"/> awaits the
    /// stream write and appends to the hash synchronously. Do not stash it, and do not start
    /// background work over it without copying first (RemEx-8su9).
    /// </remarks>
    private async Task HandleInboundDataFrameAsync(FileChannel channel, FileFrameEnvelope envelope, ReadOnlyMemory<byte> payload, CancellationToken ct)
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

    /// <summary>
    /// Says which key was looked up and which are actually registered, when a file channel is missing
    /// (RemEx-3ipz3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// ONE LINE SEPARATES THE TWO SUSPECTS BEHIND RemEx-6bfyt, which both transfer directions reach
    /// through this same lookup and which are indistinguishable from the phone. A registered set that
    /// is NON-EMPTY and does not contain the key is client-id SKEW — /ws/files keys the channel on
    /// the query-string clientId while the control path looks up connectionClientId, compared
    /// Ordinal. A registered set that is EMPTY means the phone never opened /ws/files at all, most
    /// likely refused by its protocolVersion &gt;= 3 gate, and that rejection is already logged at
    /// the endpoint with its reason.
    /// </para>
    /// <para>
    /// REDACTED, like every other client id in this file. A diagnostic is exportable — the whole
    /// point of it is that a user sends it somewhere — so printing paired-device identifiers raw
    /// would put them in a file that leaves the machine.
    /// </para>
    /// </remarks>
    private void LogChannelMiss(string? clientId, string direction)
    {
        var registered = _channels.Keys
            .Select(Remex.Agent.Services.Security.LogRedaction.RedactClientId)
            .ToArray();

        _logger.LogWarning(
            "No /ws/files channel for {ClientId} on the {Direction} path. Registered: {Registered} ({Count}).",
            Remex.Agent.Services.Security.LogRedaction.RedactClientId(clientId ?? string.Empty),
            direction,
            registered.Length == 0 ? "(none)" : string.Join(", ", registered),
            registered.Length);
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

    /// <summary>
    /// Testable seam: resolves a download's source <paramref name="rootId"/> and opens it for reading.
    ///
    /// <para>Goes through <see cref="SharedRootReadResolver"/> rather than
    /// <see cref="IFileTransferService.OpenForReadAsync"/> so a file reached by full-device browsing — i.e. a
    /// bare mounted volume that is not a pinned shared root — downloads instead of failing with
    /// "Unknown shared root" (RemEx-hb1t.6). Calling the service directly here is what left every real
    /// Android download broken after RemEx-39jw fixed only the legacy v2 handler.</para>
    ///
    /// <para>Fail-closed: an empty <paramref name="clientId"/> (unidentified peer) has no full-browse grant,
    /// so the resolver refuses rather than widening access.</para>
    /// </summary>
    internal Task<Stream> OpenDownloadSourceAsync(string clientId, string rootId, string relativePath, CancellationToken ct)
        => _readResolver.OpenForReadAsync(rootId, relativePath, clientId, ct);

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
            LogChannelMiss(clientId, "download");
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
            source = await OpenDownloadSourceAsync(clientId, offer.DestRoot!, hostRelativePath, ct);
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

    // internal, not private, so HostSendDrainTests can drive the send path directly and assert the one
    // property that matters here: that no file_transfer_complete reaches the control socket before the
    // peer has acked the data (RemEx-zd8ws). Reaching it through the public offer entry points instead
    // would mean standing up consent, a real volume and a live /ws/files channel loop, and would still
    // leave the ack — the very thing under test — to be injected through a scripted socket race.
    internal async Task StreamSenderAsync(FileChannel channel, SendSession session, Stream source, long size, WebSocket controlWs, CancellationToken outerCt)
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

            // DRAIN BEFORE COMPLETING (RemEx-zd8ws). The bulk data frames and file_transfer_complete
            // travel on SEPARATE sockets — /ws/files and the control /ws — and TCP orders bytes only
            // WITHIN one connection, never between two. Announcing completion the instant the last frame
            // is enqueued lets the tiny complete overtake the still-in-flight bulk bytes: the peer
            // finalizes a zero-byte transfer and reports "Transfer incomplete." while the data is
            // literally still arriving. Measured on a 353,985-byte screenshot push: every one of its two
            // data frames was dropped as "No sink", because handleComplete had already torn the sink
            // down.
            //
            // THE BACKPRESSURE WAIT ABOVE IS NOT THIS WAIT, which is exactly why this survived so long.
            // It blocks only once outstanding unacked bytes exceed MaxUnackedBytes (8 MB), so every
            // transfer smaller than that — nearly all of them, and every screenshot — reached this line
            // without ever forcing a single ack round trip. The failure was INVERSELY sized: large
            // pushes incidentally survived because backpressure had already drained them, small ones
            // failed every time. That is the shape that makes a bug look like a flaky feature.
            //
            // This mirrors the Kotlin fix (RemEx-y6x6, FileTransferEngine.runUpload); the phone learned
            // it first and the C# sender never got the same treatment. Both receivers ack the final
            // frame unconditionally — `envelope.Final || interval elapsed`, in HandleInboundDataFrameAsync
            // here and FileHostHandler.kt on the phone — so the ack this waits for is guaranteed to be
            // sent, and it carries a committed offset covering everything we wrote.
            //
            // Against sentOffset rather than `size`: sentOffset is what actually went on the wire. The
            // two are equal whenever the source's length matched the declared size, and where they
            // differ, waiting for `size` would be waiting on bytes nobody ever sent.
            if (!await WaitForFinalAckAsync(session, sentOffset, ct))
            {
                _logger.LogWarning(
                    "Host-sent transfer {TransferId} gave up waiting for the peer to acknowledge its data ({Committed}/{Sent} bytes acked); not announcing completion.",
                    session.TransferId, session.CommittedOffset, sentOffset);

                // Tell the peer to let go, for the RemEx-cc30z reason: it accepted, so it is holding a
                // receive session, a sink and a staging partial that only its 7-day sweep would collect.
                // Best-effort on purpose — a failure to send the release must not replace a silent
                // abandonment with a thrown one, and the transfer is lost either way. Narrowed to the
                // faults the channel-miss release above already handles, rather than catching
                // everything, so an unexpected exception still surfaces instead of being filed away as
                // "the peer could not be told".
                try
                {
                    await SendControlAsync(controlWs, session.TransferId, FileTransferControlActions.Cancel, ct);
                }
                catch (Exception releaseEx) when (releaseEx is WebSocketException or InvalidOperationException or OperationCanceledException)
                {
                    _logger.LogDebug(
                        releaseEx, "Could not release stalled transfer {TransferId} at the peer.", session.TransferId);
                }

                return;
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

    /// <summary>
    /// Waits until the peer has acked every byte <see cref="StreamSenderAsync"/> put on the wire.
    /// Returns false when it stops acking altogether (RemEx-zd8ws).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A DEAD SOCKET DOES NOT RELY ON THE TIMEOUT AT ALL, which is what keeps
    /// <see cref="AckDrainIdleTimeout"/> free to be generous. <see cref="RunChannelAsync"/>'s teardown
    /// cancels every send session belonging to a client whose /ws/files channel dropped; that cancels
    /// <paramref name="ct"/> and unblocks this immediately. The idle window covers only the narrower
    /// case of a peer that holds its socket open and quietly stops acking — which is indistinguishable
    /// from a very slow one except by waiting.
    /// </para>
    /// <para>
    /// The offset is re-checked around every wait rather than trusting one wakeup, because the ack
    /// signal is a counting semaphore: acks that arrived during the send loop and were never consumed
    /// by the backpressure wait leave permits behind, so a wakeup does not by itself mean progress.
    /// Conversely no wakeup can be lost — <see cref="SendSession.OnAck"/> stores the offset BEFORE it
    /// releases, so an ack landing between the check and the wait leaves a permit that satisfies it.
    /// </para>
    /// </remarks>
    private async Task<bool> WaitForFinalAckAsync(SendSession session, long sentOffset, CancellationToken ct)
    {
        while (session.CommittedOffset < sentOffset)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idle.CancelAfter(AckDrainIdleTimeout);
            try
            {
                await session.WaitForAckAsync(idle.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Our own idle window expired. A REAL cancellation — shutdown, a dropped channel, a peer
                // cancel — is deliberately not caught here: it must keep propagating as it always did,
                // so that a torn-down transfer still logs as cancelled rather than as a stalled peer.
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Tells the peer to stop and let go of a transfer (RemEx-cc30z).
    /// </summary>
    /// <remarks>
    /// The counterpart to <see cref="HandleControl"/>, which is the inbound half and has existed all
    /// along — the host could always be told to release a transfer and had no way to say it. Used
    /// where a push dies after the peer has already accepted, at which point the peer is holding a
    /// receive session, a sink and a staging file that only its orphan sweep would ever collect.
    /// </remarks>
    private static async Task SendControlAsync(WebSocket controlWs, string transferId, string action, CancellationToken ct)
    {
        await MessageSerializer.SendAsync(controlWs, new RemexMessage
        {
            Type = MessageTypes.FileTransferControl,
            ProtocolVersion = ProtocolVersionPolicy.Current,
            FileTransferControl = new FileTransferControl
            {
                TransferId = transferId,
                Action = action,
            },
        }, ct);
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

    /// <summary>
    /// Reads one complete binary message into <paramref name="destination"/>, reassembling WebSocket
    /// fragments. Returns the byte count, or -1 when the socket closed or the frame was oversize.
    /// </summary>
    /// <remarks>
    /// Fills a buffer the CALLER owns rather than returning a fresh array. The previous shape
    /// accumulated into a growable <see cref="MemoryStream"/> and finished with <c>ToArray()</c>,
    /// which at the 256 KB frame cap meant the stream's doubling allocations plus one more Large
    /// Object Heap array — every frame, for a buffer discarded moments later (RemEx-8su9).
    ///
    /// <paramref name="destination"/> must be at least <see cref="MaxFrameBytes"/>; anything longer
    /// is refused here rather than silently truncated, because a truncated frame would parse as a
    /// valid header with a short payload and be written to the file as if it were complete.
    /// </remarks>
    private static async Task<int> ReceiveBinaryAsync(WebSocket ws, Memory<byte> destination, CancellationToken ct)
    {
        var received = 0;
        // ValueWebSocketReceiveResult, because the Memory overload of ReceiveAsync returns the struct
        // form — which is also the point of using it: no per-receive result allocation either.
        ValueWebSocketReceiveResult result;
        do
        {
            if (received >= destination.Length)
                return -1; // No room even to attempt the next fragment: treat as oversize.

            var segment = destination[received..];
            result = await ws.ReceiveAsync(segment, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                return -1;

            received += result.Count;
            if (received > MaxFrameBytes)
                return -1; // Oversize frame: close the channel rather than buffer unbounded data.
        }
        while (!result.EndOfMessage);

        return received;
    }

    private static async Task ReHashPartialAsync(string partialPath, IncrementalHash hasher, CancellationToken ct)
    {
        await using var stream = new FileStream(
            partialPath, FileMode.Open, FileAccess.Read, FileShare.None, 65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
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

    // internal for the same reason as StreamSenderAsync above: the drain test has to play the peer and
    // deliver the ack itself. Still a nested type, so this widens nothing outside the assembly.
    internal sealed class SendSession
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

    // internal, not private, so FileChannelSendFramingTests can assert on the bytes handed to the
    // socket. Without that the bounded ArraySegment in SendFrameAsync is unguarded: replacing it with
    // `new ArraySegment<byte>(buffer)` compiles, runs, and ships the rented array's zero-padded tail.
    internal sealed class FileChannel
    {
        private readonly WebSocket _ws;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private volatile bool _superseded;

        public FileChannel(WebSocket ws) => _ws = ws;

        public void MarkSuperseded() => _superseded = true;

        /// <summary>Kills the socket, for a client whose pairing has been revoked (RemEx-6nkht).</summary>
        /// <remarks>
        /// Superseded FIRST so that a send already past its own state check cannot put another frame
        /// of somebody else's file on a socket the user has just cut. Abort rather than a close
        /// handshake, for the reason <c>ClientSessionRegistry.DisconnectClient</c> gives.
        /// </remarks>
        public void Abort()
        {
            _superseded = true;
            try { _ws.Abort(); } catch (ObjectDisposedException) { /* the loop got there first */ }
        }

        /// <summary>
        /// Writes one framed message to the channel.
        /// </summary>
        /// <remarks>
        /// The frame buffer is RENTED rather than allocated. At the 256 KB payload cap a fresh
        /// <c>byte[]</c> per frame is a Large Object Heap allocation, and a transfer running at
        /// 100 MB/s produces roughly 400 of them a second — pure Gen2 pressure for a buffer that is
        /// dead the instant the send completes (RemEx-npdm).
        ///
        /// THE LIFETIME IS THE WHOLE SAFETY ARGUMENT, and here it is trivial rather than delicate:
        /// <c>WebSocket.SendAsync</c> does not retain the buffer beyond the returned task, and that
        /// task is awaited before the <c>finally</c> returns it. Nothing else can observe the array.
        /// This is deliberately NOT the shape that keeps reverting in the capture path (RemEx-lcp8),
        /// where the buffer outlives its producer and is cached by the caller.
        ///
        /// AND THE LENGTH TRAVELS SEPARATELY. <c>ArrayPool</c> returns an array AT LEAST the requested
        /// size, usually a power-of-two bucket larger, so the segment must be bounded by what was
        /// WRITTEN. This is not merely a correctness detail: <c>Return</c> is called without
        /// <c>clearArray</c>, so the tail past the frame holds WHATEVER THE PREVIOUS RENTER LEFT —
        /// realistically bytes of an earlier frame, possibly from a different file or a different
        /// transfer. Sending <c>new ArraySegment(buffer)</c> would put that on the wire to the peer.
        /// The bound is load-bearing for confidentiality, not just for a well-formed frame.
        ///
        /// Not clearing on return is the deliberate other half of that: clearing would memset the
        /// full bucket on every frame — at 100 MB/s that is the throughput this change exists to
        /// stop wasting — and the bound already prevents anything past the frame from being sent.
        /// </remarks>
        public async Task SendFrameAsync(FileFrameEnvelope envelope, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            if (_superseded)
                return;

            var header = FileFrameCodec.SerializeHeader(envelope);
            var frameLength = FileFrameCodec.GetFrameLength(header.Length, payload.Length);
            var buffer = ArrayPool<byte>.Shared.Rent(frameLength);
            try
            {
                // Bound the send by what WriteFrame reports writing, not by the precomputed length,
                // so the "the length travels separately" invariant is visible at the call site.
                var written = FileFrameCodec.WriteFrame(header, payload.Span, buffer);

                await _sendLock.WaitAsync(ct);
                try
                {
                    await _ws.SendAsync(
                        new ArraySegment<byte>(buffer, 0, written),
                        WebSocketMessageType.Binary,
                        endOfMessage: true,
                        ct);
                }
                finally
                {
                    _sendLock.Release();
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
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
