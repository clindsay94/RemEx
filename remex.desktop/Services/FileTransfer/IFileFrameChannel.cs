using System;
using System.Threading;
using System.Threading.Tasks;
using Remex.Core.Models;

namespace Remex.Desktop.Services.FileTransfer;

/// <summary>
/// One open binary <c>/ws/files</c> channel to the host: raw <see cref="FileFrameEnvelope"/> frames
/// out, ack and error frames back (perf audit P1-5).
/// </summary>
/// <remarks>
/// <para>
/// A SEAM IN THE SAME SPIRIT AS <see cref="IFileTransferConnection"/>, and just as narrow. An upload
/// needs exactly two things from the binary channel: a way to put a frame on it, and to hear the
/// frames the host sends back about ONE transfer. Connect, close and reconnect belong to
/// <see cref="IFileChannelConnector"/>, so a test fake here never has to model them.
/// </para>
/// <para>
/// Frames and the control plane travel on SEPARATE sockets. Nothing orders bytes across the two, which
/// is why an upload must never announce completion on <c>/ws</c> before this channel has carried an
/// ack for every byte it sent. See <c>docs/REGRESSION-GUARDS.md</c>, "Never announce
/// <c>file_transfer_complete</c> before the peer has acked the data".
/// </para>
/// </remarks>
internal interface IFileFrameChannel
{
    /// <summary>
    /// Routes every inbound frame naming <paramref name="transferId"/> to <paramref name="onFrame"/>,
    /// and the loss of the channel to <paramref name="onClosed"/>. Dispose the result to stop.
    /// </summary>
    /// <remarks>
    /// When the channel is ALREADY closed, <paramref name="onClosed"/> runs before this returns: a
    /// sender that subscribed to a dead channel would otherwise wait for an ack that cannot come.
    /// Both callbacks run on the channel's receive thread and must not block.
    /// </remarks>
    IDisposable Subscribe(string transferId, Action<FileFrameEnvelope> onFrame, Action onClosed);

    /// <summary>
    /// Sends one frame: <paramref name="envelope"/> as the header, <paramref name="payload"/> after it.
    /// </summary>
    /// <remarks>
    /// <paramref name="ct"/> is observed BEFORE the frame goes out, not during: cancelling a
    /// <c>ClientWebSocket</c> send aborts the whole socket, and this channel is shared by every
    /// upload. A cancelled upload stops between frames and tells the host on the control plane.
    /// </remarks>
    Task SendAsync(FileFrameEnvelope envelope, ReadOnlyMemory<byte> payload, CancellationToken ct);
}

/// <summary>
/// Supplies the binary channel to the host the control connection is talking to, when it can be
/// used from here at all.
/// </summary>
internal interface IFileChannelConnector
{
    /// <summary>
    /// Returns an open channel, reusing a live one, or <c>null</c> when the binary channel cannot be
    /// used for this connection — the host is not local, or the socket would not open.
    /// </summary>
    /// <remarks>
    /// <c>null</c> is a normal answer, not an error: the caller falls back to the legacy Base64 path,
    /// which works against every host. Throws only <see cref="OperationCanceledException"/> for a
    /// cancelled <paramref name="ct"/>, so a stopped upload still unwinds as cancelled.
    /// </remarks>
    Task<IFileFrameChannel?> TryAcquireAsync(CancellationToken ct);
}
