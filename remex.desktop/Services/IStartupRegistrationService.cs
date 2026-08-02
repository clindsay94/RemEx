namespace Remex.Desktop.Services;

/// <summary>
/// Platform service that manages registering the client application for autostart/launch-at-login.
/// </summary>
public interface IStartupRegistrationService
{
    bool IsSupported { get; }
    /// <summary>Whether autostart is registered, treating an unanswerable query as "no".</summary>
    /// <remarks>
    /// Kept for callers that genuinely cannot act on "do not know". Prefer <see cref="TryIsEnabled"/>
    /// anywhere the difference is visible to a user.
    /// </remarks>
    bool IsEnabled();

    /// <summary>
    /// Whether autostart is registered, or <see langword="null"/> when that could not be
    /// established (RemEx-h5lr).
    /// </summary>
    /// <remarks>
    /// **THE DISTINCTION <see cref="IsEnabled"/> CANNOT MAKE, AND IT IS USER-VISIBLE.** The Windows
    /// path shells out to <c>schtasks /Query</c> and the Linux path reads a file; both can fail for
    /// reasons that say nothing about whether the task exists — an EDR blocking the executable, an
    /// unavailable Task Scheduler RPC endpoint, an unreadable <c>~/.config/autostart</c>. IsEnabled
    /// reports every one of those as <see langword="false"/>, so the settings switch shows "off" for
    /// a machine whose autostart is registered and working.
    /// <para>
    /// That matters twice over: the switch is also the control that WRITES, so a user who "corrects"
    /// a false negative is issuing a real registration against a state nobody established, and a
    /// caller that seeds the toggle from a false negative would write that lie back.
    /// </para>
    /// </remarks>
    bool? TryIsEnabled();
    void SetEnabled(bool enabled);
}
