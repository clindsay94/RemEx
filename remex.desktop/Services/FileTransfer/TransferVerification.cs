using Remex.Core.Helpers;
using Remex.Desktop.ViewModels;

namespace Remex.Desktop.Services.FileTransfer;

/// <summary>
/// Implemented by a transfer's progress sink when it also wants to hear about the SHA-256 check
/// (2026-10-08 redesign): that the bytes are all across and the hash is being compared, and then the hash
/// that matched. The transfer queue's row implements it, so a finished transfer can say "Verified" and offer
/// the hash instead of a bare "Done".
/// </summary>
/// <remarks>
/// A side interface on the <see cref="IProgress{T}"/> every transfer already takes, rather than a new
/// parameter or return value, because the queue's work delegates and both phone-transfer entry points already
/// pass that one object through. A sink that does not implement it simply never hears; nothing else changes.
/// </remarks>
public interface ITransferVerificationSink
{
    /// <summary>Every byte is across; the two ends are now comparing hashes.</summary>
    void ReportVerifying();

    /// <summary>The hashes matched. <paramref name="sha256Base64"/> is the file's SHA-256 as the wire carries it.</summary>
    void ReportVerified(string sha256Base64);
}

/// <summary>Helpers that reach an <see cref="ITransferVerificationSink"/> through a plain progress reference.</summary>
public static class TransferVerification
{
    /// <summary>Tells <paramref name="progress"/> the hash check has started, if it wants to know.</summary>
    public static void ReportVerifying(this IProgress<TransferProgress>? progress)
    {
        if (progress is ITransferVerificationSink sink)
            sink.ReportVerifying();
    }

    /// <summary>
    /// Tells <paramref name="progress"/> the transfer was verified with <paramref name="sha256Base64"/>, if it
    /// wants to know. A missing or malformed hash is not passed on: a row only says "Verified" for a real one.
    /// </summary>
    public static void ReportVerified(this IProgress<TransferProgress>? progress, string? sha256Base64)
    {
        if (progress is ITransferVerificationSink sink && HashFormat.ToHex(sha256Base64) is not null)
            sink.ReportVerified(sha256Base64!);
    }
}
