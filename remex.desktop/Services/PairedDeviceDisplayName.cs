namespace Remex.Desktop.Services;

/// <summary>
/// Resolves the friendly name shown for a paired phone (RemEx-9see).
/// </summary>
/// <remarks>
/// <para>
/// **DISPLAY ONLY. THIS NEVER TOUCHES <c>PairedClientRegistry</c>**, and the separation is a safety
/// property rather than tidiness. That registry is the ONLY authentication path in production
/// (CLAUDE.md, High-Risk Areas): a write that corrupted it would brick every paired phone with no
/// clear error on either end. A user renaming a device in a settings card must not be able to reach
/// it, so renaming operates on a separate map keyed by device id and the registry record stays
/// byte-identical.
/// </para>
/// <para>
/// The map holds names only — never a key, a pin or a secret. It is the sort of file a user shares
/// when asking for help, and a friendly name is the one thing in this feature that is safe to see.
/// </para>
/// </remarks>
public static class PairedDeviceDisplayName
{
    /// <summary>
    /// Longest friendly name accepted.
    /// </summary>
    /// <remarks>
    /// A card row is one line. Without a cap, one device renders a wall of text and pushes the
    /// unpair button of every other device off screen — which turns a cosmetic choice into a
    /// usability failure for the rows the user actually needs.
    /// </remarks>
    public const int MaxLength = 48;

    /// <summary>
    /// The name to show for a device.
    /// </summary>
    /// <param name="deviceId">The paired device's identifier.</param>
    /// <param name="names">The user's friendly-name overrides, keyed by device id.</param>
    /// <remarks>
    /// NEVER RETURNS BLANK. A nameless row with an unpair button beside it is a decision the user
    /// cannot make safely — the existing File-Sharing Trust list already renders raw ShortIds and is
    /// described as opaque, and a blank row is strictly worse than opaque. The fallback is the id
    /// itself, which is at least stable and comparable against what the phone shows.
    /// </remarks>
    public static string Resolve(string deviceId, IReadOnlyDictionary<string, string>? names)
    {
        if (names is not null
            && names.TryGetValue(deviceId, out var custom)
            && !string.IsNullOrWhiteSpace(custom))
        {
            return custom;
        }

        return string.IsNullOrWhiteSpace(deviceId) ? UnknownDevice : deviceId;
    }

    /// <summary>Shown when a device has neither a name nor a usable id.</summary>
    /// <remarks>
    /// English here is a placeholder for the caller to localize; this class returns a marker rather
    /// than a sentence so it stays free of resource lookups and testable without a culture.
    /// </remarks>
    public const string UnknownDevice = "(unknown device)";

    /// <summary>
    /// Normalizes a name the user typed, or null to clear the override.
    /// </summary>
    /// <remarks>
    /// **BLANK CLEARS RATHER THAN STORES.** Storing an empty string would leave the device rendering
    /// nameless with no obvious way back, because the field the user would type into is the same one
    /// they just emptied. Clearing restores the id fallback, which is recoverable.
    /// </remarks>
    public static string? Normalize(string? typed)
    {
        if (string.IsNullOrWhiteSpace(typed)) return null;

        var trimmed = typed.Trim();
        return trimmed.Length <= MaxLength ? trimmed : trimmed[..MaxLength].TrimEnd();
    }

    /// <summary>
    /// Applies a rename to the name map, returning a new map.
    /// </summary>
    /// <remarks>
    /// Returns a fresh dictionary rather than mutating, so a caller cannot accidentally publish a
    /// half-applied map to the UI — and so this can never be handed something that also happens to
    /// be backing the registry.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> Rename(
        IReadOnlyDictionary<string, string>? names,
        string deviceId,
        string? typed)
    {
        var updated = names is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(names, StringComparer.Ordinal);

        // A rename for a device with no id has nowhere to go, and inventing a key would create an
        // orphan entry that no row ever reads.
        if (string.IsNullOrWhiteSpace(deviceId)) return updated;

        var normalized = Normalize(typed);
        if (normalized is null) updated.Remove(deviceId);
        else updated[deviceId] = normalized;

        return updated;
    }
}
