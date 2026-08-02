using Remex.Core.Models;

namespace Remex.Agent.Services.FileTransfer;

/// <summary>
/// A file operation refused because the destination name is taken (RemEx-6vd8).
/// </summary>
/// <remarks>
/// <para>
/// **A TYPE RATHER THAN A MESSAGE, WHICH IS THE WHOLE POINT.** These sites previously threw a plain
/// <see cref="IOException"/> whose message was English prose, and the handler surfaced it as
/// <c>errorMessage</c>. A client could only string-match it — which breaks when the wording improves
/// and cannot work once the host is localized.
/// </para>
/// <para>
/// Still an <see cref="IOException"/>, so every existing <c>catch (IOException)</c> and
/// <c>catch (Exception)</c> keeps working unchanged; the code is additional information for the one
/// handler that looks for it, not a new failure mode.
/// </para>
/// <para>
/// THE PROSE IS KEPT, NOT REPLACED. It remains what a person reads when the client is older than
/// this change, or when the UI has nothing better to show.
/// </para>
/// </remarks>
public sealed class FileConflictException : IOException
{
    public FileConflictException(string errorCode, string conflictingName, string message)
        : base(message)
    {
        ErrorCode = errorCode;
        ConflictingName = conflictingName;
    }

    /// <summary>One of <see cref="FileTransferErrorCodes"/>. Invariant, never localized.</summary>
    public string ErrorCode { get; }

    /// <summary>
    /// The bare name that collided, so the UI can say which file it is asking about.
    /// </summary>
    /// <remarks>
    /// The NAME, not the path: the sheet asks "report.pdf already exists", and a full path would
    /// leak the host's directory layout into a phone dialog for no benefit to the question.
    /// </remarks>
    public string ConflictingName { get; }

    /// <summary>The destination is taken by a file and the caller wanted to put a file there.</summary>
    public static FileConflictException FileExists(string name) =>
        new(FileTransferErrorCodes.DestinationExists, name, $"A file named '{name}' already exists.");

    /// <summary>The destination is taken by a folder and the caller wanted to put a folder there.</summary>
    public static FileConflictException DirectoryExists(string name) =>
        new(FileTransferErrorCodes.DestinationExists, name, $"A folder named '{name}' already exists.");

    /// <summary>
    /// The destination is taken by something of the other kind — a folder where a file was going.
    /// </summary>
    /// <remarks>
    /// Distinguished so a client does not offer "replace", which here would mean deleting a whole
    /// directory tree to make room for one file. Nobody intends that from a copy, and nothing undoes it.
    /// </remarks>
    public static FileConflictException DifferentKindExists(string name) =>
        new(FileTransferErrorCodes.DestinationIsDifferentKind, name,
            $"Something of a different kind already exists at the destination '{name}'.");
}
