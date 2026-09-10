using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Remex.Core.Services.Clipboard;

namespace Remex.Desktop.Services;

/// <summary>
/// Reads and writes the PC clipboard through Avalonia, on the UI thread (RemEx-hgqs, RemEx-ci98m).
/// </summary>
/// <remarks>
/// <para>
/// **THE UI-THREAD HOP IS THE WHOLE JOB.** Avalonia's clipboard hangs off a <c>TopLevel</c>, and the
/// caller is a WebSocket message handler running on a thread-pool thread with no window in reach.
/// Touching <c>Clipboard</c> from there is undefined at best; on Windows the clipboard is
/// apartment-bound and it simply will not work. Every existing PC clipboard write in this app goes
/// through a view that already lives on the UI thread (<c>RemoteView</c>, <c>DiagnosticLogsView</c>),
/// which is why none of them needed this and this one does.
/// </para>
/// <para>
/// **IT RESOLVES THE WINDOW PER CALL RATHER THAN CACHING ONE.** The main window is not guaranteed to
/// exist when the container builds this service — the embedded host is deliberately built BEFORE any
/// Avalonia statics are touched (see <c>Program.cs</c>) — and a window captured at construction would
/// be stale after a close-and-reopen. Looking it up each time costs nothing next to a clipboard round
/// trip.
/// </para>
/// <para>
/// **NOTHING HERE LOGS THE TEXT**, and the log lines carry a byte count instead. A clipboard holds
/// whatever the user last copied; see <see cref="Remex.Core.Validation.ClipboardValidation"/>, which
/// returns an enum and a length for the same reason.
/// </para>
/// </remarks>
public sealed class AvaloniaHostClipboard(ILogger<AvaloniaHostClipboard> logger) : IHostClipboard
{
    /// <summary>
    /// How long to wait for the UI thread before giving up.
    /// </summary>
    /// <remarks>
    /// A dispatcher that has STOPPED pumping never runs the queued job and never faults it — the
    /// returned task simply hangs. That state is reachable: the user quits the window,
    /// <c>StartWithClassicDesktopLifetime</c> returns, and <c>Program.cs</c>'s <c>finally</c> begins
    /// shutting the embedded host down while a connection is still being served. An unbounded await
    /// there parks a WebSocket receive loop on something that can never complete.
    /// </remarks>
    private static readonly TimeSpan UiThreadTimeout = TimeSpan.FromSeconds(5);

    public Task<string?> GetTextAsync(CancellationToken ct = default)
        // Shares OnUiThreadAsync with the write, which is the point: the dispatcher-binding hazard,
        // the stopped-dispatcher hang and the timeout are identical for a read, and a second copy of
        // that reasoning is a second place for it to rot. Null on any failure - the caller
        // distinguishes "could not read" from "read, and it was empty".
        => OnUiThreadAsync<string?>(
            null,
            // TryGetTextAsync because IClipboard.GetTextAsync is [Obsolete] in Avalonia 11.3. The
            // two have the SAME null semantics - an earlier version of this comment claimed
            // otherwise, and the distinction it described does not exist in either API.
            //
            // A null answer here means "read fine, no TEXT on the clipboard" - an image, a file
            // list, nothing at all - which this method reports as EMPTY, not as unreadable. The
            // unreadable answer is the null returned by the failure paths above, and the phone says
            // different things about the two.
            async clipboard => await clipboard.TryGetTextAsync() ?? string.Empty,
            ct);

    public Task<bool> SetTextAsync(string text, CancellationToken ct = default)
        => OnUiThreadAsync(false, async clipboard =>
        {
            await clipboard.SetTextAsync(text);
            return true;
        }, ct);

    private async Task<T> OnUiThreadAsync<T>(
        T onFailure,
        Func<Avalonia.Input.Platform.IClipboard, Task<T>> work,
        CancellationToken ct,
        // A read failure and a write failure were indistinguishable in the log once these two paths
        // merged, in a feature whose whole point is telling outcomes apart.
        [System.Runtime.CompilerServices.CallerMemberName] string operation = "")
    {
        // **CHECKED BEFORE THE DISPATCHER IS TOUCHED, AND THE ORDER IS THE WHOLE POINT.** The first
        // access to Dispatcher.UIThread BINDS it permanently; do that before AppBuilder has run its
        // platform setup and it binds to Avalonia's null implementation, after which
        // StartWithClassicDesktopLifetime throws PlatformNotSupportedException and the PC app never
        // gets a window or a tray icon at all. That window is real, not theoretical: Program.cs
        // starts the embedded host — Kestrel listening, mDNS advertising — BEFORE it touches any
        // Avalonia statics, so a phone auto-reconnecting off the advert at logon can land a
        // clipboard_push on a thread-pool socket thread while the UI is still starting. The failure
        // would be total, silent, and nowhere near this file. Application.Current reads
        // AvaloniaLocator and initialises no dispatcher, so it is safe to ask from any thread.
        if (Application.Current is null)
        {
            logger.LogWarning("Clipboard {Operation} skipped: the UI has not started yet.", operation);
            return onFailure;
        }

        try
        {
            var write = Dispatcher.UIThread.InvokeAsync(async () =>
            {
                // A headless or already-closed UI is a real state, not an error: the host can be
                // serving a phone before the window exists or after it has gone. Report it and let
                // the caller tell the phone the write did not happen, rather than throwing into a
                // socket loop.
                if (Application.Current?.ApplicationLifetime
                        is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
                {
                    logger.LogWarning("Clipboard {Operation} skipped: no desktop window is available.", operation);
                    return onFailure;
                }

                var clipboard = window.Clipboard;
                if (clipboard is null)
                {
                    logger.LogWarning("Clipboard {Operation} skipped: the window exposes no clipboard.", operation);
                    return onFailure;
                }

                return await work(clipboard);
            });

            // Bounded, and honouring ct, for the stopped-dispatcher case above. Cancelling the token
            // alone would not help: the wait is on a job that was accepted and will never be run.
            return await write.WaitAsync(UiThreadTimeout, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The connection is going away. Expected, not a clipboard fault - logging it as one
            // would put a warning in the operator's log for every shutdown that happens to catch a
            // clipboard call in flight.
            return onFailure;
        }
        catch (Exception ex)
        {
            // Deliberately swallowed to a bool. The caller is a message handler; a clipboard that
            // refuses is something to report, not something to tear a connection down for.
            //
            // TYPE AND MESSAGE, NOT THE EXCEPTION. Logging `ex` in full would rest on an assumption
            // about whether a third party's clipboard implementation ever echoes the string it was
            // handed into an exception or a stack frame - and the string is a password or a 2FA code
            // often enough that the assumption is not worth making for a log line.
            logger.LogWarning(
                "Clipboard {Operation} failed: {Type}: {Message}", operation, ex.GetType().Name, ex.Message);
            return onFailure;
        }
    }
}
