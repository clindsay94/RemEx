using System.IO;

namespace Remex.Desktop.Services.FileTransfer;

/// <summary>
/// A download whose bytes did not match the SHA-256 the host advertised.
/// </summary>
/// <remarks>
/// A distinct type rather than an <see cref="IOException"/> with a recognisable message, because the
/// transfer queue has to tell this apart from every other failure in order to localize it — and
/// matching on message text would break the moment the wording changed, silently reverting to
/// showing raw English (RemEx-s4p4).
/// <para>
/// Carries no message worth showing: the partial file has already been deleted and there is nothing
/// the user can act on beyond retrying, so the queue supplies the wording.
/// </para>
/// </remarks>
public sealed class FileTransferIntegrityException : IOException
{
    public FileTransferIntegrityException()
        : base("Download failed: SHA-256 integrity check failed.")
    {
    }
}
