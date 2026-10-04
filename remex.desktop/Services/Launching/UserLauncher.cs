using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Remex.Core.Logging;

namespace Remex.Desktop.Services.Launching;

/// <summary>Which route <see cref="UserLauncher.Launch"/> took.</summary>
public enum UserLaunchRoute
{
    /// <summary>Started at the user's normal permissions through <see cref="IUnelevatedLauncher"/>.</summary>
    Unelevated,

    /// <summary>Started the way RemEx always did: <c>Process.Start</c> with shell execute, which from
    /// the elevated host means administrator rights. Only taken for the results that allow it.</summary>
    Standard,
}

/// <summary>
/// Thrown by <see cref="UserLauncher.Launch"/> when a target could not be opened at normal
/// permissions and the result does not allow the elevated fallback
/// (<see cref="UnelevatedLaunchResult.Failed"/>). Nothing was started.
/// </summary>
public sealed class UserLaunchFailedException : InvalidOperationException
{
    public UserLaunchFailedException()
        : base("The target could not be opened with the user's normal permissions, and RemEx does not open "
            + "links, documents or folders with administrator rights instead.")
    {
    }
}

/// <summary>
/// The ONE place RemEx opens a user-facing target - an app from the launcher, a web link, a folder
/// (RemEx-pp4cm.2). Every such call site goes through here so the "drop administrator rights, fall
/// back only when that is safe" rule cannot be forgotten at the next call site.
/// </summary>
/// <remarks>
/// <para>
/// ORDER: the caller validates first (the launcher allowlist and network-path guard stay in
/// <c>AppLauncherService</c>, unchanged), then calls this. Nothing here validates or widens what can
/// be launched; it only decides which token the child gets.
/// </para>
/// <para>
/// FALLBACK IS NARROW (security review of RemEx-pp4cm.2). The old elevated launch runs only for
/// <see cref="UnelevatedLaunchResult.ElevationRequired"/> (the target asks for it),
/// <see cref="UnelevatedLaunchResult.NotNeeded"/> (not elevated / Linux - the old launch is already at
/// user level), <see cref="UnelevatedLaunchResult.NoShell"/> (no desktop shell exists at all), and
/// <see cref="UnelevatedLaunchResult.ProgramRouteUnavailable"/> (a launcher-list program, no worse than
/// before). <see cref="UnelevatedLaunchResult.Failed"/> - a link, document or folder that could not
/// be opened at normal permissions - throws <see cref="UserLaunchFailedException"/> instead, because
/// falling back there would open an administrator browser, which is the bug this fixes. A <c>null</c>
/// launcher (the embedded host is not running in this process) keeps the old launch.
/// </para>
/// </remarks>
public static class UserLauncher
{
    /// <summary>
    /// Opens <paramref name="target"/> for the signed-in user.
    /// </summary>
    /// <param name="unelevated">The de-elevation mechanism, or <c>null</c> when none is available.</param>
    /// <param name="target">A validated file or folder path, or an absolute URL.</param>
    /// <param name="workingDirectory">Working directory for an executable target, or <c>null</c>.</param>
    /// <param name="standardStart">Test seam for the fallback launch; <c>null</c> means the real
    /// <c>Process.Start</c>. Production callers never pass it.</param>
    /// <exception cref="UserLaunchFailedException">The target could not be opened at normal
    /// permissions and may not be opened elevated. Nothing was started.</exception>
    public static UserLaunchRoute Launch(
        IUnelevatedLauncher? unelevated,
        string target,
        string? workingDirectory = null,
        Action<ProcessStartInfo>? standardStart = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(target);

        if (unelevated is not null)
        {
            switch (unelevated.TryLaunch(target, arguments: null, workingDirectory))
            {
                case UnelevatedLaunchResult.Launched:
                    return UserLaunchRoute.Unelevated;
                case UnelevatedLaunchResult.Failed:
                    throw new UserLaunchFailedException();
            }
        }

        var psi = new ProcessStartInfo
        {
            FileName = target,
            // Shell execute is what makes documents, folders and URLs open through their file
            // association; it is the behaviour every call site had before RemEx-pp4cm.2.
            UseShellExecute = true,
            WorkingDirectory = workingDirectory ?? string.Empty,
        };
        (standardStart ?? StartWithShell)(psi);
        return UserLaunchRoute.Standard;
    }

    /// <summary>
    /// <see cref="Launch"/> for the PC app's own links and folders: a
    /// <see cref="UserLaunchFailedException"/> is logged and shown to the user as a notification
    /// ("Couldn't open it") rather than thrown. Other exceptions still propagate to the caller's own
    /// handling, as before.
    /// </summary>
    /// <param name="notify">Test seam receiving (title, message); <c>null</c> means the real
    /// <see cref="NotificationService"/>.</param>
    /// <returns><c>false</c> when the target could not be opened.</returns>
    public static bool LaunchOrNotify(
        IUnelevatedLauncher? unelevated,
        string target,
        Action<ProcessStartInfo>? standardStart = null,
        Action<string, string>? notify = null)
    {
        try
        {
            Launch(unelevated, target, workingDirectory: null, standardStart);
            return true;
        }
        catch (UserLaunchFailedException ex)
        {
            InMemoryLogSink.Append(LogLevel.Warning, "Launch", "Could not open a link or folder with normal permissions", ex);
            (notify ?? NotifyUser)(
                LocalizationService.Instance["Launch_OpenFailed_Title"],
                LocalizationService.Instance["Launch_OpenFailed_Message"]);
            return false;
        }
    }

    private static void NotifyUser(string title, string message)
        => NotificationService.Instance.Notify(NotificationImportance.Outcome, title, message);

    private static void StartWithShell(ProcessStartInfo psi) => Process.Start(psi)?.Dispose();
}
