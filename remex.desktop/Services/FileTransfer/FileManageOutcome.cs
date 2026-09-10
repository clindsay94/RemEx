using Remex.Core.Models;

namespace Remex.Desktop.Services.FileTransfer;

/// <summary>
/// What a <c>file_manage</c> request actually came back with, structured rather than collapsed.
/// </summary>
/// <remarks>
/// <para>
/// **THE THREE FIELDS BELOW THE MESSAGE ARE THE WHOLE POINT.** <c>ManageAsync</c> used to read
/// <see cref="FileManageResponse.Success"/> and nothing else, so <c>errorCode</c>,
/// <c>conflictingName</c> and <c>resolvedName</c> — every structured thing the host says about a
/// filename collision — were deserialized and thrown away. The PC was left doing what
/// <see cref="FileTransferErrorCodes"/> exists to stop a client doing: showing the host's English
/// prose and offering no answer to it.
/// </para>
/// <para>
/// A RECORD RATHER THAN AN EXCEPTION, because a collision is an ANSWER and not a failure. The
/// caller has a question to put to the user — replace, keep both, skip — and then a retry to
/// issue. Every other refusal still arrives as <see cref="FileTransferHostException"/>, so the
/// existing catch sites keep working unchanged; see <c>FileTransferClient.CopyRemoteAsync</c> for
/// where the split is made.
/// </para>
/// </remarks>
/// <param name="Success">Whether the host performed the operation.</param>
/// <param name="ErrorMessage">The host's own prose. Shown only when there is no code to act on.</param>
/// <param name="ErrorCode">One of <see cref="FileTransferErrorCodes"/>, or null.</param>
/// <param name="ConflictingName">The bare name that collided, so the prompt can name it.</param>
/// <param name="ResolvedName">
/// The name the host actually used, set only when "keep both" changed it. Non-null on a SUCCESS,
/// which is the case that matters: a rename the user is never told about leaves them believing
/// they have <c>report.pdf</c> when the file on disk is <c>report (2).pdf</c>.
/// </param>
public readonly record struct FileManageOutcome(
    bool Success,
    string? ErrorMessage,
    string? ErrorCode,
    string? ConflictingName,
    string? ResolvedName)
{
    /// <summary>A refusal the user can answer, rather than one they can only be told about.</summary>
    /// <remarks>
    /// Keyed on the CODE being present rather than on which code it is. An unrecognised code still
    /// gets a prompt — <see cref="FileConflictPolicy.ActionsFor"/> narrows it to Skip and Cancel —
    /// because a newer host saying "this is a collision" in a dialect this build does not speak is
    /// still saying it is a collision, and the safe answers are safe against every meaning.
    /// </remarks>
    public bool IsConflict => !Success && !string.IsNullOrWhiteSpace(ErrorCode);

    /// <summary>The outcome for a host that answered without a <c>file_manage_response</c> at all.</summary>
    /// <remarks>
    /// Pre-existing behaviour, kept deliberately: the previous code tested
    /// <c>response.FileManageResponse?.Success == false</c>, so a missing payload read as success.
    /// Changing that here would turn a silent no-op into a user-visible failure on a path no bead
    /// has looked at.
    /// </remarks>
    public static FileManageOutcome Succeeded { get; } =
        new(Success: true, ErrorMessage: null, ErrorCode: null, ConflictingName: null, ResolvedName: null);

    /// <summary>Reads the outcome off the host's reply.</summary>
    public static FileManageOutcome From(FileManageResponse? response) =>
        response is null
            ? Succeeded
            : new FileManageOutcome(
                response.Success,
                response.ErrorMessage,
                response.ErrorCode,
                response.ConflictingName,
                response.ResolvedName);
}
