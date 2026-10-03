namespace Remex.Desktop.Services.FileTransfer;

/// <summary>
/// What went wrong with a PC-started transfer with a paired phone, when it was the PC's own diagnosis
/// rather than a reason the phone wrote (RemEx-xt0af).
/// </summary>
public enum PhoneTransferProblem
{
    /// <summary>The PC gave no reason it could word more precisely.</summary>
    Unknown,

    /// <summary>The phone cancelled the transfer, or sent an error frame with no reason.</summary>
    PhoneStopped,

    /// <summary>The phone declined and gave no reason of its own.</summary>
    PhoneRefused,

    /// <summary>The file is over the transfer size cap.</summary>
    FileTooLarge,

    /// <summary>The file on this PC changed size between the offer and the send.</summary>
    FileChanged,

    /// <summary>The file name is one RemEx will not transfer.</summary>
    NameNotAllowed,
}

/// <summary>
/// A PC-started transfer with a phone failed for a reason the PC itself diagnosed.
/// </summary>
/// <remarks>
/// A TYPE, NOT A MESSAGE, for the reason <see cref="FileTransferIntegrityException"/> is one
/// (RemEx-s4p4): the transfer queue words it in the user's language from <see cref="ResourceKey"/>,
/// and the English <see cref="Exception.Message"/> is for the log only. Showing the PC's own English
/// diagnosis through <see cref="FileTransferHostException"/> is what this replaced.
/// </remarks>
public sealed class PhoneTransferFailedException : IOException
{
    public PhoneTransferFailedException(PhoneTransferProblem problem, string? logDetail = null)
        : base(logDetail ?? problem.ToString())
        => Problem = problem;

    /// <summary>The diagnosis.</summary>
    public PhoneTransferProblem Problem { get; }

    /// <summary>The <c>Localization/Strings*.resx</c> key that words <see cref="Problem"/>.</summary>
    public string ResourceKey => Problem switch
    {
        PhoneTransferProblem.PhoneStopped => "FileTransfer_ErrPhoneStopped",
        PhoneTransferProblem.PhoneRefused => "FileTransfer_PhoneRefused",
        PhoneTransferProblem.FileTooLarge => "FileTransfer_ErrTooLarge",
        PhoneTransferProblem.FileChanged => "FileTransfer_ErrFileChanged",
        PhoneTransferProblem.NameNotAllowed => "FileTransfer_ErrNameNotAllowed",
        _ => "FileTransfer_ErrGeneric",
    };
}
