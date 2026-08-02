using Remex.Desktop.ViewModels;

namespace Remex.Desktop.Services;

/// <summary>
/// Decides what a dropped file should become (RemEx-wbhc).
/// </summary>
/// <remarks>
/// <para>
/// Dropping a file on the PC window is the most natural "send this to my phone" gesture, and the
/// code is one enum value away from doing it: <c>FileTransferView</c> already accepts OS drops but
/// enqueues <see cref="FileTransferQueueKind.Upload"/> unconditionally, so every drop goes into the
/// shared root instead.
/// </para>
/// <para>
/// **THE SPLIT ONLY EXISTS WHERE BOTH ANSWERS ARE PLAUSIBLE.** On the transfer view a user might
/// mean either, so the surface offers two zones while a drag is in progress. Everywhere else there
/// is no second meaning, and inventing a split would make the user aim at a target they did not know
/// existed — so those surfaces resolve to one answer and say which.
/// </para>
/// </remarks>
public static class DropZoneResolver
{
    /// <summary>
    /// Which half of a split surface a drop landed in.
    /// </summary>
    /// <param name="pointerY">Drop position, measured from the top of the drop surface.</param>
    /// <param name="surfaceHeight">Height of the drop surface.</param>
    /// <param name="splitFraction">
    /// Where the boundary sits, 0..1 from the top. Defaults to half.
    /// </param>
    /// <remarks>
    /// **A DEGENERATE SURFACE RESOLVES TO SEND-TO-PHONE RATHER THAN TO UPLOAD**, and the direction
    /// matters. Upload writes into the PC's shared root, where a mistaken file is silently added to
    /// a folder the phone can browse; send-to-phone raises a transfer the user watches complete. If
    /// the geometry is unusable, the recoverable, visible answer is the right default.
    /// </remarks>
    public static FileTransferQueueKind ResolveSplit(
        double pointerY, double surfaceHeight, double splitFraction = 0.5)
    {
        if (surfaceHeight <= 0 || double.IsNaN(pointerY)) return FileTransferQueueKind.SendToPhone;

        var boundary = surfaceHeight * Math.Clamp(splitFraction, 0d, 1d);

        // The boundary belongs to the TOP zone, so a drop exactly on the line resolves the same way
        // every time rather than depending on a floating-point comparison landing either side.
        return pointerY <= boundary
            ? FileTransferQueueKind.SendToPhone
            : FileTransferQueueKind.Upload;
    }

    /// <summary>
    /// What a drop means on a surface that offers no choice.
    /// </summary>
    /// <remarks>
    /// HomeView and the main window accept nothing today. Giving them a drop target is worth doing,
    /// but a split there would be a hidden mode: the user has no reason to expect that where they
    /// release the mouse changes what happens. One meaning, stated on the surface.
    /// </remarks>
    public static FileTransferQueueKind ResolveSingleZone() => FileTransferQueueKind.SendToPhone;

    /// <summary>
    /// Whether a set of dropped paths has anything worth enqueueing.
    /// </summary>
    /// <remarks>
    /// An empty drop is not an error to report — the OS can deliver one from a drag that carried no
    /// file data — but it must not enqueue a transfer of nothing, which would surface as a
    /// zero-byte entry the user cannot explain.
    /// </remarks>
    public static bool HasAnythingToSend(IReadOnlyList<string>? paths)
    {
        if (paths is null) return false;

        foreach (var path in paths)
        {
            if (!string.IsNullOrWhiteSpace(path)) return true;
        }

        return false;
    }
}
