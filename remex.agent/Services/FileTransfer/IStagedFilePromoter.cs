namespace Remex.Agent.Services.FileTransfer;

/// <summary>
/// Lands a verified staging file on an absolute local path the PC itself chose (RemEx-xt0af).
/// </summary>
/// <remarks>
/// <para>
/// A SEPARATE SEAM FROM <c>IFileTransferService.PromoteStagedFileAsync</c>, AND ON PURPOSE. That one
/// resolves a destination inside a SHARED ROOT and authorises the write against the root's settings,
/// because the name it is given comes from a phone. This one takes a destination the person at the PC
/// picked in a save or folder dialog, so there is no root to resolve and nothing to authorise here.
/// Keeping them apart is what stops a phone-supplied path from ever reaching this method.
/// </para>
/// <para>
/// The production implementation is <see cref="FileTransferService"/>, so both paths share one
/// landing: a same-volume rename with the inherited ACL restored, or a copy beside the destination
/// that is renamed onto it.
/// </para>
/// </remarks>
public interface IStagedFilePromoter
{
    /// <summary>Moves <paramref name="stagingPath"/> onto <paramref name="destination"/>, replacing it.</summary>
    Task PromoteStagedFileToPathAsync(string stagingPath, string destination, CancellationToken ct);
}
