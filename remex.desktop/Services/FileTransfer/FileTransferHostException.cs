using System;
using System.IO;

namespace Remex.Desktop.Services.FileTransfer;

/// <summary>
/// A file-transfer failure whose message came from the connected phone and is fit to show a user.
/// </summary>
/// <remarks>
/// This type exists to answer a question the catch sites in <c>FileTransferViewModel</c> previously
/// could not: <em>is this message safe to put on screen?</em> Every failure — a host refusal, a
/// <see cref="TimeoutException"/> from the control-request timeout, a dropped socket, a cancellation
/// — arrived as a plain <see cref="Exception"/>, so the only options were to show all of them or
/// none of them. Both are wrong (RemEx-mznc).
/// <para>
/// The host's replies are genuinely worth showing. <c>FileHostHandler.kt</c> answers a request to
/// pin a folder with "Adding a shared folder must be done on the phone" — copy that tells the user
/// exactly what to do next, which no generic message can replace. That is why the family cannot be
/// collapsed the way RemEx-p9fn collapsed the browse and search sites, and why RemEx-ixsk's review
/// finding — a family of messages may only be collapsed when every member truly shares a cause —
/// applies here.
/// </para>
/// <para>
/// Throwing this type is therefore a PROMISE about <see cref="HostMessage"/>: it is the host's own
/// user-facing sentence, carrying no developer prefix, no exception type name and no stack detail.
/// Do not construct it from a caught exception's message, and do not add a "Something failed: "
/// prefix — the prefix is precisely what this type was introduced to strip. Use
/// <see cref="ForHostError"/>, which also handles the host replying with a blank message.
/// </para>
/// </remarks>
public sealed class FileTransferHostException : IOException
{
    private FileTransferHostException(string hostMessage)
        : base(hostMessage)
        => HostMessage = hostMessage;

    /// <summary>The host's own message, ready to display without further processing.</summary>
    public string HostMessage { get; }

    /// <summary>
    /// Builds the right exception for a host reply, based on whether it actually said anything.
    /// </summary>
    /// <param name="hostMessage">The host's <c>ErrorMessage</c>, which may be null or blank.</param>
    /// <param name="developerContext">
    /// English detail for the log when the host said nothing usable. This is never shown to a user:
    /// a blank host reply produces a plain <see cref="IOException"/>, which the catch sites treat as
    /// "cause unknown" and answer with localized copy.
    /// </param>
    /// <remarks>
    /// The blank case is not theoretical — <c>ErrorMessage is string err</c> matches the empty
    /// string, so without this check a host that set the field but left it empty would blank the
    /// status line and tell the user nothing at all.
    /// </remarks>
    public static IOException ForHostError(string? hostMessage, string developerContext)
        => string.IsNullOrWhiteSpace(hostMessage)
            ? new IOException(developerContext)
            : new FileTransferHostException(hostMessage);
}
