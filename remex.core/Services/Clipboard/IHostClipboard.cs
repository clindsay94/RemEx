namespace Remex.Core.Services.Clipboard;

/// <summary>
/// Writes text to the PC's clipboard (RemEx-hgqs).
/// </summary>
/// <remarks>
/// <para>
/// **THIS EXISTS BECAUSE THE MESSAGE HANDLER CANNOT REACH A CLIPBOARD, AND NOT MERELY FOR TESTING.**
/// Avalonia's clipboard hangs off a <c>TopLevel</c> and must be touched on the UI thread.
/// <c>remex.agent</c> has no <c>Dispatcher.UIThread</c> reference anywhere in it — the PC app's
/// existing clipboard writes are a <c>Func&lt;string, Task&gt;</c> that a VIEW injects into its view
/// model precisely because the view is the thing that owns a top level (see
/// <c>RemoteViewModel.CopyToClipboardAsync</c> for the Wake-on-LAN MAC). A handler resolved from DI
/// has no view to borrow, so the seam is the only way across rather than a convenience.
/// </para>
/// <para>
/// IT RETURNS A BOOL RATHER THAN THROWING because every caller is a message handler, and a clipboard
/// write that fails is a thing to report to the phone, not an exception to unwind a socket loop with.
/// </para>
/// <para>
/// **NO IMPLEMENTATION MAY LOG THE TEXT.** Not at debug level, not truncated, not hashed-and-sampled.
/// The whole feature is trusted with whatever the user last copied; see
/// <see cref="Remex.Core.Validation.ClipboardValidation"/>, which returns an enum and a byte count
/// for the same reason.
/// </para>
/// </remarks>
public interface IHostClipboard
{
    /// <summary>Puts <paramref name="text"/> on the PC clipboard. Returns false if it could not be written.</summary>
    Task<bool> SetTextAsync(string text, CancellationToken ct = default);
}
