using System.Net.WebSockets;

namespace Remex.Core.Native;

/// <summary>
/// Turns a half-open socket into a detected drop, for both native client sockets (perf audit P0-12).
/// </summary>
/// <remarks>
/// <para>
/// A link that dies without a RST — Wi-Fi dropping silently, the PC sleeping mid-session — leaves a
/// <see cref="ClientWebSocket"/> reading <see cref="WebSocketState.Open"/> until the OS gives up on
/// TCP retransmits, which on Android is many minutes. Until then the receive loop is parked, every
/// send blocks, and every control message queued behind it stalls with no error anywhere.
/// </para>
/// <para>
/// Two bounds close that window. <see cref="ApplyKeepAlive"/> makes the socket send a WebSocket Ping
/// on an interval and abort itself when no Pong comes back in time, which catches a dead link even
/// when nothing is being sent. <see cref="SendOrAbortAsync"/> bounds each outbound frame and aborts
/// the socket when one cannot be written, which catches it at the first send.
/// </para>
/// <para>
/// **BOTH END IN AN ABORT, NOT A NEW RECOVERY PATH.** An aborted socket fails the pending receive,
/// and each client's receive loop already treats a failed receive as the connection ending: the
/// control client raises <c>ConnectionStateChanged(false)</c>, which is what the Kotlin heartbeat
/// reconnects on (and reconnect re-runs the proof-of-possession challenge), and the desktop client
/// clears its streaming flag so the next operation reconnects and restarts the stream. Nothing here
/// reconnects or re-pairs by itself, on purpose: a second recovery path would race the first.
/// </para>
/// <para>
/// Plain BCL <see cref="CancellationTokenSource"/> and <see cref="ClientWebSocketOptions"/>
/// properties only — no reflection, safe under NativeAOT.
/// </para>
/// </remarks>
internal static class SocketLiveness
{
    /// <summary>
    /// How often the socket pings. Kept at the framework default
    /// (<see cref="WebSocket.DefaultKeepAliveInterval"/>, 30 s) so this change adds no wakeups — it
    /// only turns the unsolicited Pong the socket already sent into a Ping that expects an answer.
    /// </summary>
    internal static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a Ping may go unanswered before the socket aborts itself. A dead link is detected
    /// within <see cref="KeepAliveInterval"/> + this, about 50 s, instead of the OS retransmit timeout.
    /// The host's receive loops answer Pings as part of every <c>ReceiveAsync</c>, and none of its
    /// per-message handlers hold the loop anywhere near this long.
    /// </summary>
    internal static readonly TimeSpan KeepAliveTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Ceiling on writing one frame. A send only blocks once the kernel send buffer is full — the peer
    /// has stopped acknowledging — so this is generous: the largest routine frame is a 64 KiB legacy
    /// upload chunk (~88 KB as base64 JSON), and failing to move that in fifteen seconds is a link
    /// under ~6 KB/s, which is not carrying anything useful anyway.
    /// </summary>
    internal static readonly TimeSpan DefaultSendTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Test-only override for <see cref="DefaultSendTimeout"/>. Process-wide, so a test that sets it
    /// must restore it. Same seam as <c>RemexDesktopClient.ConnectTimeoutOverrideForTests</c>.
    /// </summary>
    internal static TimeSpan? SendTimeoutOverrideForTests { get; set; }

    internal static TimeSpan SendTimeout => SendTimeoutOverrideForTests ?? DefaultSendTimeout;

    /// <summary>Configures Ping/Pong keep-alive with a timeout. Call before <c>ConnectAsync</c>.</summary>
    internal static void ApplyKeepAlive(ClientWebSocketOptions options)
    {
        options.KeepAliveInterval = KeepAliveInterval;
        options.KeepAliveTimeout = KeepAliveTimeout;
    }

    /// <summary>
    /// Sends one complete message, aborting <paramref name="socket"/> if it cannot be written within
    /// <see cref="SendTimeout"/>.
    /// </summary>
    /// <exception cref="TimeoutException">Our deadline expired; the socket has been aborted.</exception>
    /// <exception cref="OperationCanceledException">The caller's token was cancelled.</exception>
    /// <remarks>
    /// The caller cancelling surfaces unchanged as <see cref="OperationCanceledException"/> — that is
    /// not a dead link and must not be reported as one. (The framework still aborts the socket on a
    /// cancelled send, as it does for a cancelled receive; see
    /// <c>CancelledReceiveKillsTheSocketTests</c>. That was already true before this helper.)
    /// </remarks>
    internal static async Task SendOrAbortAsync(
        WebSocket socket,
        ArraySegment<byte> bytes,
        WebSocketMessageType messageType,
        CancellationToken ct)
    {
        var timeout = SendTimeout;
        // One linked CTS + timer per send, including remote-desktop pointer input. Deliberately not
        // pooled: TryReset cannot reuse a source whose timer fired (the timeout case is exactly the
        // one that must work), and a shared timer would need its own synchronisation across the two
        // sockets. At input rates (tens to low hundreds per second) this is a few small allocations on
        // a path already allocating the serialized frame, so it was left simple. (perf audit P0-12)
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            await socket.SendAsync(bytes, messageType, true, deadline.Token);
        }
        catch (Exception ex) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested
                                   && ex is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException)
        {
            // Filtered on OUR deadline having fired, not on the exception type alone: depending on
            // where in the write the cancellation lands, the framework surfaces it as a cancellation
            // or as the stream failing underneath it. Either way it is this deadline's doing.
            // Idempotent, and explicit rather than relying on the framework's cancel-aborts behaviour:
            // the abort is the whole point, because it is what fails the parked receive and hands the
            // drop to the existing reconnect path.
            socket.Abort();
            throw new TimeoutException(
                $"Sending on the socket did not complete within {timeout.TotalSeconds:F0} seconds; the connection was aborted.");
        }
    }
}
