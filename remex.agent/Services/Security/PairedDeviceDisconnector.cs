using Microsoft.Extensions.Logging;
using Remex.Agent.Services.FileTransfer;
using Remex.Agent.Services.RemoteDesktop;

namespace Remex.Agent.Services.Security;

/// <summary>
/// Cuts every live connection belonging to a device whose pairing has just been revoked
/// (RemEx-6nkht).
/// </summary>
/// <remarks>
/// <para>
/// **REVOKING A PAIRING USED TO ONLY AFFECT THE NEXT CONNECTION.** <c>IsClientPaired</c> is consulted
/// when a connection is ESTABLISHED — <c>EvaluateDesktopAuth</c>, <c>EvaluateFilesAuth</c>, the /ws
/// pairing gate — and nowhere afterwards. So a phone that was already mirroring the desktop kept
/// mirroring it, and kept controlling it, until it happened to disconnect. The user unpairs precisely
/// because they no longer trust whoever is holding that phone, and the confirmation tells them the
/// device "will no longer be able to connect to this PC". This is what makes that sentence true now
/// rather than at some unspecified later moment.
/// </para>
/// <para>
/// THREE CHANNELS, THREE OWNERS, AND THE TEARDOWN BELONGS TO EACH OWNER. The control channel is a
/// socket in <see cref="ClientSessionRegistry"/>; the screen stream is a loop with a drain contract in
/// <see cref="DesktopSessionRegistry"/>; the file channel is a socket with a partial-preserving
/// <c>finally</c> in <see cref="TransferSessionManager"/>. Each is asked to end its own — a fourth
/// registry holding sockets across all three would have to re-derive three different lifetimes and
/// would get the file one wrong, because ending that channel by its socket is only correct BECAUSE
/// its own <c>finally</c> suspends the partials.
/// </para>
/// <para>
/// BEST EFFORT, AND EACH CHANNEL INDEPENDENTLY SO. One channel failing to close must not leave the
/// other two open, and none of the three is load-bearing for security on its own: the credential is
/// already gone by the time this runs, so the worst case a failure here produces is a connection that
/// survives until its next authentication rather than one that survives forever. That is also why a
/// failure is logged rather than added to the revocation's failure list — a revocation that reported
/// itself incomplete because a socket was already closing would train the user to ignore the message
/// that matters.
/// </para>
/// </remarks>
public sealed class PairedDeviceDisconnector(
    ClientSessionRegistry sessions,
    DesktopSessionRegistry desktops,
    TransferSessionManager transfers,
    ILogger<PairedDeviceDisconnector> logger) : IPairedDeviceDisconnector
{
    /// <summary>How long to wait for a cancelled screen stream to finish draining before moving on.</summary>
    /// <remarks>
    /// A constant with no test seam, and the honest reason is blast radius rather than difficulty —
    /// an init property would have made the timeout branch injectable easily enough (review). A
    /// regression in that branch costs the bound on a wait and nothing else: the cancel has already
    /// been delivered by the time the budget matters, which is why the timeout path still reports the
    /// channel as cut.
    /// </remarks>
    private static readonly TimeSpan DrainBudget = TimeSpan.FromSeconds(2);

    public async Task DisconnectAsync(string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return;

        // OFF THE CALLER'S THREAD, AND THIS IS THE POINT OF THE Task.Run RATHER THAN A HABIT (review).
        // The caller is a Settings button, so it arrives on the Avalonia dispatcher — and
        // ConfigureAwait is banned repo-wide, so a continuation would stay there. Aborting a socket
        // completes its parked ReceiveAsync on the aborting thread, and the handler's whole unwind
        // follows from that: PingPongHandler's held-key release, the desktop loop's pacer and capture
        // teardown, on Linux a set of D-Bus portal round-trips. Every one of those would otherwise run
        // on the UI thread with dispatcher frames beneath it, which is the exact shape of the freezes
        // this repo has already shipped twice (RemEx-e3pn, RemEx-rbfq).
        await Task.Run(async () =>
        {
            var control = Attempt("control", () => sessions.DisconnectClient(clientId) > 0);
            var desktop = await AttemptAsync("desktop", () => CancelDesktopWithinBudgetAsync(clientId));
            var files = Attempt("files", () => transfers.DisconnectClient(clientId));

            Log(() => logger.LogInformation(
                "Cut live connections for revoked device {ClientId}: control={Control}, desktop={Desktop}, files={Files}.",
                LogRedaction.RedactClientId(clientId), control, desktop, files));
        }, CancellationToken.None);

        bool Attempt(string channel, Func<bool> cut)
        {
            try
            {
                return cut();
            }
            catch (Exception ex)
            {
                Failed(channel, ex);
                return false;
            }
        }

        async Task<bool> AttemptAsync(string channel, Func<Task<bool>> cut)
        {
            try
            {
                return await cut();
            }
            catch (Exception ex)
            {
                Failed(channel, ex);
                return false;
            }
        }

        void Failed(string channel, Exception ex) => Log(() => logger.LogWarning(
            ex, "Could not cut the {Channel} channel for revoked device {ClientId}.",
            channel, LogRedaction.RedactClientId(clientId)));

        // BOUNDED, because CancelAsync completes only once every cancellation callback has run and
        // the revocation awaits this before the UI refreshes (review). TakeOverAsync bounds the same
        // wait with its drainTimeout for the same reason; a wedged capture teardown must not leave a
        // confirmation hanging. The cancel is already delivered by then — the budget is on WAITING for
        // the drain, not on asking for it — so the outcome is reported as cut either way.
        //
        // THIS ONLY BOUNDS ANYTHING BECAUSE CancelSessionsForAsync USES CancelAsync. A plain Cancel
        // would run the callbacks before returning a Task at all, and WhenAny would be reached after
        // the wait it is meant to cap. Its doc comment says so; do not simplify one without the other.
        async Task<bool> CancelDesktopWithinBudgetAsync(string id)
        {
            var cancelling = desktops.CancelSessionsForAsync(id);
            if (await Task.WhenAny(cancelling, Task.Delay(DrainBudget)) == (Task)cancelling)
            {
                return await cancelling;
            }

            Failed("desktop", new TimeoutException(
                $"The desktop session did not drain within {DrainBudget.TotalSeconds:0.#}s of being cancelled."));
            return true;
        }

        // THE LOGGING IS INSIDE THE ISOLATION, NOT OUTSIDE IT (review). Reporting a failed cut used to
        // happen in the catch block itself, unguarded — so a logger that threw (a disposed provider
        // during shutdown, a broken sink) escaped the first channel's Attempt and the other two were
        // never cut at all. Worse, it escaped as far as the revocation's result, where it would have
        // pre-empted the AggregateException about actual store failures and been shown to the user as
        // the revocation's outcome. Nothing about a log line may decide any of that.
        static void Log(Action write)
        {
            try { write(); }
            catch { /* a failed log must never cost a channel */ }
        }
    }
}
