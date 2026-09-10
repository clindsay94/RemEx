using System;
using System.Threading.Tasks;
using Remex.Core.Messages;

namespace Remex.Desktop.Services.FileTransfer;

/// <summary>
/// The connection, narrowed to the two things file transfer actually needs from it: a way to put a
/// message on the wire, and notification when a <c>file_*</c> message comes back.
/// </summary>
/// <remarks>
/// Extracted so a test can deliver an INBOUND file transfer message to
/// <see cref="FileTransferClient"/> (RemEx-qmnl).
/// <para>
/// BE EXACT ABOUT WHY, because the obvious wider claim is false. The download unwind path was NOT
/// untestable before this: <c>FileTransferCancelOrderingTests</c> and
/// <c>FileTransferClientLeakTests</c> already drive <c>DownloadAsync</c> end to end, via the
/// <c>OutboundSender</c>/<c>IWebSocketSender</c> seam and via failures that occur before the
/// connection is reached. What was genuinely impossible is the inbound direction: a C# event can
/// only be raised by the class that DECLARES it, so no test could hand the client a
/// <c>file_transfer_end</c> through a real <c>ConnectionViewModel</c> — and a host-reported failure
/// is the ordinary way a download goes wrong. RemEx-gyf4 found two real defects on that unwind by
/// reading it, which is the argument for covering it rather than reasoning about it.
/// </para>
/// <para>
/// DELIBERATELY NARROW, following the <c>IWebSocketSender</c> precedent (RemEx-h01r). A fuller
/// connection abstraction would have to model connect, receive, close and state — none of which file
/// transfer cares about, and each of which becomes a fake that can drift from the real thing.
/// <c>ConnectionViewModel</c> satisfies this without gaining a single new member: both signatures
/// below are exactly what it already declared.
/// </para>
/// </remarks>
public interface IFileTransferConnection
{
    /// <summary>Raised for each inbound <c>file_*</c> message routed to the transfer layer.</summary>
    event Action<RemexMessage>? FileTransferMessageReceived;

    /// <summary>Puts <paramref name="message"/> on the wire.</summary>
    Task SendAsync(RemexMessage message);
}
