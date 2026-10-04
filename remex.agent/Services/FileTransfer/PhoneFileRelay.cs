using System.Collections.Concurrent;
using System.Collections.Frozen;
using Microsoft.Extensions.Logging;
using Remex.Agent.Services.Security;
using Remex.Core.Guards;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Validation;
using Remex.Desktop.Services;
using Remex.Desktop.Services.FileTransfer;
using Remex.Desktop.ViewModels;

namespace Remex.Agent.Services.FileTransfer;

/// <summary>
/// The PC browsing a paired phone (RemEx-xt0af): relays a fixed set of <c>file_*</c> requests from the
/// PC's own File Transfer screen down a paired phone's existing session, hands the phone's replies
/// back, and runs PC-started transfers over the phone's own file channel. Since RemEx-fgmne the set
/// includes <c>file_manage_request</c> (rename, delete, move, copy, new folder), but only for a phone
/// that said so.
/// </summary>
/// <remarks>
/// <para>
/// THE CONNECTION STILL RUNS ANDROID → PC. The phone dialled <c>/ws</c> and <c>/ws/files</c>; nothing
/// here opens a socket. The phone's file host has always answered these requests on that session —
/// the PC just never sent them. No message type is new.
/// </para>
/// <para>
/// FIVE RULES, EACH WITH A TEST, AND EACH ONE IS THE THING STANDING BETWEEN A LOCAL PROCESS AND THE
/// PHONE'S FILES:
/// </para>
/// <list type="number">
/// <item>The target must be paired AND connected right now (<see cref="IsReachable"/>), or
/// <see cref="Open"/> and every send throw <see cref="PhoneNotConnectedException"/>.</item>
/// <item>Only the request types in <see cref="RelayedRequests"/> are forwarded: the seven read-only
/// ones, and <c>file_manage_request</c> (RemEx-fgmne). Root management (<c>file_root_manage_request</c>:
/// what the phone shares stays the phone's decision), hashing and the legacy v2 transfer are refused
/// before they reach the wire, whatever the caller asks for.</item>
/// <item>A <c>file_manage_request</c> is forwarded only when THIS phone's own roots reply said the
/// person allows the PC to change files (<see cref="FileCapabilities.PcChanges"/>), and only when its
/// operation, names and paths pass <c>FilePathValidation</c>. The phone checks its switch and its
/// shared folders again on every request: this side keeps the screen honest, that side cannot be
/// talked around.</item>
/// <item>An outbound request carries no client id at all. The phone does not need one to answer, and
/// a request that named some other device would be a lie told on the PC's behalf.</item>
/// <item>A reply is accepted only from a session that PROVED its identity and is not loopback
/// (RemEx-4215's rule, applied to replies), and only for a request this relay sent to THAT client id.
/// It is delivered to the one connection that asked. Nothing is broadcast.</item>
/// </list>
/// <para>
/// THE PHONE'S OWN SETTINGS ARE THE CONSENT. What answers is what the person shared under "Access from
/// your PC" on the phone. No new prompt is raised there, and no PC-side grant is consulted.
/// </para>
/// </remarks>
public sealed class PhoneFileRelay : IPhoneFileAccess, IPhoneFileTransfers, IDisposable
{
    /// <summary>Each relayed reply type, and the request type it answers.</summary>
    internal static readonly FrozenDictionary<string, string> RequestForReply =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MessageTypes.FileRootsResponse] = MessageTypes.FileRootsRequest,
            [MessageTypes.FileBrowseResponse] = MessageTypes.FileBrowseRequest,
            [MessageTypes.FileVolumesResponse] = MessageTypes.FileVolumesRequest,
            [MessageTypes.FileSearchResponse] = MessageTypes.FileSearchRequest,
            [MessageTypes.FileManifestResponse] = MessageTypes.FileManifestRequest,
            [MessageTypes.FileMetadataResponse] = MessageTypes.FileMetadataRequest,
            [MessageTypes.FileThumbnailResponse] = MessageTypes.FileThumbnailRequest,
            // RemEx-fgmne: managing files on the phone from the PC. A deliberate widening, gated per phone
            // by FileCapabilities.PcChanges and by the phone's own switch. file_root_manage_request is NOT
            // here and must not be: which folders a phone shares is only ever the phone's decision.
            [MessageTypes.FileManageResponse] = MessageTypes.FileManageRequest,
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// The ONLY request types the PC may send to a phone: seven read-only ones, plus
    /// <c>file_manage_request</c>, which <see cref="Connection.SendAsync"/> additionally gates on the
    /// phone's own say-so. Root management is deliberately absent.
    /// </summary>
    internal static readonly FrozenSet<string> RelayedRequests =
        RequestForReply.Values.ToFrozenSet(StringComparer.Ordinal);

    private readonly ILogger<PhoneFileRelay> _logger;
    private readonly PairedClientRegistry _paired;
    private readonly ClientSessionRegistry _sessions;
    private readonly IPairedDeviceSource _devices;
    private readonly TransferSessionManager _transfers;
    private readonly ConcurrentDictionary<Guid, Connection> _connections = new();

    /// <summary>
    /// How long a relayed request may wait for the phone before it is answered with a failure.
    /// </summary>
    /// <remarks>
    /// Longer than <see cref="FileTransferClient"/>'s own per-request timeouts on purpose: the client
    /// normally gives up first and says so, and this is the backstop that guarantees no request is
    /// left registered here forever. Test seam only; DI binds constructors.
    /// </remarks>
    internal TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>
    /// How long a relayed copy or move may wait. Those two stream the whole file on the phone before it
    /// answers, so the 90-second backstop would report a healthy large copy as failed while it was still
    /// running (the PC client has no deadline for them either, RemEx-l519). Test seam only.
    /// </summary>
    internal TimeSpan CopyMoveTimeout { get; init; } = TimeSpan.FromMinutes(30);

    public PhoneFileRelay(
        ILogger<PhoneFileRelay> logger,
        PairedClientRegistry paired,
        ClientSessionRegistry sessions,
        IPairedDeviceSource devices,
        TransferSessionManager transfers)
    {
        _logger = Guard.NotNull(logger);
        _paired = Guard.NotNull(paired);
        _sessions = Guard.NotNull(sessions);
        _devices = Guard.NotNull(devices);
        _transfers = Guard.NotNull(transfers);
        _sessions.ProvenSessionChanged += OnProvenSessionChanged;
    }

    /// <inheritdoc />
    public event Action? AvailabilityChanged;

    /// <inheritdoc />
    public IReadOnlyList<PhoneFileSource> AvailablePhones() =>
        [.. _devices.PairedDevices()
            .Where(row => row.IsOnline && IsReachable(row.ClientId))
            .Select(row => new PhoneFileSource(row.ClientId, FriendlyName(row)))];

    /// <inheritdoc />
    public IPhoneFileConnection Open(string clientId)
    {
        if (!IsReachable(clientId))
            throw new PhoneNotConnectedException();

        var connection = new Connection(this, clientId);
        _connections[connection.Id] = connection;
        return connection;
    }

    /// <summary>Paired, and holding a live proven session right now.</summary>
    internal bool IsReachable(string? clientId) =>
        !string.IsNullOrWhiteSpace(clientId)
        && _paired.IsClientPaired(clientId)
        && _sessions.IsConnected(clientId);

    /// <summary>
    /// Hands a phone's reply to the one relay connection waiting on it. Returns false — and delivers
    /// nothing — when the sender may not answer, or nothing asked.
    /// </summary>
    /// <param name="senderClientId">The id the sending connection settled on.</param>
    /// <param name="identityProven">Whether that connection proved the id (pairing or reconnect proof).</param>
    /// <param name="isLoopback">Whether that connection is this PC talking to itself.</param>
    public bool TryDeliverReply(string? senderClientId, bool identityProven, bool isLoopback, RemexMessage message)
    {
        if (message is null)
            return false;

        // A LOOPBACK OR UNPROVEN SENDER IS REFUSED BEFORE ANYTHING ELSE IS LOOKED AT. Loopback never
        // proves an id and can claim any one it likes (RemEx-4215), so without this any local process —
        // including an unelevated one — could answer the PC's own questions about a phone and put its
        // own file listing on the PC's screen under that phone's name.
        if (isLoopback || !identityProven || string.IsNullOrWhiteSpace(senderClientId))
        {
            _logger.LogWarning(
                "Refused a {Type} from a connection that has not proved it is a paired phone.", message.Type);
            return false;
        }

        // STILL PAIRED, CHECKED AGAIN NOW. A reply already in flight when the phone was unpaired comes
        // from a session that proved its id earlier; it is not a paired phone's answer any more, and an
        // unpaired phone's listing must not reach the PC's screen.
        if (!_paired.IsClientPaired(senderClientId))
        {
            _logger.LogWarning(
                "Dropped a {Type} from {ClientId}: that phone is no longer paired.",
                message.Type, LogRedaction.RedactClientId(senderClientId));
            return false;
        }

        if (!RequestForReply.TryGetValue(message.Type, out var requestType))
            return false;

        var requestId = ReplyRequestId(message);
        if (requestType != MessageTypes.FileRootsRequest && string.IsNullOrEmpty(requestId))
            return false;

        foreach (var connection in _connections.Values)
        {
            // THE SENDER'S OWN ID, NOT THE ONE IN THE MESSAGE BODY. A phone may only answer what was
            // asked of it; a reply naming some other phone's request id is simply not found here.
            if (!string.Equals(connection.ClientId, senderClientId, StringComparison.Ordinal))
                continue;

            if (connection.TryComplete(requestType, requestId ?? string.Empty, message))
                return true;
        }

        _logger.LogDebug(
            "Dropped a {Type} from {ClientId}: nothing on this PC asked that phone for it.",
            message.Type, LogRedaction.RedactClientId(senderClientId));
        return false;
    }

    // ─── IPhoneFileTransfers ─────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task DownloadAsync(
        string clientId, string rootId, string remoteRelativePath, string localPath,
        IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var controlWs = ControlSocketOrThrow(clientId);
        var result = await _transfers.PullFileAsync(
            clientId, rootId, remoteRelativePath, localPath, controlWs, ToByteProgress(progress), ct);
        ThrowIfFailed(clientId, result);
    }

    /// <inheritdoc />
    public async Task UploadAsync(
        string clientId, string localPath, string rootId, string remoteDirectory,
        IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var controlWs = ControlSocketOrThrow(clientId);
        long? total = null;
        try
        {
            total = new FileInfo(localPath).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // The transfer engine reports the unreadable file with its own reason.
        }

        var result = await _transfers.UploadFileToPeerAsync(
            clientId, localPath, rootId, remoteDirectory, controlWs, ToByteProgress(progress, total), ct);
        ThrowIfFailed(clientId, result);
    }

    private System.Net.WebSockets.WebSocket ControlSocketOrThrow(string clientId)
    {
        if (!IsReachable(clientId))
            throw new PhoneNotConnectedException();
        return _sessions.ControlSocketFor(clientId) ?? throw new PhoneNotConnectedException();
    }

    private static IProgress<long>? ToByteProgress(IProgress<TransferProgress>? progress, long? total = null) =>
        progress is null ? null : new InlineProgress(bytes => progress.Report(new TransferProgress(bytes, total)));

    /// <summary>
    /// Turns a failed verdict into the exception the transfer queue describes best.
    /// </summary>
    /// <remarks>
    /// The PC's OWN diagnoses (<see cref="PeerTransferFailure"/>) become types the queue words in the
    /// user's language; their English goes to the log and nowhere else. Anything else came from the
    /// phone, which writes its refusals for people ("Destination folder not found or read-only."), and
    /// is shown as it wrote it — the same promise <see cref="FileTransferHostException"/> makes for the
    /// PC's own host.
    /// </remarks>
    private void ThrowIfFailed(string clientId, FileTransferResult result)
    {
        if (result.Verified)
            return;

        _logger.LogInformation(
            "A PC-started transfer with {ClientId} failed: {Reason}",
            LogRedaction.RedactClientId(clientId), result.Error);

        if (!IsReachable(clientId))
            throw new PhoneNotConnectedException();

        throw FailureFor(result.Error);
    }

    /// <summary>
    /// The exception a failed verdict's reason becomes. Every <see cref="PeerTransferFailure"/>
    /// constant maps to a type the transfer queue localizes; only a reason the phone wrote reaches
    /// <see cref="FileTransferHostException"/>.
    /// </summary>
    internal static Exception FailureFor(string? error) => error switch
    {
        PeerTransferFailure.NoAnswer
            or PeerTransferFailure.StoppedResponding
            or PeerTransferFailure.StoppedAcknowledging => new TimeoutException(error),
        PeerTransferFailure.ConnectionDropped
            or PeerTransferFailure.ChannelMissing
            or PeerTransferFailure.Stopped => new PhoneNotConnectedException(error),
        PeerTransferFailure.Incomplete
            or PeerTransferFailure.HashMismatch => new FileTransferIntegrityException(fromPhone: true),
        PeerTransferFailure.PhoneStopped => new PhoneTransferFailedException(PhoneTransferProblem.PhoneStopped, error),
        PeerTransferFailure.PhoneDeclined
            or PeerTransferFailure.PhoneDidNotAccept => new PhoneTransferFailedException(PhoneTransferProblem.PhoneRefused, error),
        PeerTransferFailure.TooLarge => new PhoneTransferFailedException(PhoneTransferProblem.FileTooLarge, error),
        PeerTransferFailure.LocalFileChanged => new PhoneTransferFailedException(PhoneTransferProblem.FileChanged, error),
        PeerTransferFailure.NameNotAllowed => new PhoneTransferFailedException(PhoneTransferProblem.NameNotAllowed, error),
        // The local file could not be read (upload) or landed (download): the queue's plain IOException
        // arm already says which side, in the user's language.
        PeerTransferFailure.LocalFileMissing
            or PeerTransferFailure.LocalFileUnreadable
            or PeerTransferFailure.SaveFailed => new IOException(error),
        PeerTransferFailure.Failed
            or PeerTransferFailure.PhoneRequired
            or PeerTransferFailure.RootRequired
            or PeerTransferFailure.BadDestination => new PhoneTransferFailedException(PhoneTransferProblem.Unknown, error),
        _ => FileTransferHostException.ForHostError(error, "The phone transfer failed without a reason."),
    };

    /// <summary>Synchronous <see cref="IProgress{T}"/>: the queue item marshals to the UI itself.</summary>
    private sealed class InlineProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }

    // ─── Session lifecycle ───────────────────────────────────────────────────────

    private void OnProvenSessionChanged(string clientId)
    {
        // ASK, DO NOT ASSUME WHICH WAY IT WENT. A phone that redialled before its old session unwound
        // raises this on the way out of the old one while the new one is already live; that phone is
        // still here and its connections must keep working.
        if (!IsReachable(clientId))
        {
            foreach (var connection in _connections.Values)
            {
                if (string.Equals(connection.ClientId, clientId, StringComparison.Ordinal))
                    connection.OnPhoneGone();
            }
        }

        try
        {
            AvailabilityChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A phone availability subscriber threw.");
        }
    }

    public void Dispose()
    {
        _sessions.ProvenSessionChanged -= OnProvenSessionChanged;
        foreach (var connection in _connections.Values)
            connection.Dispose();
    }

    private static string? FriendlyName(PairedDeviceRow row)
    {
        if (!string.IsNullOrWhiteSpace(row.NameOverride)) return row.NameOverride;
        return string.IsNullOrWhiteSpace(row.DeviceName) ? null : row.DeviceName;
    }

    /// <summary>The request id a relayed REQUEST carries; null for roots, which has none.</summary>
    internal static string? RequestId(RemexMessage message) => message.Type switch
    {
        MessageTypes.FileBrowseRequest => message.FileBrowseRequest?.RequestId,
        MessageTypes.FileVolumesRequest => message.FileVolumesRequest?.RequestId,
        MessageTypes.FileSearchRequest => message.FileSearchRequest?.RequestId,
        MessageTypes.FileManifestRequest => message.FileManifestRequest?.RequestId,
        MessageTypes.FileMetadataRequest => message.FileMetadataRequest?.RequestId,
        MessageTypes.FileThumbnailRequest => message.FileThumbnailRequest?.RequestId,
        MessageTypes.FileManageRequest => message.FileManageRequest?.RequestId,
        _ => null,
    };

    /// <summary>The request id a REPLY answers; null for roots, which has none.</summary>
    internal static string? ReplyRequestId(RemexMessage message) => message.Type switch
    {
        MessageTypes.FileBrowseResponse => message.FileBrowseResponse?.RequestId,
        MessageTypes.FileVolumesResponse => message.FileVolumesResponse?.RequestId,
        MessageTypes.FileSearchResponse => message.FileSearchResponse?.RequestId,
        MessageTypes.FileManifestResponse => message.FileManifestResponse?.RequestId,
        MessageTypes.FileMetadataResponse => message.FileMetadataResponse?.RequestId,
        MessageTypes.FileThumbnailResponse => message.FileThumbnailResponse?.RequestId,
        MessageTypes.FileManageResponse => message.FileManageResponse?.RequestId,
        _ => null,
    };

    /// <summary>Whether a relayed request carries the body its type promises.</summary>
    private static bool HasBody(RemexMessage message) => message.Type switch
    {
        MessageTypes.FileRootsRequest => true,
        MessageTypes.FileBrowseRequest => message.FileBrowseRequest is not null,
        MessageTypes.FileVolumesRequest => message.FileVolumesRequest is not null,
        MessageTypes.FileSearchRequest => message.FileSearchRequest is not null,
        MessageTypes.FileManifestRequest => message.FileManifestRequest is not null,
        MessageTypes.FileMetadataRequest => message.FileMetadataRequest is not null,
        MessageTypes.FileThumbnailRequest => message.FileThumbnailRequest is not null,
        MessageTypes.FileManageRequest => message.FileManageRequest is not null,
        _ => false,
    };

    /// <summary>
    /// A reply this PC writes itself, so a request the phone can no longer answer fails with a reason
    /// instead of hanging. Every reply type has an <c>errorMessage</c>, and
    /// <see cref="FileTransferClient"/> already turns one into a failure.
    /// </summary>
    internal static RemexMessage FailureReply(string requestType, string requestId, string reason) => requestType switch
    {
        MessageTypes.FileRootsRequest => new RemexMessage
        {
            Type = MessageTypes.FileRootsResponse,
            FileRootsResponse = new FileRootsResponse { Roots = [], ErrorMessage = reason },
        },
        MessageTypes.FileBrowseRequest => new RemexMessage
        {
            Type = MessageTypes.FileBrowseResponse,
            FileBrowseResponse = new FileBrowseResponse { RequestId = requestId, Entries = [], ErrorMessage = reason },
        },
        MessageTypes.FileVolumesRequest => new RemexMessage
        {
            Type = MessageTypes.FileVolumesResponse,
            FileVolumesResponse = new FileVolumesResponse { RequestId = requestId, Volumes = [], ErrorMessage = reason },
        },
        MessageTypes.FileSearchRequest => new RemexMessage
        {
            Type = MessageTypes.FileSearchResponse,
            FileSearchResponse = new FileSearchResponse { RequestId = requestId, Entries = [], ErrorMessage = reason },
        },
        MessageTypes.FileManifestRequest => new RemexMessage
        {
            Type = MessageTypes.FileManifestResponse,
            FileManifestResponse = new FileManifestResponse { RequestId = requestId, Entries = [], ErrorMessage = reason },
        },
        MessageTypes.FileMetadataRequest => new RemexMessage
        {
            Type = MessageTypes.FileMetadataResponse,
            FileMetadataResponse = new FileMetadataResponse { RequestId = requestId, ErrorMessage = reason },
        },
        MessageTypes.FileManageRequest => new RemexMessage
        {
            Type = MessageTypes.FileManageResponse,
            FileManageResponse = new FileManageResponse { RequestId = requestId, Success = false, ErrorMessage = reason },
        },
        _ => new RemexMessage
        {
            Type = MessageTypes.FileThumbnailResponse,
            FileThumbnailResponse = new FileThumbnailResponse { RequestId = requestId, ErrorMessage = reason },
        },
    };

    /// <summary>
    /// Why a <c>file_manage_request</c> may not be sent, or null when it may. Pure, so every refusal is
    /// testable without a socket. The operation must be one of the five, the root must be named, and
    /// every name and path must pass <see cref="FilePathValidation"/>'s remote rules — the same ones the
    /// phone applies.
    /// </summary>
    internal static string? ManageRefusal(FileManageRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RootId))
            return "a root id is required";

        if (!FilePathValidation.IsValidRemoteRelativePath(request.RelativePath, out var pathError))
            return $"relativePath: {pathError}";

        var isRootItself = request.RelativePath.Trim('/').Length == 0;

        switch (request.Operation)
        {
            case FileManageOperations.Delete:
                // The shared folder itself is not "a file in it": the root is the person's, and the
                // phone's Remove-folder switch is the only way it goes.
                return isRootItself ? "the shared folder itself cannot be deleted" : null;

            case FileManageOperations.Rename:
                if (isRootItself)
                    return "the shared folder itself cannot be renamed";
                return FilePathValidation.IsValidRemoteName(request.NewName, out var renameError)
                    ? null : $"newName: {renameError}";

            case FileManageOperations.Mkdir:
                return FilePathValidation.IsValidRemoteName(request.NewName, out var mkdirError)
                    ? null : $"newName: {mkdirError}";

            case FileManageOperations.Copy:
            case FileManageOperations.Move:
                if (isRootItself)
                    return "the shared folder itself cannot be copied or moved";
                if (string.IsNullOrWhiteSpace(request.DestinationPath)
                    || request.DestinationPath.Trim('/').Length == 0)
                {
                    return "a destination is required";
                }
                return FilePathValidation.IsValidRemoteRelativePath(request.DestinationPath, out var destError)
                    ? null : $"destinationPath: {destError}";

            default:
                return "that operation is not one the phone is sent";
        }
    }

    /// <summary>One browsing connection to one phone.</summary>
    private sealed class Connection(PhoneFileRelay relay, string clientId) : IPhoneFileConnection
    {
        private readonly ConcurrentDictionary<(string Type, string Id), PendingRequest> _pending = new();
        private int _closed;
        private int _disconnectRaised;
        private volatile bool _phoneAllowsChanges;

        public Guid Id { get; } = Guid.NewGuid();

        public string ClientId { get; } = clientId;

        public bool IsConnected => Volatile.Read(ref _closed) == 0 && relay.IsReachable(ClientId);

        public event Action<RemexMessage>? FileTransferMessageReceived;

        public event Action? Disconnected;

        public async Task SendAsync(RemexMessage message)
        {
            Guard.NotNull(message);

            if (Volatile.Read(ref _closed) != 0)
                throw new PhoneNotConnectedException();

            // THE ALLOWLIST IS CHECKED HERE, ON THE ONE PATH TO THE WIRE, so no caller can widen it.
            if (!RelayedRequests.Contains(message.Type) || !HasBody(message))
            {
                relay._logger.LogWarning("Refused to send {Type} to a phone: it is not a read-only browsing request.", message.Type);
                throw new PhoneFileRequestRefusedException($"{message.Type} is not sent to a phone.");
            }

            var requestId = RequestId(message);
            if (message.Type != MessageTypes.FileRootsRequest && string.IsNullOrWhiteSpace(requestId))
                throw new PhoneFileRequestRefusedException($"{message.Type} needs a request id before it can be sent to a phone.");

            var isCopyOrMove = false;
            if (message.Type == MessageTypes.FileManageRequest)
            {
                // THE PHONE MUST HAVE SAID, ON ITS OWN ROOTS REPLY, THAT THE PERSON ALLOWS THIS. A phone
                // that predates the switch never says it, so it reads as "no".
                if (!_phoneAllowsChanges)
                {
                    relay._logger.LogWarning(
                        "Refused to send {Operation} to {ClientId}: that phone has not said it lets the PC change files.",
                        message.FileManageRequest!.Operation, LogRedaction.RedactClientId(ClientId));
                    throw new PhoneChangesNotAllowedException();
                }

                if (ManageRefusal(message.FileManageRequest!) is { } refusal)
                {
                    relay._logger.LogWarning("Refused to send a manage request to a phone: {Reason}.", refusal);
                    throw new PhoneFileRequestRefusedException($"file_manage_request is not sent to a phone: {refusal}.");
                }

                isCopyOrMove = message.FileManageRequest!.Operation is FileManageOperations.Copy or FileManageOperations.Move;
            }

            if (!relay.IsReachable(ClientId))
                throw new PhoneNotConnectedException();

            var key = (message.Type, requestId ?? string.Empty);
            var pending = new PendingRequest(isCopyOrMove ? relay.CopyMoveTimeout : relay.RequestTimeout);
            if (_pending.TryRemove(key, out var superseded))
                superseded.Dispose();
            _pending[key] = pending;

            // Armed after registration, so a timeout can only ever find its own entry.
            pending.Arm(() =>
            {
                if (_pending.TryRemove(new KeyValuePair<(string, string), PendingRequest>(key, pending)))
                {
                    pending.Dispose();
                    relay._logger.LogWarning("{Type} to {ClientId} got no answer in time.", key.Type, LogRedaction.RedactClientId(ClientId));
                    Deliver(FailureReply(key.Type, key.Item2, LocalizationService.Instance["FileTransfer_PhoneNoAnswer"]));
                }
            });

            // NO CLIENT ID ON THE WAY OUT. The PC's UI never stamps one, and the relay must not either:
            // a request that named a device would be a claim made on the PC's behalf.
            var outbound = message with { ClientId = null };

            bool sent;
            try
            {
                sent = await relay._sessions.TrySendAsync(ClientId, outbound, CancellationToken.None);
            }
            catch
            {
                Forget(key, pending);
                throw;
            }

            if (!sent)
            {
                Forget(key, pending);
                throw new PhoneNotConnectedException();
            }
        }

        public bool TryComplete(string requestType, string requestId, RemexMessage reply)
        {
            if (Volatile.Read(ref _closed) != 0)
                return false;
            if (!_pending.TryRemove((requestType, requestId), out var pending))
                return false;

            pending.Dispose();

            // THE PHONE'S OWN WORDS ON WHETHER THE PC MAY CHANGE ITS FILES, re-read on every roots reply
            // so a person who flips the switch and refreshes is believed in both directions. A failure
            // reply this PC wrote itself (no answer, phone gone) carries no capabilities and reads as
            // "not allowed", which is the safe side.
            if (reply.Type == MessageTypes.FileRootsResponse)
                _phoneAllowsChanges = reply.FileRootsResponse?.FileCapabilities?.PcChanges == true;

            Deliver(reply);
            return true;
        }

        /// <summary>
        /// The phone's session is gone: say so once, then fail everything still waiting so nothing
        /// sits out its timeout for an answer that cannot come.
        /// </summary>
        public void OnPhoneGone()
        {
            Interlocked.Exchange(ref _closed, 1);

            if (Interlocked.Exchange(ref _disconnectRaised, 1) == 0)
            {
                try
                {
                    Disconnected?.Invoke();
                }
                catch (Exception ex)
                {
                    relay._logger.LogWarning(ex, "A phone-disconnected subscriber threw.");
                }
            }

            var reason = LocalizationService.Instance["FileTransfer_PhoneDisconnected"];
            foreach (var key in _pending.Keys)
            {
                if (_pending.TryRemove(key, out var pending))
                {
                    pending.Dispose();
                    Deliver(FailureReply(key.Type, key.Id, reason));
                }
            }
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _closed, 1);
            relay._connections.TryRemove(Id, out _);
            foreach (var key in _pending.Keys)
            {
                if (_pending.TryRemove(key, out var pending))
                    pending.Dispose();
            }
        }

        private void Forget((string Type, string Id) key, PendingRequest pending)
        {
            if (_pending.TryRemove(new KeyValuePair<(string, string), PendingRequest>(key, pending)))
                pending.Dispose();
        }

        private void Deliver(RemexMessage reply)
        {
            try
            {
                FileTransferMessageReceived?.Invoke(reply);
            }
            catch (Exception ex)
            {
                // Delivered on the phone's reader loop: a subscriber's fault must not end the session.
                relay._logger.LogWarning(ex, "A phone file-reply subscriber threw.");
            }
        }
    }

    /// <summary>A relayed request still waiting for the phone, with its timeout.</summary>
    private sealed class PendingRequest(TimeSpan timeout) : IDisposable
    {
        private readonly CancellationTokenSource _timeout = new();
        private CancellationTokenRegistration _registration;

        public void Arm(Action onTimeout)
        {
            try
            {
                _registration = _timeout.Token.Register(onTimeout);
                _timeout.CancelAfter(timeout);
            }
            catch (ObjectDisposedException)
            {
                // Answered or superseded before the timer was even armed: nothing left to time out.
            }
        }

        public void Dispose()
        {
            _registration.Dispose();
            _timeout.Dispose();
        }
    }
}
