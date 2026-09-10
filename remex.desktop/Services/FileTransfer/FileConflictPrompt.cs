using Remex.Core.Models;

namespace Remex.Desktop.Services.FileTransfer;

/// <summary>
/// One filename collision, as a state the UI can branch on rather than a sentence it must print.
/// </summary>
/// <remarks>
/// <para>
/// **THIS TYPE IS THE POINT OF THE WHOLE CHANGE.** Before it, a paste that collided set
/// <c>StatusText</c> to the host's own English — "A file with that name already exists." — and the
/// user's only remaining move was to rename something by hand. The host has been sending a
/// machine-readable code for exactly this since RemEx-6vd8; the PC was the one layer that read the
/// prose instead.
/// </para>
/// <para>
/// SO THE PROSE IS NOT STORED HERE. What is stored is the code and the name, and every sentence on
/// screen is looked up from <c>Strings.resx</c> — which is what makes the prompt work in the eight
/// non-English locales this app ships, and what stops a reworded host message silently changing
/// what the PC says.
/// </para>
/// </remarks>
public sealed class FileConflictPrompt
{
    /// <param name="errorCode">The host's <c>errorCode</c>, one of <see cref="FileTransferErrorCodes"/>.</param>
    /// <param name="conflictingName">
    /// The bare name the prompt is about. The caller substitutes the entry's own name when the host
    /// did not say — an older host sends the code and nothing else, and a prompt that cannot name
    /// the file it is asking about is worse than the prose it replaced.
    /// </param>
    public FileConflictPrompt(string? errorCode, string conflictingName)
    {
        ErrorCode = errorCode;
        ConflictingName = conflictingName;
        _actions = FileConflictPolicy.ActionsFor(errorCode);
    }

    private readonly IReadOnlyList<FileConflictAction> _actions;

    /// <summary>The host's machine-readable reason. Never shown; only branched on.</summary>
    public string? ErrorCode { get; }

    /// <summary>The bare name that collided.</summary>
    public string ConflictingName { get; }

    /// <summary>Whether <paramref name="action"/> is one of the answers this collision may be given.</summary>
    /// <remarks>
    /// Checked at the point the answer is RECEIVED as well as where the buttons are drawn. A view
    /// that binds the wrong visibility, or a keyboard route that reaches a hidden button, would
    /// otherwise deliver an answer the policy withheld — and the one withheld most often is the
    /// one that deletes a directory tree.
    /// </remarks>
    public bool Allows(FileConflictAction action) => _actions.Contains(action);

    /// <summary>Whether the destructive answer is on offer. Bound by the view.</summary>
    public bool CanReplace => Allows(FileConflictAction.Replace);

    /// <summary>Whether the host can be asked to invent a free name. Bound by the view.</summary>
    public bool CanKeepBoth => Allows(FileConflictAction.KeepBoth);

    /// <summary>The localized sentence explaining this collision, naming the file.</summary>
    public string Message =>
        string.Format(LocalizationService.Instance[MessageKeyFor(ErrorCode)], ConflictingName);

    /// <summary>Which <c>Strings.resx</c> key explains <paramref name="errorCode"/>.</summary>
    /// <remarks>
    /// SEPARATE FROM <see cref="FileConflictPolicy.ActionsFor"/> ON PURPOSE, and the split is the
    /// interesting part: <c>resolved_name_unusable</c> shares its ACTIONS with every unknown code
    /// but must not share its EXPLANATION. Telling a user who has just chosen "keep both" that
    /// something unspecified went wrong, when the truth is that the invented name was too long for
    /// the destination, is the moment the feature stops being trustworthy.
    /// </remarks>
    public static string MessageKeyFor(string? errorCode) => errorCode switch
    {
        FileTransferErrorCodes.DestinationExists => "FileTransfer_ConflictExistsFormat",
        FileTransferErrorCodes.DestinationIsDifferentKind => "FileTransfer_ConflictDifferentKindFormat",
        FileTransferErrorCodes.ResolvedNameTaken => "FileTransfer_ConflictNameTakenFormat",
        FileTransferErrorCodes.ResolvedNameUnusable => "FileTransfer_ConflictNameUnusableFormat",
        _ => "FileTransfer_ConflictUnknownFormat",
    };
}
