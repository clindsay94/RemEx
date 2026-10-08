using Remex.Core.Models;

namespace Remex.Core.Validation;

/// <summary>
/// Validates a <see cref="FileReadRangeRequest"/> before a host reads anything (2026-10-08 redesign).
/// </summary>
/// <remarks>
/// The path itself is checked where every other file request's is: the PC resolves it with
/// <see cref="FilePathValidation.ResolveWithinRoot"/>, and the PC's relay checks it with
/// <see cref="FilePathValidation.IsValidRemoteRelativePath"/> before it goes down to a phone. This covers
/// what is new about a range read, which is the numbers.
/// </remarks>
public static class FileReadRangeValidation
{
    /// <summary>
    /// True when <paramref name="request"/> names a root, a non-empty path, a non-negative offset and a
    /// length from 1 to <see cref="FileTransferLimits.ReadRangeMaxBytes"/>. Otherwise false, with a plain
    /// <paramref name="error"/>.
    /// </summary>
    public static bool IsValid(FileReadRangeRequest? request, out string? error)
    {
        error = null;
        if (request is null)
        {
            error = "The read request carried no body.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.RootId))
        {
            error = "No shared folder was named.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.RelativePath?.Trim('/')))
        {
            error = "No file was named.";
            return false;
        }

        if (request.Offset < 0)
        {
            error = "The read offset can't be negative.";
            return false;
        }

        if (request.Length is < 1 or > FileTransferLimits.ReadRangeMaxBytes)
        {
            error = $"A read must be between 1 and {FileTransferLimits.ReadRangeMaxBytes} bytes.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Where a read of <paramref name="length"/> bytes starts in a file of <paramref name="fileSize"/> bytes:
    /// <paramref name="offset"/> itself, or, when <paramref name="fromEnd"/> is true, the last
    /// <paramref name="length"/> bytes (the whole file when it is shorter). Shared by both hosts' callers so
    /// a tail means the same thing on the PC and the phone.
    /// </summary>
    public static long StartOffset(long offset, int length, bool fromEnd, long fileSize) =>
        fromEnd ? Math.Max(0, fileSize - length) : offset;
}
