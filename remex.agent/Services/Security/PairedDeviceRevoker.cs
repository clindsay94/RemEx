using Microsoft.Extensions.Logging;
using Remex.Core.Services.FileTransfer;
using Remex.Desktop.Services;

namespace Remex.Agent.Services.Security;

/// <summary>
/// Ends a pairing and forgets everything recorded about the device (RemEx-5lb90).
/// </summary>
/// <remarks>
/// <para>
/// **THIS IS THE FIRST PRODUCTION CALLER OF <see cref="PairedClientRegistry.UnregisterClient"/>.**
/// That registry is the only authentication path in production; a break in it silently bricks every
/// device pairing. Nothing else in this type does anything clever, deliberately — the whole job is
/// to call five teardowns and one disconnect in one place so that none of them can be forgotten.
/// </para>
/// <para>
/// FIVE STORES, AND MISSING ONE IS INVISIBLE UNTIL THE DEVICE PAIRS AGAIN. The registry holds the
/// credential; <see cref="PairedClientNameStore"/> what the device reported;
/// <see cref="PairedDeviceNameOverrideStore"/> what the user chose;
/// <see cref="PairedDeviceActivityStore"/> when it paired and was last seen; and
/// <see cref="IFileTrustService"/> the file-transfer grants. A leftover row in the middle three
/// resurfaces against a re-paired device wearing the identity of a pairing the user deliberately
/// ended. A leftover TRUST row is worse than cosmetic: that store is pruned lazily, only for clients
/// that are no longer paired, so a row that survives a revocation stops being prunable the moment the
/// device pairs again — it is simply live, and the "new" pairing silently inherits full-device browse
/// with no consent prompt (review of RemEx-5lb90).
/// </para>
/// <para>
/// THE CREDENTIAL GOES FIRST, AND EVERY LATER TEARDOWN RUNS ANYWAY. First because it is the one that
/// grants access, so if the process dies mid-revocation the access is already the thing that went.
/// "Anyway" because the first review of this class assumed a throw could only come from a late
/// teardown and that was backwards: the three record stores swallow their own IO failures, and the
/// registry — the FIRST call — is the one that propagates. Letting it short-circuit would have left a
/// device unpaired in memory while keeping every record, and cleared nothing at all after a restart.
/// So failures are collected and rethrown at the end, which is also what lets the UI tell the user
/// that a revocation only half-happened instead of showing a row quietly vanishing.
/// </para>
/// </remarks>
public sealed class PairedDeviceRevoker(
    PairedClientRegistry registry,
    PairedClientNameStore names,
    PairedDeviceNameOverrideStore overrides,
    PairedDeviceActivityStore activity,
    IFileTrustService fileTrust,
    IPairedDeviceDisconnector disconnector,
    ILogger<PairedDeviceRevoker> logger) : IPairedDeviceRevoker
{
    public async Task RevokeAsync(string clientId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return;

        List<Exception>? failures = null;

        // TRACKED SEPARATELY FROM THE REST, because it is the only failure the user can act on
        // (RemEx-pynli). The registry removes from memory and then persists, so a failed write here
        // means the device is unpaired for this run and still on disk — it comes back on the next
        // start. Every other store failing leaves a stale name or date, which is invisible and
        // nothing anyone can do anything about.
        var pairingMayReturn = false;

        void Attempt(Action teardown)
        {
            try
            {
                teardown();
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }
        }

        Attempt(() => registry.UnregisterClient(clientId));
        pairingMayReturn = failures is { Count: > 0 };
        Attempt(() => names.Forget(clientId));
        Attempt(() => overrides.Forget(clientId));
        Attempt(() => activity.Forget(clientId));

        try
        {
            await fileTrust.RevokeAsync(clientId, ct);
        }
        catch (Exception ex)
        {
            (failures ??= []).Add(ex);
        }

        // AFTER THE STORES, NOT BEFORE THEM, AND OUTSIDE THE FAILURE LIST. The credential is what
        // decides whether the device may come back, so it goes first and this closes the door behind
        // it; cutting the sockets first would leave a window in which the phone reconnects, is still
        // paired, and is simply back. It is deliberately not part of `failures`: the disconnect is
        // best effort by construction, and a revocation that reported itself incomplete because a
        // socket was already closing would train the user to ignore the message that matters.
        await disconnector.DisconnectAsync(clientId);

        if (failures is { Count: > 0 })
        {
            // WITH THE EXCEPTIONS, not just a count (review). The support question this log exists to
            // answer is "did the file-access grant actually go?" — the one with privilege attached —
            // and a count cannot answer it. The UI shows the same text for three seconds and then it
            // is gone, so if it is not here it is nowhere.
            var failure = new PairedDeviceRevocationException(
                "One or more paired-device teardowns failed; the revocation is incomplete.",
                pairingMayReturn, failures);
            logger.LogError(
                failure,
                "Revoking {ClientId} left {FailureCount} teardown(s) unfinished (pairingMayReturn={MayReturn}).",
                LogRedaction.RedactClientId(clientId), failures.Count, pairingMayReturn);
            throw failure;
        }

        // The registry and the trust store log their own revocations, redacted. This line says the
        // RECORDS went too, so a support log can distinguish "unpaired" from "unpaired and still
        // carrying old rows" — which is why it is AFTER the failure check rather than before it. Run
        // unconditionally, it asserted the clean outcome in the one case where the two differ.
        logger.LogInformation(
            "Paired device records cleared for {ClientId} (name, user override, activity, file trust).",
            LogRedaction.RedactClientId(clientId));
    }
}
