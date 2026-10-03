namespace Remex.Agent.Services.FileTransfer;

/// <summary>
/// The reasons <see cref="TransferSessionManager"/> itself gives when a transfer with a phone fails,
/// as opposed to a reason the phone sent (RemEx-xt0af).
/// </summary>
/// <remarks>
/// <para>
/// NAMED SO THE PC CAN TELL ITS OWN WORDS FROM THE PHONE'S. A <c>FileTransferResult.Error</c> carries
/// either, and they deserve different handling on screen: the phone's refusals ("Destination folder not
/// found or read-only.") are shown as the phone wrote them, while these are the PC's own diagnoses and
/// are turned into localized sentences by <see cref="PhoneFileRelay"/>. Matching on these constants is
/// what keeps that mapping from depending on a string somebody re-worded.
/// </para>
/// <para>
/// The values are English and also go to the log. Do not show them to a user directly.
/// </para>
/// </remarks>
public static class PeerTransferFailure
{
    /// <summary>The phone never answered the offer.</summary>
    public const string NoAnswer = "The phone did not answer.";

    /// <summary>Bytes stopped moving for longer than the idle window.</summary>
    public const string StoppedResponding = "The phone stopped responding.";

    /// <summary>The sender drained and the phone never acknowledged the last of it.</summary>
    public const string StoppedAcknowledging = "The phone stopped acknowledging data.";

    /// <summary>The control or file socket went away mid-transfer.</summary>
    public const string ConnectionDropped = "The connection to the phone dropped.";

    /// <summary>The phone accepted but its binary file channel was not there.</summary>
    public const string ChannelMissing = "The phone's file channel is not connected.";

    /// <summary>The sending side was cancelled under the transfer.</summary>
    public const string Stopped = "The transfer was stopped.";

    /// <summary>The phone cancelled the transfer, or sent an error frame with no reason.</summary>
    public const string PhoneStopped = "The phone stopped the transfer.";

    /// <summary>Fewer bytes arrived than were declared.</summary>
    public const string Incomplete = "Transfer incomplete.";

    /// <summary>The bytes that arrived do not hash to what the sender announced.</summary>
    public const string HashMismatch = "SHA-256 mismatch — file corrupted in transit.";
}
