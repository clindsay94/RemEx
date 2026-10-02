using Remex.Core.Models;

namespace Remex.Core.Validation;

/// <summary>
/// The one rule both ends of the pinned-sensor sync apply to a sensor name (RemEx-wqo7a.5).
/// </summary>
/// <remarks>
/// <para>
/// Inbound on the host, a <c>home_pins_change</c> that fails <see cref="IsValidChange"/> is logged at
/// Warning and dropped with no reply: the phone's optimistic toggle is then corrected by the next
/// <c>home_pins_sync</c>, which is the reply it needed anyway. Outbound, the host runs every list it
/// publishes through <see cref="NormalizeNames"/>, so the phone never has to defend against a list
/// the host itself would refuse.
/// </para>
/// <para>
/// NativeAOT-safe: no reflection, no regex, no allocation beyond the result list.
/// </para>
/// </remarks>
public static class HomePinsValidation
{
    /// <summary>Longest sensor name accepted, in UTF-16 code units.</summary>
    /// <remarks>
    /// Hardware sensor names run to ~60 characters ("CPU Core #12 Distance to TjMax"); 200 leaves room
    /// for any real one and keeps a hostile list of 100 names to ~40 KB.
    /// </remarks>
    public const int MaxNameLength = 200;

    /// <summary>Most names a list may carry; extras beyond this are dropped, first ones kept.</summary>
    public const int MaxListCount = 100;

    /// <summary>
    /// Whether <paramref name="name"/> may identify a sensor: not blank, at most
    /// <see cref="MaxNameLength"/> long, and free of control characters.
    /// </summary>
    /// <remarks>
    /// NOT TRIMMED, AND NOT REJECTED FOR SURROUNDING SPACES. Identity is the name exactly as the host's
    /// sensor library reports it, so trimming here would make a name that really ends in a space
    /// unpinnable — it would never match its own reading again.
    /// </remarks>
    public static bool IsValidSensorName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength)
        {
            return false;
        }

        foreach (var c in name)
        {
            if (char.IsControl(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether a phone's pin change may be applied.</summary>
    public static bool IsValidChange(HomePinChange? change)
        => change is not null && IsValidSensorName(change.SensorName);

    /// <summary>
    /// The list as both sides may rely on it: invalid names dropped, duplicates removed
    /// case-insensitively keeping the FIRST spelling and position, and at most
    /// <see cref="MaxListCount"/> names. Null (an absent JSON list) reads as empty.
    /// </summary>
    public static List<string> NormalizeNames(IEnumerable<string?>? names)
    {
        var result = new List<string>();
        if (names is null)
        {
            return result;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            if (result.Count == MaxListCount)
            {
                break;
            }

            if (IsValidSensorName(name) && seen.Add(name!))
            {
                result.Add(name!);
            }
        }

        return result;
    }

    /// <summary>
    /// A copy of <paramref name="sync"/> with both lists normalized, or null when there is no sync.
    /// </summary>
    /// <remarks>
    /// Revision and timestamp are carried through untouched: ordering is the receiver's decision, and a
    /// negative or zero revision is simply older than anything the host has actually sent.
    /// </remarks>
    public static HomePinnedSensors? Normalize(HomePinnedSensors? sync)
        => sync is null
            ? null
            : sync with
            {
                SensorNames = NormalizeNames(sync.SensorNames),
                PinnableSensorNames = NormalizeNames(sync.PinnableSensorNames),
            };
}
