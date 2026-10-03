using System;
using System.Threading;
using System.Threading.Tasks;
using Remex.Desktop.ViewModels;

namespace Remex.Desktop.Services.FileTransfer;

/// <summary>
/// Moves file bytes between this PC and a paired phone, for the File Transfer screen (RemEx-xt0af).
/// </summary>
/// <remarks>
/// <para>
/// THE HOST DOES THE TRANSFER, NOT THE UI. Bytes ride the phone's own binary <c>/ws/files</c> channel,
/// which the phone opens to the PC, so only the host can reach them. The UI asks for a file and gets
/// told when it has landed; it never sees a frame.
/// </para>
/// <para>
/// Declared here and implemented by the host for the reason given on <see cref="IPhoneFileAccess"/>.
/// </para>
/// </remarks>
public interface IPhoneFileTransfers
{
    /// <summary>
    /// Copies one file from the phone's <paramref name="rootId"/> into <paramref name="localPath"/>,
    /// replacing what is there. Completes once the file is verified and in place.
    /// </summary>
    /// <param name="remoteRelativePath">The file's path inside the shared root, '/'-separated.</param>
    /// <param name="localPath">An absolute path on this PC, chosen by the person at the PC.</param>
    /// <exception cref="PhoneNotConnectedException">The phone is not paired and connected.</exception>
    /// <exception cref="FileTransferHostException">The phone refused, with a reason fit to show.</exception>
    Task DownloadAsync(
        string clientId, string rootId, string remoteRelativePath, string localPath,
        IProgress<TransferProgress>? progress, CancellationToken ct);

    /// <summary>
    /// Copies one local file into the phone's <paramref name="rootId"/>, into
    /// <paramref name="remoteDirectory"/>, keeping its name. Completes once the phone verified it.
    /// </summary>
    /// <param name="remoteDirectory">The destination folder inside the shared root; empty for its top.</param>
    /// <exception cref="PhoneNotConnectedException">The phone is not paired and connected.</exception>
    /// <exception cref="FileTransferHostException">The phone refused, with a reason fit to show.</exception>
    Task UploadAsync(
        string clientId, string localPath, string rootId, string remoteDirectory,
        IProgress<TransferProgress>? progress, CancellationToken ct);
}
