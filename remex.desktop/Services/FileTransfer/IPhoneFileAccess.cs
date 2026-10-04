using System;
using System.Collections.Generic;

namespace Remex.Desktop.Services.FileTransfer;

/// <summary>A paired phone the PC can browse right now, as the Source picker shows it.</summary>
/// <param name="ClientId">The phone's paired client id. Never shown to the user.</param>
/// <param name="DisplayName">
/// The name to show: the user's own name for the device when they set one, otherwise the name the
/// phone reported at pairing. Null when the phone has never reported a name; the picker then shows a
/// localized fallback instead of the raw id.
/// </param>
public sealed record PhoneFileSource(string ClientId, string? DisplayName);

/// <summary>
/// Lets the PC's own File Transfer screen browse a paired phone's shared folders (RemEx-xt0af).
/// </summary>
/// <remarks>
/// <para>
/// THE DIRECTION OF THE CONNECTION DOES NOT CHANGE. The phone dials the PC, always. What this adds is
/// that the PC may send a small, fixed set of <c>file_*</c> REQUESTS down a paired phone's existing,
/// already-authenticated session, and get the phone's replies back. The phone has served exactly these
/// requests for as long as it has had a file host; the PC simply never asked. No new wire types, no
/// socket opened by the PC.
/// </para>
/// <para>
/// DECLARED HERE, IMPLEMENTED BY THE HOST, for the same dependency-direction reason as
/// <see cref="IClientSessionSource"/>: <c>remex.agent</c> references <c>remex.desktop</c>, so the
/// desktop cannot name the host's relay type. Resolve it from <c>App.EmbeddedHostServices</c> on every
/// use rather than caching it, because the host publishes its container after it starts.
/// </para>
/// <para>
/// READ-ONLY UNLESS THE PHONE SAYS OTHERWISE. Roots, browse, volumes, manifest, metadata, thumbnail and
/// search requests are always relayed. <c>file_manage_request</c> (rename, delete, move, copy, new
/// folder) is relayed only to a phone whose own roots reply said the person turned on "Let your PC
/// change files" (RemEx-fgmne); anything else is refused before it reaches the phone.
/// </para>
/// <para>
/// THE PHONE'S OWN SETTINGS ARE THE CONSENT. What the PC can see is exactly what the person shared
/// under "Access from your PC" on the phone: the shared folders, plus whole-device browsing only when
/// they turned it on there. No new prompt is raised on the phone while the PC browses.
/// </para>
/// </remarks>
public interface IPhoneFileAccess
{
    /// <summary>
    /// Every phone that is both paired and connected right now. Never null; empty when none.
    /// </summary>
    IReadOnlyList<PhoneFileSource> AvailablePhones();

    /// <summary>Raised when a phone connects or disconnects, so a picker can refresh. Any thread.</summary>
    event Action? AvailabilityChanged;

    /// <summary>
    /// Opens a file-browsing connection to one phone, carried over that phone's existing session.
    /// </summary>
    /// <exception cref="PhoneNotConnectedException">
    /// The phone is not paired, or not connected at this moment.
    /// </exception>
    IPhoneFileConnection Open(string clientId);
}

/// <summary>
/// A browsing connection to one phone. Hand it to <see cref="FileTransferClient"/> exactly like the
/// PC's own connection; it relays the read-only request types and delivers the phone's replies.
/// </summary>
/// <remarks>
/// Disposing it releases its registration with the host. A request already in flight when the phone
/// disconnects is completed with a failure reply rather than left to time out, and
/// <see cref="Disconnected"/> is raised once.
/// </remarks>
public interface IPhoneFileConnection : IFileTransferConnection, IDisposable
{
    /// <summary>The phone this connection reaches.</summary>
    string ClientId { get; }

    /// <summary>False once the phone has disconnected or this connection was disposed.</summary>
    bool IsConnected { get; }

    /// <summary>Raised once when the phone's session ends. May arrive on any thread.</summary>
    event Action? Disconnected;
}

/// <summary>
/// The phone the PC tried to reach is not paired, or is not connected right now.
/// </summary>
/// <remarks>
/// A plain <see cref="Exception"/> rather than an <see cref="System.IO.IOException"/> on purpose: the
/// transfer queue maps IOExceptions to "the folder on this PC is unavailable", which would send the
/// user looking at the wrong machine.
/// </remarks>
public sealed class PhoneNotConnectedException : Exception
{
    public PhoneNotConnectedException()
        : base("The phone is not paired and connected.")
    {
    }

    public PhoneNotConnectedException(string message)
        : base(message)
    {
    }

    public PhoneNotConnectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The PC asked to relay a request that is not on the allowlist, or whose operation, names or paths
/// failed validation (RemEx-xt0af, RemEx-fgmne).
/// </summary>
public sealed class PhoneFileRequestRefusedException : InvalidOperationException
{
    public PhoneFileRequestRefusedException()
        : base("That request cannot be sent to a phone.")
    {
    }

    public PhoneFileRequestRefusedException(string message)
        : base(message)
    {
    }

    public PhoneFileRequestRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The PC asked to change files on a phone that has not said it allows that (RemEx-fgmne): its "Let your
/// PC change files" switch is off, or the phone predates the switch.
/// </summary>
/// <remarks>
/// Its own type rather than <see cref="PhoneFileRequestRefusedException"/> because the person has
/// something to DO about this one (turn the switch on, on the phone), so the screen words it differently
/// from "the phone said no".
/// </remarks>
public sealed class PhoneChangesNotAllowedException : InvalidOperationException
{
    public PhoneChangesNotAllowedException()
        : base("The phone has not allowed the PC to change its files.")
    {
    }

    public PhoneChangesNotAllowedException(string message)
        : base(message)
    {
    }

    public PhoneChangesNotAllowedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
