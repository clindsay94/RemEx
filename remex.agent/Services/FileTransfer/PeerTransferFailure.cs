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

    /// <summary>A transfer ended with a failure that carried no reason at all.</summary>
    public const string Failed = "The transfer failed.";

    /// <summary>The caller named no phone. A programming error, not something a person did.</summary>
    public const string PhoneRequired = "A paired phone is required.";

    /// <summary>The caller named no shared folder on the phone. A programming error.</summary>
    public const string RootRequired = "A shared folder on the phone is required.";

    /// <summary>A pull's destination was not a full path to a file on this PC. A programming error.</summary>
    public const string BadDestination = "The destination must be a full path to a file on this PC.";

    /// <summary>The file name failed <c>FilePathValidation</c>; the validator's detail goes to the log.</summary>
    public const string NameNotAllowed = "That file name cannot be transferred.";

    /// <summary>The file to upload is not on this PC any more.</summary>
    public const string LocalFileMissing = "The file on this PC could not be found.";

    /// <summary>The file to upload could not be opened or measured.</summary>
    public const string LocalFileUnreadable = "The file on this PC could not be opened.";

    /// <summary>The file to upload changed size between the offer and the send.</summary>
    public const string LocalFileChanged = "The file on this PC changed size while it was being sent.";

    /// <summary>The file to upload is over the transfer size cap.</summary>
    public const string TooLarge = "The file is too large to send.";

    /// <summary>The phone declined a pull and gave no reason of its own.</summary>
    public const string PhoneDeclined = "The phone declined to send the file.";

    /// <summary>The phone declined an upload and gave no reason of its own.</summary>
    public const string PhoneDidNotAccept = "The phone did not accept the file.";

    /// <summary>
    /// A pulled file verified but could not be landed at the folder the PC chose; the exception's own
    /// detail goes to the log.
    /// </summary>
    public const string SaveFailed = "Verified but could not be saved.";
}
