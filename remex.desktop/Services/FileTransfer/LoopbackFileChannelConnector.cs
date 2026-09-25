using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.WebSockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Desktop.Services.Security;
using Remex.Desktop.ViewModels;

namespace Remex.Desktop.Services.FileTransfer;

/// <summary>
/// Opens, and keeps open, the binary <c>/ws/files</c> channel to the PC's OWN host (perf audit P1-5).
/// </summary>
/// <remarks>
/// <para>
/// LOOPBACK ONLY, ON PURPOSE. The host skips proof-of-possession on <c>/ws/files</c> for loopback
/// (<c>HostBootstrapper</c>, the <c>isLoopbackFiles</c> check) and demands a
/// <c>ChannelReconnectAuth</c> challenge from anyone else — which this UI does not implement. So a
/// remote host gets <c>null</c> here and its uploads stay on the legacy Base64 path, which is
/// unchanged. The loopback test is <see cref="ConnectionViewModel.IsLoopbackHost"/> itself, not a
/// copy: the TLS decision below must be exactly the one the control channel made for the same host.
/// </para>
/// <para>
/// NO <c>clientId</c> IS SENT. A loopback control connection is frozen at no identity (RemEx-4215), so
/// every <c>file_transfer_offer</c> / <c>complete</c> / <c>control</c> it sends is keyed on the empty
/// string; the host keys this channel's receive sessions on the <c>clientId</c> query value, and an
/// absent one reads as that same empty string. Sending a paired id would not match — and the host
/// refuses a paired id on loopback outright (RemEx-4u0d).
/// </para>
/// <para>
/// ONE LONG-LIVED CHANNEL, NOT ONE PER UPLOAD. The host's channel loop, on exit, suspends every
/// receive session for its key — including one a NEWER channel has just started. Opening and closing
/// a channel per file would put that teardown in a race with the next file's offer on every upload;
/// keeping one open means it can only happen after a genuine drop.
/// </para>
/// </remarks>
internal sealed class LoopbackFileChannelConnector : IFileChannelConnector, IDisposable
{
    /// <summary>How long a connect may take before the upload falls back to the legacy path.</summary>
    internal static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    private readonly Func<string> _hostAddress;
    private readonly Func<Task<IReadOnlyDictionary<string, string>?>> _loadPins;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WebSocketFileFrameChannel? _channel;
    private bool _disposed;

    public LoopbackFileChannelConnector(
        Func<string> hostAddress,
        ILogger? logger = null,
        Func<Task<IReadOnlyDictionary<string, string>?>>? loadPins = null)
    {
        _hostAddress = hostAddress;
        _logger = logger ?? NullLogger.Instance;
        _loadPins = loadPins ?? LoadPinsFromStoreAsync;
    }

    public async Task<IFileFrameChannel?> TryAcquireAsync(CancellationToken ct)
    {
        if (!TryBuildFilesUri(_hostAddress(), out var filesUri))
            return null;

        await _gate.WaitAsync(ct);
        try
        {
            if (Volatile.Read(ref _disposed))
                return null;

            if (_channel is { IsOpen: true } live && live.Uri == filesUri)
                return live;

            _channel?.Dispose();
            _channel = null;

            var pins = await _loadPins();
            var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);

            // The control channel's rule for a loopback host, restated through the SHARED policy
            // rather than re-derived (RemEx-mlce, RemEx-xmgw): a pinned store must contain this cert,
            // an empty store trusts the local host on first use. A missing store fails closed.
            socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                certificate is not null
                && CertificatePinPolicy.IsCertificateAcceptable(
                    CertificatePinPolicy.ComputeSpkiHash(certificate), pins, allowFirstTimeTrust: true);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ConnectTimeout);
            try
            {
                await socket.ConnectAsync(filesUri, timeout.Token);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested
                && ex is WebSocketException or HttpRequestException or IOException or OperationCanceledException)
            {
                socket.Dispose();
                _logger.LogWarning(ex,
                    "Could not open the binary file channel at {Uri}; this upload uses the legacy path.",
                    filesUri);
                return null;
            }
            catch
            {
                socket.Dispose();
                throw;
            }

            var channel = new WebSocketFileFrameChannel(filesUri, socket, _logger);

            // Publish FIRST, then re-check. Dispose does not take the gate, so it can land at any
            // point here: checking the flag before storing would leave a window in which Dispose
            // swaps out the OLD (null) field and this then stores a live socket on a disposed
            // connector that nothing ever closes. After the store, either Dispose's Exchange sees
            // this channel and closes it, or this sees the flag and does. Both is harmless -
            // WebSocketFileFrameChannel.Dispose is idempotent. Interlocked, not a volatile write: the
            // store and the flag read below must not reorder (Dispose mirrors it - flag, then Exchange).
            Interlocked.Exchange(ref _channel, channel);
            if (Volatile.Read(ref _disposed))
            {
                Interlocked.CompareExchange(ref _channel, null, channel);
                channel.Dispose();
                return null;
            }

            return channel;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The <c>/ws/files</c> address for a loopback control address, or false for any other host.
    /// </summary>
    internal static bool TryBuildFilesUri(string hostAddress, out Uri filesUri)
    {
        filesUri = null!;
        if (!Uri.TryCreate(hostAddress, UriKind.Absolute, out var controlUri))
            return false;
        if (!ConnectionViewModel.IsLoopbackHost(controlUri))
            return false;

        // Same shape as RemoteDesktopService.BuildDesktopUri for /ws/desktop: the control address
        // normally ends in /ws, and the binary channel is a sibling path under it.
        var path = controlUri.AbsolutePath.TrimEnd('/');
        path = path.EndsWith("/ws", StringComparison.OrdinalIgnoreCase) ? path + "/files" : path + "/ws/files";

        var builder = new UriBuilder(controlUri)
        {
            Path = path,
            Query = "protocolVersion=" + ProtocolVersionPolicy.Current.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        filesUri = builder.Uri;
        return true;
    }

    private static async Task<IReadOnlyDictionary<string, string>?> LoadPinsFromStoreAsync()
    {
        // A missing store is not an empty store: null makes the certificate check fail closed.
        var store = App.Services?.GetService(typeof(PinnedCertStore)) as PinnedCertStore;
        return store is null ? null : await store.GetAllPinsAsync();
    }

    /// <remarks>
    /// Does not take the gate: a connect in progress may hold it for up to <see cref="ConnectTimeout"/>,
    /// and disposal runs on the UI thread. A connect that finishes after this sees the flag and
    /// discards its channel.
    /// </remarks>
    public void Dispose()
    {
        Volatile.Write(ref _disposed, true);
        Interlocked.Exchange(ref _channel, null)?.Dispose();
    }
}

/// <summary>
/// A <see cref="ClientWebSocket"/> carrying <see cref="FileFrameCodec"/> frames, with inbound frames
/// routed per transfer id.
/// </summary>
internal sealed class WebSocketFileFrameChannel : IFileFrameChannel, IDisposable
{
    /// <summary>Largest inbound frame accepted — the host's own bound for the same channel.</summary>
    private const int MaxInboundFrameBytes = FileTransferLimits.DataPayloadBytes + (64 * 1024);

    private readonly ClientWebSocket _socket;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _receiveCts = new();
    private readonly ConcurrentDictionary<string, Subscription> _subscriptions = new();
    private int _closed;
    private int _disposed;

    public WebSocketFileFrameChannel(Uri uri, ClientWebSocket socket, ILogger logger)
    {
        Uri = uri;
        _socket = socket;
        _logger = logger;
        _ = Task.Run(ReceiveLoopAsync);
    }

    public Uri Uri { get; }

    public bool IsOpen => Volatile.Read(ref _closed) == 0 && _socket.State == WebSocketState.Open;

    public IDisposable Subscribe(string transferId, Action<FileFrameEnvelope> onFrame, Action onClosed)
    {
        var subscription = new Subscription(this, transferId, onFrame, onClosed);
        _subscriptions[transferId] = subscription;

        // Checked AFTER registering, so a close that lands between the two cannot be missed: either
        // MarkClosed sees this subscription, or this sees the flag. Seeing both is harmless -
        // Subscription.NotifyClosed runs its callback at most once.
        if (Volatile.Read(ref _closed) != 0)
            subscription.NotifyClosed();
        return subscription;
    }

    public async Task SendAsync(FileFrameEnvelope envelope, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsOpen)
            throw new IOException("The binary file channel is closed.");

        var header = FileFrameCodec.SerializeHeader(envelope);
        var frameLength = FileFrameCodec.GetFrameLength(header.Length, payload.Length);
        var frame = ArrayPool<byte>.Shared.Rent(frameLength);
        try
        {
            // The RETURNED length bounds the send, never the rented array, which may be longer and
            // hold another renter's bytes (FileFrameCodec.WriteFrame remarks).
            var written = FileFrameCodec.WriteFrame(header, payload.Span, frame);

            await _sendLock.WaitAsync(ct);
            try
            {
                // CancellationToken.None on the socket send: see IFileFrameChannel.SendAsync.
                await _socket.SendAsync(
                    frame.AsMemory(0, written), WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
            {
                MarkClosed();
                throw new IOException("The binary file channel failed while sending.", ex);
            }
            finally
            {
                _sendLock.Release();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(frame);
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = ArrayPool<byte>.Shared.Rent(MaxInboundFrameBytes);
        var ct = _receiveCts.Token;
        try
        {
            while (!ct.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                var length = 0;
                ValueWebSocketReceiveResult result;
                do
                {
                    if (length == buffer.Length)
                    {
                        _logger.LogWarning("Dropping the binary file channel: an inbound frame exceeded {Max} bytes.", MaxInboundFrameBytes);
                        return;
                    }

                    result = await _socket.ReceiveAsync(buffer.AsMemory(length), ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return;
                    length += result.Count;
                }
                while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Binary)
                    continue;

                if (!FileFrameCodec.TryRead(buffer.AsSpan(0, length), out var envelope, out _) || envelope is null)
                {
                    _logger.LogWarning("Discarding a malformed frame on the binary file channel ({Len} bytes).", length);
                    continue;
                }

                if (_subscriptions.TryGetValue(envelope.TransferId, out var subscription))
                    subscription.Deliver(envelope);
            }
        }
        catch (WebSocketException ex) when (!ct.IsCancellationRequested)
        {
            // A genuine drop, not a teardown we asked for: say why, or the only trace of it is an
            // upload that ends in a generic "channel closed". Every live subscriber hears about it below.
            _logger.LogWarning(ex, "The binary file channel at {Uri} dropped.", Uri);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Our own Dispose (cancel, then abort - which can surface as a WebSocketException on the
            // pending receive). Ordinary teardown, deliberately quiet.
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            MarkClosed();
        }
    }

    private void MarkClosed()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
            return;

        foreach (var subscription in _subscriptions.Values)
            subscription.NotifyClosed();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        MarkClosed();
        _receiveCts.Cancel();
        try { _socket.Abort(); } catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException) { }
        _socket.Dispose();
        _receiveCts.Dispose();
    }

    private sealed class Subscription(
        WebSocketFileFrameChannel owner,
        string transferId,
        Action<FileFrameEnvelope> onFrame,
        Action onClosed) : IDisposable
    {
        private int _closedNotified;

        public void Deliver(FileFrameEnvelope envelope) => onFrame(envelope);

        public void NotifyClosed()
        {
            if (Interlocked.Exchange(ref _closedNotified, 1) == 0)
                onClosed();
        }

        public void Dispose()
        {
            // Remove only THIS subscription: a later one for the same id must not be torn down by
            // an earlier one's dispose.
            ((ICollection<KeyValuePair<string, Subscription>>)owner._subscriptions)
                .Remove(new KeyValuePair<string, Subscription>(transferId, this));
        }
    }
}
