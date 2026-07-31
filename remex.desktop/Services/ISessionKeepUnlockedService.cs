namespace Remex.Desktop.Services;

/// <summary>
/// Platform service that exposes the opt-in "keep the signed-in session usable while a client is
/// connected" setting to the settings UI. Backed on Windows by the machine-wide flag that
/// remex.agent reads (see <c>SessionGuardSettings</c>) — machine-wide for ACL protection, not
/// because a separate service reads it; there is no service (RemEx-hn5k). Unsupported (and
/// reported as such) elsewhere.
/// (RemEx-l6o)
/// </summary>
public interface ISessionKeepUnlockedService
{
    /// <summary>True only on platforms where the session guard exists (Windows).</summary>
    bool IsSupported { get; }

    /// <summary>Whether the user has explicitly enabled the feature. False when unsupported.</summary>
    bool IsEnabled();

    /// <summary>
    /// Persists the opt-in setting. Returns false if it could not be saved (e.g. insufficient
    /// rights) so the UI can revert the toggle and warn the user instead of silently failing.
    /// </summary>
    bool SetEnabled(bool enabled);
}
