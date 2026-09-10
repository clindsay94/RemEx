using Remex.Core.Models;

namespace Remex.Agent.Services.FileTransfer;

/// <summary>What the phone said about a push this PC offered.</summary>
/// <param name="Accepted">Whether the bytes may be sent.</param>
/// <param name="TransferIds">
/// The receiver-minted ids, INDEX-ALIGNED to the offered files. Empty unless accepted.
/// </param>
/// <param name="RefusedReason">Developer-facing English for the log; never shown to a user.</param>
public sealed record FilePushOutcome(
    bool Accepted,
    IReadOnlyList<string> TransferIds,
    string? RefusedReason)
{
    public static FilePushOutcome Refused(string reason) => new(false, [], reason);
}

/// <summary>
/// Interprets a <see cref="FilePushResponse"/> against the offer it is supposed to answer (RemEx-y7my).
/// </summary>
/// <remarks>
/// <para>
/// **SEPARATE FROM THE SOCKET WORK BECAUSE THIS IS WHERE THE PROTOCOL SUBTLETY IS.** Sending a
/// message and awaiting a reply is ordinary plumbing; deciding whether a reply actually authorises
/// pushing somebody's files is not, and it is the part worth testing exhaustively.
/// </para>
/// <para>
/// EVERY REJECTION HERE IS FAIL-CLOSED. This runs on the sending side, so a wrong "yes" means the PC
/// transmits a file the person holding the phone did not agree to receive. There is no case where
/// guessing is better than refusing and letting the caller report that nothing was sent.
/// </para>
/// </remarks>
public static class FilePushNegotiation
{
    /// <summary>
    /// Decides whether a reply authorises the push, and with which ids.
    /// </summary>
    /// <remarks>
    /// <para>
    /// **THE COUNT CHECK IS THE ONE THAT MATTERS.** The phone mints one id per offered file and the
    /// two arrays are related ONLY by position — nothing in the wire format ties an id to a name. If
    /// the counts disagree and we pushed anyway, file N would be sent under file M's transfer id: the
    /// receiver would file the bytes of one document under the name of another, and both sides would
    /// report success. That is worse than a failed transfer, because nothing looks wrong afterwards.
    /// </para>
    /// <para>
    /// The pushId check is the same argument one step earlier: a reply carrying a different id is an
    /// answer to a different question, and consent to send one thing is not consent to send another.
    /// </para>
    /// </remarks>
    public static FilePushOutcome Interpret(string expectedPushId, int offeredFileCount, FilePushResponse? response)
    {
        if (response is null)
        {
            return FilePushOutcome.Refused("no reply arrived before the deadline");
        }

        if (!string.Equals(response.PushId, expectedPushId, StringComparison.Ordinal))
        {
            return FilePushOutcome.Refused(
                $"the reply answers push '{response.PushId}', not '{expectedPushId}'");
        }

        if (!response.Accepted)
        {
            // The ordinary, expected outcome when somebody taps Deny. Not an error.
            return FilePushOutcome.Refused("the phone declined");
        }

        var ids = response.TransferIds ?? [];
        if (ids.Length != offeredFileCount)
        {
            return FilePushOutcome.Refused(
                $"accepted but returned {ids.Length} transfer ids for {offeredFileCount} files, so no id "
                    + "can be trusted to belong to its file");
        }

        if (ids.Any(string.IsNullOrWhiteSpace))
        {
            return FilePushOutcome.Refused("accepted but at least one transfer id is blank");
        }

        return new FilePushOutcome(true, ids, null);
    }

    /// <summary>Builds the offer for a single file, which is all the screenshot flow needs.</summary>
    /// <remarks>
    /// **THE NAME IS VALIDATED HERE, and an earlier version only CLAIMED it was.** This is the name
    /// the receiver files the bytes under and the name its consent prompt shows, so it goes through
    /// the same check the download path uses rather than being trusted because this side produced it.
    /// Returns null when the name is unusable, which the caller reports as a refusal - the same
    /// outcome as a decline, and far better than offering something the receiver must reject.
    /// </remarks>
    public static FilePushOffer? TryOfferOne(string pushId, string fileName, long sizeBytes) =>
        Remex.Core.Validation.FilePathValidation.IsValidFileName(fileName, out _)
            ? new FilePushOffer { PushId = pushId, Files = [new FilePushFile { Name = fileName, Size = sizeBytes }] }
            : null;
}
