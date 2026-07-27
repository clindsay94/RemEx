using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
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
    private readonly ConcurrentDictionary<string, IncrementalHash> _downloadHashers = new();

    /// <summary>
    /// Last-activity timestamp per live transfer, feeding the idle watchdog.
    /// </summary>
    /// <remarks>
    /// The value is a <see cref="StrongBox{T}"/> rather than a bare <c>long</c> so the receive path
    /// can update it through <c>TryGetValue</c>, which cannot insert. Writing via the indexer would
    /// let a chunk arriving AFTER the transfer's <c>finally</c> has reaped re-insert an entry that
    /// nothing ever removes - and that is the ordinary cancelled-download path, because the peer
    /// keeps streaming until it has processed the cancel. A late write into a box no longer in the
    /// dictionary is harmless and collectible.
    /// <para>
    /// Timestamps come from <see cref="Stopwatch.GetTimestamp"/>, not the wall clock: a forward
    /// system-clock correction larger than the idle limit - an NTP sync after boot, a VM resume, or
    /// the RTC-in-local-time correction a dual-boot machine makes, which this project explicitly
    /// targets - would otherwise fail a perfectly healthy transfer instantly.
    /// </para>
    /// </remarks>
    private readonly ConcurrentDictionary<string, StrongBox<long>> _transferLastActivity = new();
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

    /// <summary>Records that something arrived for <paramref name="transferId"/>, if it is still live.</summary>
    private void MarkTransferActivity(string transferId)
    {
        // TryGetValue, never the indexer - see the remarks on _transferLastActivity.
        if (_transferLastActivity.TryGetValue(transferId, out var lastActivity))
            Volatile.Write(ref lastActivity.Value, Stopwatch.GetTimestamp());
    }

    /// <summary>
    /// Awaits a transfer's end message, failing only if the peer goes silent for
    /// <see cref="TransferIdleTimeoutSeconds"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT <c>tcs.Task.WaitAsync(timeout)</c>, which is what every control request in
    /// this class uses. A total-duration bound is the wrong instrument for a wait whose length is
    /// proportional to file size — see <see cref="TransferIdleTimeoutSeconds"/>. The deadline is
    /// recomputed from the last observed activity on each pass, so it slides forward for as long as
    /// the transfer is making progress and only bites on genuine silence.
    /// <para>
    /// Cancellation arrives through the caller's <c>ct.Register</c>, which completes the TCS, so the
    /// cancelled path returns through the normal completion branch rather than through the timer.
    /// </para>
    /// </remarks>
    private async Task<RemexMessage> AwaitTransferEndAsync(
        TaskCompletionSource<RemexMessage> tcs,
        StrongBox<long> lastActivity,
        CancellationToken ct)
    {
        var idleLimit = TimeSpan.FromSeconds(TransferIdleTimeoutSeconds);

        while (true)
        {
            // Load-bearing, not defensive. Cancellation completes the TCS *and* cancels the linked
            // timer, so WhenAny may legitimately return either. If it returns the timer, without
            // this guard the next pass would build an already-cancelled delay that completes
            // instantly, and the loop would spin a core until the idle deadline expired.
            ct.ThrowIfCancellationRequested();

            var remaining = idleLimit - Stopwatch.GetElapsedTime(Volatile.Read(ref lastActivity.Value));

            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException(
                    "The transfer stopped responding: nothing was received for " +
                    $"{TransferIdleTimeoutSeconds} seconds.");
            }

            // Linked source so the pending timer is cancelled the moment the wait resolves,
            // rather than being left to run out on its own once per idle window.
            using var timerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(remaining, timerCts.Token));
            timerCts.Cancel();

            if (ReferenceEquals(completed, tcs.Task))
                return await tcs.Task;

            // The timer won, but activity may have landed while it was running: loop and
            // re-measure rather than failing on the strength of a stale deadline.
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
            throw new IOException($"Root listing error: {err}");

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

    /// <summary>
    /// How long a file transfer may go with NO sign of life from the peer before it is failed.
    /// </summary>
    /// <remarks>
    /// This is an IDLE bound, not a total-duration one, and the difference is the whole point.
    /// Upload and download last as long as the file is big, so any wall-clock deadline either
    /// aborts a large healthy transfer or is so generous it never fires — and aborting also reaps
    /// the waiter, so the peer's eventual success reply is dropped and the user is told the
    /// transfer failed while the file appears anyway. Measuring silence instead is size-independent:
    /// a 40 GB transfer that is still moving never trips it, and a peer that dies mid-file trips it
    /// in two minutes. (RemEx-l519.)
    /// </remarks>
    private const int TransferIdleTimeoutSeconds = 120;

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
            throw new IOException(response.FileManageResponse.ErrorMessage ?? $"{operation} failed.");
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
            throw new IOException($"Metadata error: {err}");
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
            throw new IOException($"Volumes error: {err}");
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
            throw new IOException($"Add root failed: {err}");

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
            throw new IOException($"Remove root failed: {err}");

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
        var lastActivity = new StrongBox<long>(Stopwatch.GetTimestamp());
        _transferLastActivity[transferId] = lastActivity;

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

            var result = await AwaitTransferEndAsync(tcs, lastActivity, ct);

            if (result.FileTransferEnd?.Success == false)
                throw new IOException($"Upload failed: {result.FileTransferEnd.ErrorMessage}");
        }
        finally
        {
            _transferEndWaiters.TryRemove(transferId, out _);
            _progressReporters.TryRemove(transferId, out _);
            _transferLastActivity.TryRemove(transferId, out _);
        }
    }

    public async Task DownloadAsync(string remoteRootId, string remoteRelativePath, string localPath, IProgress<double>? progress, CancellationToken ct)
    {
        var transferId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<RemexMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _transferEndWaiters[transferId] = tcs;
        _progressReporters[transferId] = progress;
        var lastActivity = new StrongBox<long>(Stopwatch.GetTimestamp());
        _transferLastActivity[transferId] = lastActivity;

        using var reg = ct.Register(() =>
        {
            tcs.TrySetCanceled(ct);
            _ = _connection.SendAsync(new RemexMessage
            {
                Type = MessageTypes.FileTransferCancel,
                FileTransferCancel = new FileTransferCancel { TransferId = transferId }
            });
        });

        await using var fileStream = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);

        // Unbounded channel ensures chunk ordering: the consumer writes sequentially,
        // eliminating the race condition that would exist with fire-and-forget WriteAsync.
        var channel = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        _downloadChannels[transferId] = channel;
        var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        _downloadHashers[transferId] = hasher;

        var writeTask = Task.Run(async () =>
        {
            await foreach (var data in channel.Reader.ReadAllAsync())
                await fileStream.WriteAsync(data);
        }, ct);

        // Registration through the final await is wrapped in try/finally so a throw from EITHER
        // send (Start here — download has no per-chunk send, chunks arrive via OnFileTransferMessage)
        // or from awaiting tcs.Task still reaps all FOUR dictionaries, not just the two upload uses.
        // Download hands off to a channel/writer task rather than a simple request/response, so it
        // cannot adopt SendAndAwaitReplyAsync directly, but must reap on every exit path the same
        // way (RemEx-w9lj): before this fix, a failed Start send left the transfer-end waiter,
        // progress reporter, channel, and hasher all stranded forever, and the writer task blocked
        // on the channel with nothing left to complete it.
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

            var result = await AwaitTransferEndAsync(tcs, lastActivity, ct);

            // Signal the consumer that no more chunks are coming, then wait for all writes to
            // complete before the fileStream is disposed — prevents ObjectDisposedException on
            // pending writes.
            channel.Writer.TryComplete();
            try { await writeTask; } catch (OperationCanceledException) { }

            if (result.FileTransferEnd?.Success == false)
            {
                try { File.Delete(localPath); } catch { /* best-effort */ }
                throw new IOException($"Download failed: {result.FileTransferEnd.ErrorMessage}");
            }

            // Verify the host-supplied SHA-256 against the bytes we actually received.
            // Master plan §996 calls this out as the recommended download-side parity
            // with upload integrity verification.
            var expectedHash = result.FileTransferEnd?.Sha256Base64;
            var actualHash = Convert.ToBase64String(hasher.GetHashAndReset());
            if (!string.IsNullOrEmpty(expectedHash) && expectedHash != actualHash)
            {
                try { File.Delete(localPath); } catch { /* best-effort */ }
                throw new IOException("Download failed: SHA-256 integrity check failed.");
            }
        }
        finally
        {
            // Unblock the writer task even when we got here via a throw before the normal
            // TryComplete()/await above ran (e.g. SendAsync or tcs.Task faulted), then reap every
            // dictionary this transfer touched. Any writer-task fault surfaced here is swallowed
            // deliberately — it is cleanup for a background task, not the caller-visible error,
            // which (if any) already propagated out of the try block above.
            channel.Writer.TryComplete();
            try { await writeTask; } catch { /* cleanup only; real errors already propagated above */ }

            _downloadChannels.TryRemove(transferId, out _);
            _transferEndWaiters.TryRemove(transferId, out _);
            _progressReporters.TryRemove(transferId, out _);
            _downloadHashers.TryRemove(transferId, out _);
            _transferLastActivity.TryRemove(transferId, out _);
            hasher.Dispose();
        }
    }

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
                MarkTransferActivity(chunk.TransferId);
                if (_downloadChannels.TryGetValue(chunk.TransferId, out var ch))
                {
                    var bytes = Convert.FromBase64String(chunk.DataBase64);
                    if (_downloadHashers.TryGetValue(chunk.TransferId, out var hasher))
                        hasher.AppendData(bytes);
                    ch.Writer.TryWrite(bytes);
                }
                break;

            case MessageTypes.FileTransferProgress when message.FileTransferProgress is { } prog:
                // The only proof of life an UPLOAD gets: the PC sends chunks but receives nothing
                // back until the end, so without this the watchdog would have to guess. The host
                // emits one of these every ProgressChunkInterval chunks while receiving.
                MarkTransferActivity(prog.TransferId);
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
