using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading.Channels;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Desktop.ViewModels;

namespace Remex.Desktop.Services.FileTransfer;

/// <summary>
/// Client-side file transfer service. Uses the existing WebSocket connection
/// in <see cref="ConnectionViewModel"/> to send/receive file transfer messages.
/// </summary>
public sealed class FileTransferClient : IDisposable
{
    private readonly ConnectionViewModel _connection;
    private TaskCompletionSource<RemexMessage>? _rootsWaiter;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RemexMessage>> _browseWaiters = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RemexMessage>> _transferEndWaiters = new();
    private readonly ConcurrentDictionary<string, IProgress<double>?> _progressReporters = new();
    private readonly ConcurrentDictionary<string, Channel<byte[]>> _downloadChannels = new();

    /// <summary>
    /// Bytes accepted for a download but not yet written to disk, per transfer.
    /// </summary>
    /// <remarks>
    /// A separate dictionary rather than a field on the channel because the producer and the writer
    /// live in different methods and only share a transfer id — the same reason the hashers and the
    /// end-waiters are kept this way. Reaped in the same finally as all of them.
    /// </remarks>
    private readonly ConcurrentDictionary<string, StrongBox<long>> _downloadBacklogBytes = new();
    private readonly ConcurrentDictionary<string, IncrementalHash> _downloadHashers = new();

    /// <summary>
    /// Ceiling on unwritten download backlog before the transfer is abandoned (RemEx-gyf4).
    /// </summary>
    /// <remarks>
    /// Chosen to be far above any transient stall — a disk pausing for a second at 100 MB/s queues
    /// a tenth of this — and far below the point where the process is in trouble. It does NOT rescue
    /// a destination that is persistently slower than the connection: nothing can, without flow
    /// control in the protocol, because the backlog then grows without limit by construction. What
    /// it buys is that such a transfer fails in seconds with a message naming the cause, instead of
    /// consuming RAM proportional to the whole speed difference and taking the application with it.
    /// </remarks>
    internal const long MaxQueuedDownloadBytes = 256L * 1024 * 1024;


    /// <summary>
    /// Fails a transfer whose peer has gone silent, without bounding how long a large transfer
    /// may legitimately take. See <see cref="TransferIdleWatchdog"/> for why silence rather than
    /// total duration is the right measure, and for the insert-on-receive leak it guards.
    /// </summary>
    private readonly TransferIdleWatchdog _idleWatchdog = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RemexMessage>> _manageWaiters = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RemexMessage>> _hashWaiters = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RemexMessage>> _rootManageWaiters = new();
    // ── 2.1 File Sharing Overhaul (protocolVersion 3) response waiters ──
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RemexMessage>> _volumesWaiters = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RemexMessage>> _searchWaiters = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RemexMessage>> _metadataWaiters = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RemexMessage>> _thumbnailWaiters = new();

    /// <summary>
    /// v3 file-transfer capabilities advertised by the connected host in the most recent
    /// <c>file_roots_response</c>. Null for a v2 host (or before roots are first fetched). The UI gates
    /// every v3 feature on this so a v3 message is never sent to a v2 peer (plan §1.5).
    /// </summary>
    public FileCapabilities? Capabilities { get; private set; }

    public FileTransferClient(ConnectionViewModel connection)
    {
        _connection = connection;
        _connection.FileTransferMessageReceived += OnFileTransferMessage;
    }

    /// <summary>
    /// Sends <paramref name="request"/> and awaits the reply on <paramref name="tcs"/>, bounded by
    /// <paramref name="timeout"/> when given (unbounded when null, for operations whose duration
    /// scales with data size — see <see cref="ManageAsync"/>). <paramref name="removeWaiter"/> is
    /// invoked on every exit path via <c>finally</c>, including when <c>SendAsync</c> itself throws.
    /// Every request/response helper in this class registers its waiter before sending; without this,
    /// a send failure (e.g. the socket dropped mid-request) left the waiter registered forever with
    /// nobody left to complete it, so any later caller awaiting that request id — or GC pressure from
    /// the leaked dictionary entries — hung or degraded silently (RemEx-jxmf).
    /// </summary>
    private async Task<RemexMessage> SendAndAwaitReplyAsync(
        RemexMessage request,
        TaskCompletionSource<RemexMessage> tcs,
        Action removeWaiter,
        TimeSpan? timeout,
        CancellationToken ct)
    {
        try
        {
            await _connection.SendAsync(request);
            return timeout.HasValue
                ? await tcs.Task.WaitAsync(timeout.Value, ct)
                : await tcs.Task;
        }
        finally
        {
            removeWaiter();
        }
    }

    public async Task<IReadOnlyList<FileSharedRoot>> ListRemoteRootsAsync(CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<RemexMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _rootsWaiter = tcs;

        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

        var response = await SendAndAwaitReplyAsync(
            new RemexMessage
            {
                Type = MessageTypes.FileRootsRequest,
                FileRootsRequest = new FileRootsRequest()
            },
            tcs,
            () =>
            {
                if (ReferenceEquals(_rootsWaiter, tcs))
                    _rootsWaiter = null;
            },
            TimeSpan.FromSeconds(ControlRequestTimeoutSeconds),
            ct);

        // Capture the v3 capability handshake (additive field; null for v2 hosts). Roots are always
        // fetched first, so this doubles as the negotiation — no separate handshake message.
        Capabilities = response.FileRootsResponse?.FileCapabilities;

        if (response.FileRootsResponse?.ErrorMessage is string err && !string.IsNullOrWhiteSpace(err))
            throw FileTransferHostException.ForHostError(err, $"Root listing error: {err}");

        return response.FileRootsResponse?.Roots ?? [];
    }

    /// <summary>True when the connected host advertises the v3 file-manager protocol (copy/move/mkdir/
    /// search/metadata/thumbnail on <c>/ws</c>). Gates every v3 message so none is sent to a v2 peer.</summary>
    public bool SupportsV3 => (Capabilities?.Protocol ?? 0) >= 3;

    /// <summary>True when the host advertises a given file-manager op in its capability set.</summary>
    public bool SupportsOp(string op) =>
        Capabilities?.Ops is { } ops && System.Array.IndexOf(ops, op) >= 0;

    /// <summary>True when full-device (volume) browsing may be offered for this host.</summary>
    public bool SupportsFullBrowse => SupportsV3 && Capabilities?.FullBrowse == true;

    public async Task<IReadOnlyList<FileEntry>> BrowseRemoteAsync(string rootId, string relativePath, CancellationToken ct)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<RemexMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _browseWaiters[requestId] = tcs;

        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

        var response = await SendAndAwaitReplyAsync(
            new RemexMessage
            {
                Type = MessageTypes.FileBrowseRequest,
                FileBrowseRequest = new FileBrowseRequest
                {
                    RequestId = requestId,
                    Path = relativePath,
                    RootId = rootId,
                    RelativePath = relativePath,
                }
            },
            tcs,
            () => _browseWaiters.TryRemove(requestId, out _),
            TimeSpan.FromSeconds(ControlRequestTimeoutSeconds),
            ct);

        if (response.FileBrowseResponse?.ErrorMessage is string err)
            throw new IOException($"Browse error: {err}");

        return response.FileBrowseResponse?.Entries ?? [];
    }

    public async Task DeleteRemoteAsync(string rootId, string relativePath, CancellationToken ct)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<RemexMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _manageWaiters[requestId] = tcs;
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

        var response = await SendAndAwaitReplyAsync(
            new RemexMessage
            {
                Type = MessageTypes.FileManageRequest,
                FileManageRequest = new FileManageRequest { RequestId = requestId, RootId = rootId, RelativePath = relativePath, Operation = "delete" }
            },
            tcs,
            () => _manageWaiters.TryRemove(requestId, out _),
            TimeSpan.FromSeconds(ManageRequestTimeoutSeconds),
            ct);

        if (response.FileManageResponse?.Success == false)
            throw new IOException($"Delete failed: {response.FileManageResponse.ErrorMessage}");
    }

    public async Task RenameRemoteAsync(string rootId, string relativePath, string newName, CancellationToken ct)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<RemexMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _manageWaiters[requestId] = tcs;
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

        var response = await SendAndAwaitReplyAsync(
            new RemexMessage
            {
                Type = MessageTypes.FileManageRequest,
                FileManageRequest = new FileManageRequest { RequestId = requestId, RootId = rootId, RelativePath = relativePath, Operation = "rename", NewName = newName }
            },
            tcs,
            () => _manageWaiters.TryRemove(requestId, out _),
            TimeSpan.FromSeconds(ManageRequestTimeoutSeconds),
            ct);

        if (response.FileManageResponse?.Success == false)
            throw new IOException($"Rename failed: {response.FileManageResponse.ErrorMessage}");
    }

    /// <summary>
    /// Seconds to wait for a peer to answer a <see cref="MessageTypes.FileHashRequest"/>.
    /// Deliberately generous: hashing is CPU-bound on the peer and a large file can take a
    /// while. The bound exists to stop an indefinite hang, not to enforce responsiveness —
    /// the Android client has no handler for this request at all today (RemEx-0e54), so
    /// without it the UI waited forever with IsLoading stuck on.
    /// </summary>
    private const int HashRequestTimeoutSeconds = 30;

    /// <summary>
    /// Bounds for awaiting a peer reply, in seconds, split by operation class rather than
    /// shared. A single value would be wrong at both ends: too short turns a legitimate
    /// recursive search or large delete into a false failure, too long leaves the UI
    /// spinning on a control request. Before these existed every helper awaited its
    /// TaskCompletionSource unbounded, so a peer with no handler for the request hung the
    /// view model forever (RemEx-q2bu; RemEx-0e54 was one instance of it).
    /// </summary>
    private const int ControlRequestTimeoutSeconds = 30;

    /// <inheritdoc cref="ControlRequestTimeoutSeconds"/>
    private const int ManageRequestTimeoutSeconds = 60;

    /// <inheritdoc cref="ControlRequestTimeoutSeconds"/>
    private const int SearchRequestTimeoutSeconds = 120;

    public async Task<string> VerifyRemoteHashAsync(string rootId, string relativePath, CancellationToken ct)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<RemexMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _hashWaiters[requestId] = tcs;
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

        // Bounded wait, matching ConnectionViewModel's command pattern. A peer that never
        // answers has to surface a failure the caller can show, not hang the view model.
        // The waiter is reaped on every exit (including a throw from SendAsync itself) by
        // SendAndAwaitReplyAsync — the previous code removed it only after a successful
        // await, so each timeout, cancellation, or send failure leaked an entry.
        var response = await SendAndAwaitReplyAsync(
            new RemexMessage
            {
                Type = MessageTypes.FileHashRequest,
                FileHashRequest = new FileHashRequest { RequestId = requestId, RootId = rootId, RelativePath = relativePath }
            },
            tcs,
            () => _hashWaiters.TryRemove(requestId, out _),
            TimeSpan.FromSeconds(HashRequestTimeoutSeconds),
            ct);

        if (response.FileHashResponse?.ErrorMessage is string err)
            throw new IOException($"Hash verification failed: {err}");

        return response.FileHashResponse?.Sha256Base64 ?? string.Empty;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 2.1 File Sharing Overhaul (protocolVersion 3) — file-manager ops on /ws.
    // Field names/type-strings mirror remex.core verbatim (RemexMessage / FileTransferMessages).
    // Callers must gate on Capabilities so these are never sent to a v2 host.
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>Copies <paramref name="relativePath"/> to <paramref name="destinationRelativePath"/> within the same root.</summary>
    public Task CopyRemoteAsync(string rootId, string relativePath, string destinationRelativePath, bool overwrite, CancellationToken ct)
        => ManageAsync(rootId, relativePath, Remex.Core.Models.FileManageOperations.Copy, newName: null, destinationRelativePath, overwrite, ct);

    /// <summary>Moves <paramref name="relativePath"/> to <paramref name="destinationRelativePath"/> within the same root.</summary>
    public Task MoveRemoteAsync(string rootId, string relativePath, string destinationRelativePath, bool overwrite, CancellationToken ct)
        => ManageAsync(rootId, relativePath, Remex.Core.Models.FileManageOperations.Move, newName: null, destinationRelativePath, overwrite, ct);

    /// <summary>Creates a new folder named <paramref name="folderName"/> under <paramref name="parentRelativePath"/> (wire: RelativePath=parent, NewName=folder).</summary>
    public Task MakeDirectoryRemoteAsync(string rootId, string parentRelativePath, string folderName, CancellationToken ct)
        => ManageAsync(rootId, parentRelativePath, Remex.Core.Models.FileManageOperations.Mkdir, newName: folderName, destinationPath: null, overwrite: false, ct);

    private async Task ManageAsync(string rootId, string relativePath, string operation, string? newName, string? destinationPath, bool overwrite, CancellationToken ct)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<RemexMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _manageWaiters[requestId] = tcs;
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

        // mkdir answers immediately, but copy and move stream the whole file before the peer
        // replies, so their duration scales with FILE SIZE. A wall-clock bound on those would
        // abort a large but healthy paste and — worse — reap the waiter, so the peer's eventual
        // success reply is dropped and the user is told the paste failed while the file appears
        // anyway. Those two need an idle watchdog rather than a deadline (RemEx-l519).
        var scalesWithFileSize =
            operation == FileManageOperations.Copy || operation == FileManageOperations.Move;

        var response = await SendAndAwaitReplyAsync(
            new RemexMessage
            {
                Type = MessageTypes.FileManageRequest,
                FileManageRequest = new FileManageRequest
                {
                    RequestId = requestId,
                    RootId = rootId,
                    RelativePath = relativePath,
                    Operation = operation,
                    NewName = newName,
                    DestinationPath = destinationPath,
                    Overwrite = overwrite,
                }
            },
            tcs,
            () => _manageWaiters.TryRemove(requestId, out _),
            scalesWithFileSize ? null : TimeSpan.FromSeconds(ManageRequestTimeoutSeconds),
            ct);

        if (response.FileManageResponse?.Success == false)
            throw FileTransferHostException.ForHostError(
                response.FileManageResponse.ErrorMessage,
                $"{operation} failed without the host giving a reason.");
    }

    /// <summary>Bounded recursive search under a root subtree. Returns hits plus whether results were capped.</summary>
    public async Task<(IReadOnlyList<FileSearchEntry> Entries, bool Truncated)> SearchRemoteAsync(
        string rootId, string? relativePath, string query, int maxResults, CancellationToken ct)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<RemexMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _searchWaiters[requestId] = tcs;
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

        var response = await SendAndAwaitReplyAsync(
            new RemexMessage
            {
                Type = MessageTypes.FileSearchRequest,
                FileSearchRequest = new FileSearchRequest
                {
                    RequestId = requestId,
                    RootId = rootId,
                    RelativePath = relativePath,
                    Query = query,
                    MaxResults = maxResults,
                }
            },
            tcs,
            () => _searchWaiters.TryRemove(requestId, out _),
            TimeSpan.FromSeconds(SearchRequestTimeoutSeconds),
            ct);

        if (response.FileSearchResponse?.ErrorMessage is string err && !string.IsNullOrWhiteSpace(err))
            throw new IOException($"Search error: {err}");

        var resp = response.FileSearchResponse;
        return (resp?.Entries ?? [], resp?.Truncated ?? false);
    }

    /// <summary>Detailed metadata (size, timestamps, item count, mime, read-only) for a single item.</summary>
    public async Task<FileMetadataResponse> GetMetadataRemoteAsync(string rootId, string relativePath, CancellationToken ct)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<RemexMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _metadataWaiters[requestId] = tcs;
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

        var response = await SendAndAwaitReplyAsync(
            new RemexMessage
            {
                Type = MessageTypes.FileMetadataRequest,
                FileMetadataRequest = new FileMetadataRequest { RequestId = requestId, RootId = rootId, RelativePath = relativePath }
            },
            tcs,
            () => _metadataWaiters.TryRemove(requestId, out _),
            TimeSpan.FromSeconds(ControlRequestTimeoutSeconds),
            ct);

        if (response.FileMetadataResponse is not { } meta)
            throw new IOException("No metadata response received.");
        if (meta.ErrorMessage is string err && !string.IsNullOrWhiteSpace(err))
            throw FileTransferHostException.ForHostError(err, $"Metadata error: {err}");
        return meta;
    }

    /// <summary>Requests a small base64 JPEG thumbnail (images only in v1). Returns null when unavailable.</summary>
    public async Task<string?> GetThumbnailRemoteAsync(string rootId, string relativePath, int maxDim, CancellationToken ct)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<RemexMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _thumbnailWaiters[requestId] = tcs;
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

        var response = await SendAndAwaitReplyAsync(
            new RemexMessage
            {
                Type = MessageTypes.FileThumbnailRequest,
                FileThumbnailRequest = new FileThumbnailRequest { RequestId = requestId, RootId = rootId, RelativePath = relativePath, MaxDim = maxDim }
            },
            tcs,
            () => _thumbnailWaiters.TryRemove(requestId, out _),
            TimeSpan.FromSeconds(ControlRequestTimeoutSeconds),
            ct);

        return response.FileThumbnailResponse?.JpegBase64;
    }

    /// <summary>Enumerates the host's mounted volumes/drives once full-browse consent is granted (plan §1.2).</summary>
    public async Task<(IReadOnlyList<FileVolumeInfo> Volumes, bool FullBrowseGranted)> ListVolumesAsync(CancellationToken ct)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<RemexMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _volumesWaiters[requestId] = tcs;
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

        var response = await SendAndAwaitReplyAsync(
            new RemexMessage
            {
                Type = MessageTypes.FileVolumesRequest,
                FileVolumesRequest = new FileVolumesRequest { RequestId = requestId }
            },
            tcs,
            () => _volumesWaiters.TryRemove(requestId, out _),
            TimeSpan.FromSeconds(ControlRequestTimeoutSeconds),
            ct);

        var resp = response.FileVolumesResponse;
        if (resp?.ErrorMessage is string err && !string.IsNullOrWhiteSpace(err))
            throw FileTransferHostException.ForHostError(err, $"Volumes error: {err}");
        return (resp?.Volumes ?? [], resp?.FullBrowseGranted ?? false);
    }

    public async Task<IReadOnlyList<FileSharedRoot>> AddRemoteRootAsync(string sourceRootId, string sourceRelativePath, CancellationToken ct)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<RemexMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _rootManageWaiters[requestId] = tcs;
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

        var response = await SendAndAwaitReplyAsync(
            new RemexMessage
            {
                Type = MessageTypes.FileRootManageRequest,
                FileRootManageRequest = new FileRootManageRequest { RequestId = requestId, Operation = "add", SourceRootId = sourceRootId, SourceRelativePath = sourceRelativePath }
            },
            tcs,
            () => _rootManageWaiters.TryRemove(requestId, out _),
            TimeSpan.FromSeconds(ControlRequestTimeoutSeconds),
            ct);

        if (response.FileRootManageResponse?.ErrorMessage is string err)
            throw FileTransferHostException.ForHostError(err, "Add root failed with an empty host message.");

        return response.FileRootManageResponse?.Roots ?? [];
    }

    public async Task<IReadOnlyList<FileSharedRoot>> RemoveRemoteRootAsync(string rootId, CancellationToken ct)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<RemexMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _rootManageWaiters[requestId] = tcs;
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

        var response = await SendAndAwaitReplyAsync(
            new RemexMessage
            {
                Type = MessageTypes.FileRootManageRequest,
                FileRootManageRequest = new FileRootManageRequest { RequestId = requestId, Operation = "remove", RootId = rootId }
            },
            tcs,
            () => _rootManageWaiters.TryRemove(requestId, out _),
            TimeSpan.FromSeconds(ControlRequestTimeoutSeconds),
            ct);

        if (response.FileRootManageResponse?.ErrorMessage is string err)
            throw FileTransferHostException.ForHostError(err, "Remove root failed with an empty host message.");

        return response.FileRootManageResponse?.Roots ?? [];
    }

    public async Task UploadAsync(string localPath, string remoteRootId, string remoteRelativePath, IProgress<double>? progress, CancellationToken ct)
    {
        var transferId = Guid.NewGuid().ToString("N");

        await using var fileStream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        var totalBytes = fileStream.Length;

        var tcs = new TaskCompletionSource<RemexMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _transferEndWaiters[transferId] = tcs;
        _progressReporters[transferId] = progress;
        var lastActivity = _idleWatchdog.Begin(transferId);

        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

        // Registration through the final await is wrapped in try/finally so a throw from ANY of
        // the sends below (Start, a chunk, or End) — not just the request/response shape
        // SendAndAwaitReplyAsync covers — still reaps both dictionaries. Upload streams N chunks
        // before its single reply, so it cannot adopt SendAndAwaitReplyAsync directly, but it must
        // reap on every exit path the same way (RemEx-w9lj): before this fix, a mid-transfer send
        // failure left the waiter and progress reporter stranded forever.
        try
        {
            // Send Start with empty hash — hash is computed incrementally and sent in End
            await _connection.SendAsync(new RemexMessage
            {
                Type = MessageTypes.FileTransferStart,
                FileTransferStart = new FileTransferStart
                {
                    TransferId = transferId,
                    Direction = "upload",
                    RemotePath = remoteRelativePath,
                    RemoteRootId = remoteRootId,
                    RemoteRelativePath = remoteRelativePath,
                    FileName = Path.GetFileName(localPath),
                    TotalBytes = totalBytes,
                    Sha256Base64 = string.Empty
                }
            });

            const int chunkSize = 65536;
            var buffer = new byte[chunkSize];
            int read;
            long offset = 0;

            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            while ((read = await fileStream.ReadAsync(buffer.AsMemory(0, chunkSize), ct)) > 0)
            {
                hasher.AppendData(buffer, 0, read);
                await _connection.SendAsync(new RemexMessage
                {
                    Type = MessageTypes.FileTransferChunk,
                    FileTransferChunk = new FileTransferChunk
                    {
                        TransferId = transferId,
                        Offset = offset,
                        DataBase64 = Convert.ToBase64String(buffer.AsSpan(0, read))
                    }
                });
                offset += read;

                // Our own send counts as activity too. The peer only reports progress every
                // ProgressChunkInterval chunks, so a file smaller than that interval draws no
                // peer traffic at all - without this the watchdog for a small upload would be
                // measured from registration rather than from the last thing that happened.
                Volatile.Write(ref lastActivity.Value, Stopwatch.GetTimestamp());
            }

            var sha256Base64 = Convert.ToBase64String(hasher.GetCurrentHash());

            await _connection.SendAsync(new RemexMessage
            {
                Type = MessageTypes.FileTransferEnd,
                FileTransferEnd = new FileTransferEnd { TransferId = transferId, Success = true, Sha256Base64 = sha256Base64 }
            });

            var result = await _idleWatchdog.AwaitCompletionAsync(tcs, lastActivity, ct);

            if (result.FileTransferEnd?.Success == false)
                throw FileTransferHostException.ForHostError(
                    result.FileTransferEnd.ErrorMessage,
                    $"Upload failed: {result.FileTransferEnd.ErrorMessage}");
        }
        finally
        {
            _transferEndWaiters.TryRemove(transferId, out _);
            _progressReporters.TryRemove(transferId, out _);
            _idleWatchdog.End(transferId);
        }
    }

    public async Task DownloadAsync(string remoteRootId, string remoteRelativePath, string localPath, IProgress<double>? progress, CancellationToken ct)
    {
        var transferId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<RemexMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _transferEndWaiters[transferId] = tcs;
        _progressReporters[transferId] = progress;
        var lastActivity = _idleWatchdog.Begin(transferId);

        using var reg = ct.Register(() =>
        {
            tcs.TrySetCanceled(ct);
            // Cannot be awaited - this runs inside ct.Register - but a failure matters: if the
            // cancel never reaches the peer it keeps streaming a transfer the user stopped.
            _connection.SendAsync(new RemexMessage
            {
                Type = MessageTypes.FileTransferCancel,
                FileTransferCancel = new FileTransferCancel { TransferId = transferId }
            }).FireAndForget($"send cancel for transfer {transferId}");
        });

        await using var fileStream = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);

        // Unbounded channel ensures chunk ordering: the consumer writes sequentially,
        // eliminating the race condition that would exist with fire-and-forget WriteAsync.
        var channel = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        _downloadChannels[transferId] = channel;
        var backlogBytes = new StrongBox<long>(0);
        _downloadBacklogBytes[transferId] = backlogBytes;
        var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        _downloadHashers[transferId] = hasher;

        // Cancellable so an abandoned transfer can stop the writer without draining: completing the
        // channel instead makes ReadAllAsync flush everything still queued, which on the backlog path
        // means writing the whole ceiling to the very disk that could not keep up, into a file about
        // to be deleted.
        //
        // DELIBERATELY NOT LINKED TO ct, and that is the whole safety argument. Linked, a ct fired
        // during the FINAL drain stops the writer mid-flush — and nothing downstream notices, because
        // the digest is accumulated from bytes as they are RECEIVED, never re-read from the file. The
        // integrity check would compare a complete hash against a truncated file and pass, and the
        // queue would mark the transfer Done: a short file under the user's chosen name, reported as
        // a finished download. Only DiscardPartialFileAsync may cancel this, so a successful drain
        // cannot be interrupted.
        using var writerCts = new CancellationTokenSource();
        var writeTask = Task.Run(async () =>
        {
            await foreach (var data in channel.Reader.ReadAllAsync(writerCts.Token))
            {
                await fileStream.WriteAsync(data, writerCts.Token);
                // Retire the bytes only once they are actually on the stream, so the backlog
                // measures what is genuinely outstanding rather than what has been dequeued.
                RetireBacklog(backlogBytes, data.Length);
            }
        }, writerCts.Token);

        // Every failure exit deletes the partial download through here — driven from the finally so
        // a new exit cannot forget to call it.
        async Task DiscardPartialFileAsync()
        {
            await writerCts.CancelAsync();
            try { await writeTask; } catch { /* stopping it IS the point; nothing to report */ }

            // THE FILE MUST BE CLOSED BEFORE IT CAN BE DELETED. It is opened FileShare.None, so a
            // File.Delete while the stream is still alive throws a sharing violation — and the
            // best-effort catch then swallowed it, leaving a truncated file under the FINAL name
            // looking complete. That applied to the host-error and integrity paths too, so this is a
            // pre-existing bug the RemEx-gyf4 review turned up rather than one the backlog ceiling
            // introduced. ON WINDOWS: Linux maps FileShare to an advisory flock and unlink ignores
            // it, so the delete already succeeded there (leaving the stream writing to an unlinked
            // inode). Disposing twice is safe; the await using below disposes again.
            // Best-effort like the delete below it, and for the same reason: DisposeAsync FLUSHES,
            // and the realistic trigger is the exact scenario this feature exists for — a full or
            // yanked USB stick. Unguarded it would skip the delete and replace the caller's real
            // exception, so a "destination too slow" abandon would surface as a generic failure.
            try { await fileStream.DisposeAsync(); } catch { /* best-effort */ }
            try { File.Delete(localPath); } catch { /* best-effort */ }
        }

        // Registration through the final await is wrapped in try/finally so a throw from EITHER
        // send (Start here — download has no per-chunk send, chunks arrive via OnFileTransferMessage)
        // or from awaiting tcs.Task still reaps all FOUR dictionaries, not just the two upload uses.
        // Download hands off to a channel/writer task rather than a simple request/response, so it
        // cannot adopt SendAndAwaitReplyAsync directly, but must reap on every exit path the same
        // way (RemEx-w9lj): before this fix, a failed Start send left the transfer-end waiter,
        // progress reporter, channel, and hasher all stranded forever, and the writer task blocked
        // on the channel with nothing left to complete it.
        bool completed = false;
        try
        {
            await _connection.SendAsync(new RemexMessage
            {
                Type = MessageTypes.FileTransferStart,
                FileTransferStart = new FileTransferStart
                {
                    TransferId = transferId,
                    Direction = "download",
                    RemotePath = remoteRelativePath,
                    RemoteRootId = remoteRootId,
                    RemoteRelativePath = remoteRelativePath,
                    FileName = Path.GetFileName(remoteRelativePath),
                    TotalBytes = 0,
                    Sha256Base64 = string.Empty
                }
            });

            var result = await _idleWatchdog.AwaitCompletionAsync(tcs, lastActivity, ct);

            // Honour a cancel that landed during the wait rather than finishing the download anyway.
            ct.ThrowIfCancellationRequested();

            // Signal the consumer that no more chunks are coming, then wait for all writes to
            // complete before the fileStream is disposed — prevents ObjectDisposedException on
            // pending writes.
            channel.Writer.TryComplete();
            // Deliberately NOT catching OperationCanceledException here. Nothing can have cancelled
            // the writer at this point — only DiscardPartialFileAsync does, and that runs in the
            // finally — so a swallow would be unreachable today and a trap tomorrow: this exact
            // catch is what hid the truncated-file bug while writerCts was linked to ct. A writer
            // fault that DOES reach here (a full disk faulting WriteAsync) must propagate.
            await writeTask;

            if (result.FileTransferEnd?.Success == false)
            {
                throw FileTransferHostException.ForHostError(
                    result.FileTransferEnd.ErrorMessage,
                    $"Download failed: {result.FileTransferEnd.ErrorMessage}");
            }

            // Verify the host-supplied SHA-256 against the bytes we actually received.
            // Master plan §996 calls this out as the recommended download-side parity
            // with upload integrity verification.
            var expectedHash = result.FileTransferEnd?.Sha256Base64;
            var actualHash = Convert.ToBase64String(hasher.GetHashAndReset());
            if (!string.IsNullOrEmpty(expectedHash) && expectedHash != actualHash)
            {
                throw new FileTransferIntegrityException();
            }

            completed = true;
        }
        finally
        {
            // ONE cleanup decision for every exit, rather than a delete at each throw site. The
            // paths that were missing it are the ordinary ones — user cancellation and the idle
            // timeout both went straight to this finally and left a truncated file under the FINAL
            // chosen filename, with no .part suffix to hint at it. Doing it here also means the next
            // failure exit somebody adds is covered without having to remember.
            if (!completed)
            {
                // Before the drain below, not after: abandoning must not flush a backlog into a file
                // that is about to be deleted.
                await DiscardPartialFileAsync();
            }

            // Unblock the writer task even when we got here via a throw before the normal
            // TryComplete()/await above ran (e.g. SendAsync or tcs.Task faulted), then reap every
            // dictionary this transfer touched. Any writer-task fault surfaced here is swallowed
            // deliberately — it is cleanup for a background task, not the caller-visible error,
            // which (if any) already propagated out of the try block above.
            channel.Writer.TryComplete();
            try { await writeTask; } catch { /* cleanup only; real errors already propagated above */ }

            // ORDER IS LOAD-BEARING, do not sort these. The producer looks up the channel first and
            // the hasher last, so removing the channel first means no later chunk can be hashed
            // without being queued. Removing the hasher first would open a window where a chunk is
            // folded into the digest and never written, which surfaces as an integrity failure
            // blaming the file rather than the teardown. Backlog box after the channel, same reason.
            _downloadChannels.TryRemove(transferId, out _);
            _downloadBacklogBytes.TryRemove(transferId, out _);
            _transferEndWaiters.TryRemove(transferId, out _);
            _progressReporters.TryRemove(transferId, out _);
            _downloadHashers.TryRemove(transferId, out _);
            _idleWatchdog.End(transferId);
            hasher.Dispose();
        }
    }

    /// <summary>
    /// Accounts <paramref name="byteCount"/> against a download's unwritten backlog, accepting it
    /// only while the total stays within <paramref name="limitBytes"/>.
    /// </summary>
    /// <param name="wouldQueue">What the backlog WOULD have become; meaningful only on rejection.</param>
    /// <returns><c>true</c> when the chunk may be queued.</returns>
    /// <remarks>
    /// Pulled out of the dispatch handler so the accounting can be tested without a live connection
    /// (RemEx-gyf4). The part worth pinning is not the comparison but the ROLLBACK: the counter is
    /// bumped optimistically because the common path is acceptance, so a rejection has to put the
    /// bytes back. Miss that and every refused chunk leaks a little of the ceiling, so a transfer
    /// that recovers is abandoned anyway on a backlog that has never actually existed.
    /// </remarks>
    internal static bool TryReserveBacklog(
        StrongBox<long> backlog, int byteCount, long limitBytes, out long wouldQueue)
    {
        wouldQueue = Interlocked.Add(ref backlog.Value, byteCount);
        if (wouldQueue <= limitBytes)
        {
            return true;
        }

        Interlocked.Add(ref backlog.Value, -byteCount);
        return false;
    }

    /// <summary>Retires bytes from a download's backlog once they are on the stream.</summary>
    /// <remarks>
    /// Trivial, and extracted anyway: it is the other half of <see cref="TryReserveBacklog"/> and the
    /// only thing that makes the ceiling releasable. Inline in the writer loop it was a line no test
    /// could reach — deleting it left every test green while every download over the ceiling failed.
    /// </remarks>
    internal static void RetireBacklog(StrongBox<long> backlog, int byteCount)
        => Interlocked.Add(ref backlog.Value, -byteCount);

    private void OnFileTransferMessage(RemexMessage message)
    {
        switch (message.Type)
        {
            case MessageTypes.FileRootsResponse:
                _rootsWaiter?.TrySetResult(message);
                break;

            case MessageTypes.FileBrowseResponse when message.FileBrowseResponse is { } resp:
                if (_browseWaiters.TryGetValue(resp.RequestId, out var browseTcs))
                    browseTcs.TrySetResult(message);
                break;

            case MessageTypes.FileTransferChunk when message.FileTransferChunk is { } chunk:
                // Every chunk is proof of life for the idle watchdog (RemEx-l519). Order relative
                // to the channel lookup does not matter: _downloadChannels is populated before the
                // Start send and removed only in the finally, so for the whole life of the wait
                // both entries exist or neither does.
                _idleWatchdog.Mark(chunk.TransferId);
                if (_downloadChannels.TryGetValue(chunk.TransferId, out var ch))
                {
                    var bytes = Convert.FromBase64String(chunk.DataBase64);

                    // The channel stays UNBOUNDED and this stays a TryWrite that always succeeds.
                    // Bounding it would be the obvious move and it is the wrong one here: this runs
                    // on the connection's synchronous message dispatch, so there is no way to await
                    // room, and a bounded TryWrite would return false and DROP the chunk — a
                    // corrupt file reported later as an integrity failure, which is the worst
                    // available outcome. Accounting for the backlog and abandoning the transfer
                    // deliberately keeps the ordering guarantee the channel exists for while still
                    // refusing to grow forever.
                    if (_downloadBacklogBytes.TryGetValue(chunk.TransferId, out var backlog) &&
                        !TryReserveBacklog(backlog, bytes.Length, MaxQueuedDownloadBytes, out var wouldQueue))
                    {
                        // Fail through the same waiter every other download failure uses, so the
                        // existing finally reaps the channel, the writer task and the partial file.
                        if (_transferEndWaiters.TryGetValue(chunk.TransferId, out var overflowWaiter))
                        {
                            overflowWaiter.TrySetException(
                                new FileTransferBacklogException(wouldQueue, MaxQueuedDownloadBytes));
                        }
                        break;
                    }

                    // Hash only what is actually queued. Appending before the backlog check would
                    // fold a chunk we then refused into the digest, turning an abandoned transfer
                    // into an integrity error that blames the wrong thing.
                    if (_downloadHashers.TryGetValue(chunk.TransferId, out var hasher))
                        hasher.AppendData(bytes);
                    ch.Writer.TryWrite(bytes);
                }
                break;

            case MessageTypes.FileTransferProgress when message.FileTransferProgress is { } prog:
                // The only proof of life an UPLOAD gets: the PC sends chunks but receives nothing
                // back until the end, so without this the watchdog would have to guess. The host
                // emits one of these every ProgressChunkInterval chunks while receiving.
                _idleWatchdog.Mark(prog.TransferId);
                if (_progressReporters.TryGetValue(prog.TransferId, out var reporter) && prog.TotalBytes > 0)
                    reporter?.Report((double)prog.BytesTransferred / prog.TotalBytes);
                break;

            case MessageTypes.FileTransferEnd when message.FileTransferEnd is { } end:
                if (_transferEndWaiters.TryGetValue(end.TransferId, out var endTcs))
                    endTcs.TrySetResult(message);
                break;

            case MessageTypes.FileManageResponse when message.FileManageResponse is { } manage:
                if (_manageWaiters.TryGetValue(manage.RequestId, out var manageTcs))
                    manageTcs.TrySetResult(message);
                break;

            case MessageTypes.FileHashResponse when message.FileHashResponse is { } hashResp:
                if (_hashWaiters.TryGetValue(hashResp.RequestId, out var hashTcs))
                    hashTcs.TrySetResult(message);
                break;

            case MessageTypes.FileRootManageResponse when message.FileRootManageResponse is { } rootManage:
                if (_rootManageWaiters.TryGetValue(rootManage.RequestId, out var rootManageTcs))
                    rootManageTcs.TrySetResult(message);
                break;

            // ── 2.1 File Sharing Overhaul (protocolVersion 3) responses ──
            case MessageTypes.FileVolumesResponse when message.FileVolumesResponse is { } volumes:
                if (_volumesWaiters.TryGetValue(volumes.RequestId, out var volumesTcs))
                    volumesTcs.TrySetResult(message);
                break;

            case MessageTypes.FileSearchResponse when message.FileSearchResponse is { } search:
                if (_searchWaiters.TryGetValue(search.RequestId, out var searchTcs))
                    searchTcs.TrySetResult(message);
                break;

            case MessageTypes.FileMetadataResponse when message.FileMetadataResponse is { } metadata:
                if (_metadataWaiters.TryGetValue(metadata.RequestId, out var metadataTcs))
                    metadataTcs.TrySetResult(message);
                break;

            case MessageTypes.FileThumbnailResponse when message.FileThumbnailResponse is { } thumbnail:
                if (_thumbnailWaiters.TryGetValue(thumbnail.RequestId, out var thumbnailTcs))
                    thumbnailTcs.TrySetResult(message);
                break;
        }
    }

    public void Dispose()
    {
        _connection.FileTransferMessageReceived -= OnFileTransferMessage;
    }
}
