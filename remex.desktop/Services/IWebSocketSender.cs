using System.Threading;
using System.Threading.Tasks;
using Remex.Core.Messages;

namespace Remex.Desktop.Services;

/// <summary>
/// The one outbound operation a correlated command needs: put this message on the wire.
/// </summary>
/// <remarks>
/// Extracted so the request/response correlation in <c>ConnectionViewModel</c> can be tested without
/// a live socket (RemEx-h01r). Two behaviours had been described in skipped tests since 2.0 and
/// could not be written: that a command whose response never arrives fails with a timeout rather
/// than hanging, and that two concurrent commands each receive their OWN response. The second is
/// the reason <c>_pendingCommands</c> is a dictionary at all - it replaced a single pending field
/// precisely because concurrent callers overwrote each other - so it is worth being able to prove.
/// <para>
/// Deliberately narrow. A full socket abstraction would have to model connect, receive, close and
/// state, none of which correlation cares about, and each of which would be a fake that could drift
/// from the real socket's behaviour.
/// </para>
/// </remarks>
internal interface IWebSocketSender
{
    Task SendAsync(RemexMessage message, CancellationToken ct);
}
