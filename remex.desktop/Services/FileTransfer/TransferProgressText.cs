namespace Remex.Desktop.Services.FileTransfer;

/// <summary>
/// Renders <see cref="TransferProgressFormat"/>'s decisions as localized text (RemEx-4lcq).
/// </summary>
/// <remarks>
/// <para>
/// **PORTED AS A SHAPE, NOT AS SHARED CODE**, from <c>remex.android</c>'s
/// <c>TransferProgressText.kt</c> — the wording shape is the same (rate "12.3 MB/s", eta "3 minutes
/// left"/"finishing"), but the mechanics differ everywhere a plural or a locale enters: Android's
/// <c>getQuantityString</c> does the plural selection natively, so this layer additionally calls
/// <see cref="PluralRules"/>, which Android needed nothing built for.
/// </para>
/// <para>
/// **EVERY NUMBER IS FORMATTED AGAINST <see cref="LocalizationService"/>'s CULTURE, NOT THE THREAD'S
/// AMBIENT ONE.** A user who sets RemEx to French on an English-locale Windows install must see
/// "12,4 MB/s", and reading <c>CultureInfo.CurrentCulture</c> would give them "12.4" because RemEx's
/// in-app language switch (<see cref="LocalizationService.SetCulture"/>) does not necessarily match
/// the OS locale. Mixing an OS-locale number into a resource-locale sentence is the specific bug this
/// indirection exists to prevent — the same reasoning <c>TransferProgressText.kt</c> records for
/// reading the configuration's locale instead of <c>Locale.getDefault()</c>.
/// </para>
/// <para>
/// UNIT LETTERS (B/KB/MB/GB) ARE DELIBERATELY LEFT UNTRANSLATED, UNLIKE ANDROID. Android translates
/// them per locale (French "o/ko/Mo/Go" for octet). The PC already shows byte counts elsewhere with
/// hardcoded English letters — <see cref="Converters.BytesToHumanReadableConverter"/>, used
/// throughout the file browser — and a transfer rate that translated its units while every file size
/// on the same screen did not would be a new inconsistency, not a fix. <see cref="RateText"/> reuses
/// that converter's precision rule (no decimal for bytes, one decimal otherwise) so a file's size and
/// its transfer rate render with the same shape.
/// </para>
/// </remarks>
public static class TransferProgressText
{
    /// <summary>The throughput on its own, or null when it is not known.</summary>
    public static string? RateText(TransferRate rate)
    {
        if (rate is not TransferRate.Known known) return null;

        var culture = LocalizationService.Instance.Culture;

        // Bytes per second get no decimal: a tenth of a byte is noise, and it is already the least
        // interesting of the four units. Matches BytesToHumanReadableConverter's own rule.
        var pattern = known.Unit == TransferRateUnit.BytesPerSecond ? "N0" : "F1";
        var amount = $"{known.Amount.ToString(pattern, culture)} {UnitAbbreviation(known.Unit)}";

        return string.Format(culture, LocalizationService.Instance["FileTransfer_RateFormat"], amount);
    }

    /// <summary>The time remaining on its own, or null when it is not known.</summary>
    public static string? EtaText(TransferEta eta) => eta switch
    {
        TransferEta.Finishing => LocalizationService.Instance["FileTransfer_EtaFinishing"],
        TransferEta.Remaining remaining => FormatRemaining(remaining),
        _ => null, // TransferEta.Unknown
    };

    /// <summary>The suffix appended to a transfer row, or null when there is nothing to add.</summary>
    /// <remarks>
    /// NULL RATHER THAN A PLACEHOLDER, mirroring <c>TransferProgressText.kt</c>'s
    /// <c>progressSuffix</c>: until the estimator has seen enough, the caller keeps showing exactly
    /// what it showed before rather than a "calculating" that flickers in and out on every chunk.
    /// </remarks>
    public static string? ProgressSuffix(TransferRate rate, TransferEta eta)
    {
        var rateText = RateText(rate);
        if (rateText is null) return null;

        var etaText = EtaText(eta);
        return etaText is null
            ? rateText
            : string.Format(LocalizationService.Instance["FileTransfer_RateEtaFormat"], rateText, etaText);
    }

    private static string FormatRemaining(TransferEta.Remaining remaining)
    {
        var baseKey = remaining.Unit switch
        {
            TransferEtaUnit.Seconds => "FileTransfer_EtaSecondsFormat",
            TransferEtaUnit.Minutes => "FileTransfer_EtaMinutesFormat",
            TransferEtaUnit.Hours => "FileTransfer_EtaHoursFormat",
            _ => "FileTransfer_EtaSecondsFormat",
        };

        // The category decides which SUFFIX to read, not whether to look one up at all — id and tr
        // always resolve "_Other" because PluralRules.Category never returns anything else for them,
        // which is the "falls back to Other" behaviour without a locale needing to omit a resx entry
        // (the parity checker requires every key present in every file; see PluralRules' remarks).
        var category = PluralRules.Category(LocalizationService.Instance.CultureTag, remaining.Amount);
        var key = $"{baseKey}_{category}";
        var fallbackKey = $"{baseKey}_{PluralCategory.Other}";

        var template = ResolveWithFallback(k => LocalizationService.Instance[k], key, fallbackKey);
        return string.Format(LocalizationService.Instance.Culture, template, remaining.Amount);
    }

    /// <summary>
    /// Looks up <paramref name="key"/>; if <paramref name="lookup"/> could not resolve it (returned
    /// the key itself, <see cref="LocalizationService"/>'s own "not found" signal), falls back to
    /// <paramref name="fallbackKey"/> instead of showing the raw key name on screen.
    /// </summary>
    /// <remarks>
    /// A REVIEW FINDING, NOT A CASE THIS BEAD'S NINE FILES CAN ACTUALLY HIT TODAY: every locale
    /// carries all four plural-suffixed keys (parity is enforced by
    /// <c>scripts/check-localization.ps1</c>), so <paramref name="key"/> always resolves currently.
    /// This exists for the locale that eventually does not — a tenth language added with only
    /// <c>_Other</c> filled in should read as "not translated for every count", not as the developer
    /// key name in every language. <c>internal</c> and taking the lookup as a delegate so a test can
    /// exercise the fallback without needing a genuinely missing resx entry to exist somewhere.
    /// </remarks>
    internal static string ResolveWithFallback(Func<string, string> lookup, string key, string fallbackKey)
    {
        var value = lookup(key);
        return value == key ? lookup(fallbackKey) : value;
    }

    private static string UnitAbbreviation(TransferRateUnit unit) => unit switch
    {
        TransferRateUnit.BytesPerSecond => "B",
        TransferRateUnit.KilobytesPerSecond => "KB",
        TransferRateUnit.MegabytesPerSecond => "MB",
        TransferRateUnit.GigabytesPerSecond => "GB",
        _ => "B",
    };
}
