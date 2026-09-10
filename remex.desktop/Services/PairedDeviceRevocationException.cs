namespace Remex.Desktop.Services;

/// <summary>
/// A revocation that did not finish, and specifically whether the PAIRING survived it (RemEx-pynli).
/// </summary>
/// <remarks>
/// <para>
/// **THE TWO FAILURES NEED DIFFERENT WORDS BECAUSE THEY NEED DIFFERENT ACTIONS.** The credential store
/// removes the client from memory and THEN persists, so a failed write leaves the device unpaired for
/// this run and still present on disk: the next time RemEx starts, the pairing is back and the phone
/// can authenticate again. The action is to unpair it again after the restart. Reporting that as
/// "something went wrong" tells a user whose pairing will return exactly as much as it tells one
/// whose pairing is gone.
/// </para>
/// <para>
/// THE FALSE CASE IS "NOT COMING BACK", NOT "HARMLESS" (review). An earlier draft of this comment
/// called every non-credential failure a stale name or date with nothing to do about it. That is true
/// of the three record stores and false of the trust store, whose leftover row grants FILE ACCESS to
/// a device that pairs again — which is more consequential than a returning pairing, and which the
/// user can act on from the Trusted Devices card. The flag says only what it says: whether this
/// pairing survives a restart.
/// </para>
/// <para>
/// A TYPE RATHER THAN A MESSAGE, because the words belong to the UI. The host cannot build a
/// localized string — it does not know the user's culture and must not own their vocabulary — so it
/// reports the FACT and the view model chooses the sentence.
/// </para>
/// </remarks>
public sealed class PairedDeviceRevocationException(
    string message, bool pairingMayReturn, IReadOnlyList<Exception> failures)
    : Exception(message, failures is { Count: > 0 } ? failures[0] : null)
{
    /// <summary>
    /// True when the credential itself could not be persisted, so the device may be paired again the
    /// next time the host starts.
    /// </summary>
    public bool PairingMayReturn { get; } = pairingMayReturn;

    /// <summary>Every teardown that failed, in the order they were attempted. Never empty.</summary>
    /// <remarks>
    /// An empty list is rejected rather than tolerated: this type means "something failed", and the
    /// only producer constructs it inside a <c>Count &gt; 0</c> check. A fallback for the empty case
    /// would have been unreachable code whose one job was to put the untranslated summary back on
    /// screen — dead, and dead in the wrong direction (review).
    /// </remarks>
    public IReadOnlyList<Exception> Failures { get; } = failures is null
        ? throw new ArgumentNullException(nameof(failures))
        : failures.Count > 0
            ? failures
            : throw new ArgumentException("A revocation failure carries at least one cause.", nameof(failures));

    /// <summary>What actually went wrong, for showing to a person.</summary>
    /// <remarks>
    /// **NOT <see cref="Exception.Message"/>, WHICH IS A CONSTANT.** This type replaced an
    /// <see cref="AggregateException"/>, and that one appended each inner message in parentheses — so
    /// formatting <c>Message</c> into a status line used to yield "…the revocation is incomplete.
    /// (Access to the path 'paired.json' is denied.)" and now yields only the half a user cannot act
    /// on. Worse, in eight of the nine locales that constant is the one untranslated English sentence
    /// on the screen. The summary belongs in the log; the reason belongs on the screen (review).
    /// </remarks>
    public string Reason => string.Join("; ", Failures.Select(f => f.Message));
}
