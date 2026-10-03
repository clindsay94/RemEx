using System.Diagnostics;

namespace Remex.Desktop.Services.Launching;

/// <summary>Which route <see cref="UserLauncher.Launch"/> took.</summary>
public enum UserLaunchRoute
{
    /// <summary>Started at the user's normal permissions through <see cref="IUnelevatedLauncher"/>.</summary>
    Unelevated,

    /// <summary>Started the way RemEx always did: <c>Process.Start</c> with shell execute, which from
    /// the elevated host means administrator rights. Used when the de-elevated route declined.</summary>
    Standard,
}

/// <summary>
/// The ONE place RemEx opens a user-facing target - an app from the launcher, a web link, a folder
/// (RemEx-pp4cm.2). Every such call site goes through here so the "drop administrator rights, fall
/// back only when that cannot work" rule cannot be forgotten at the next call site.
/// </summary>
/// <remarks>
/// <para>
/// ORDER: the caller validates first (the launcher allowlist and network-path guard stay in
/// <c>AppLauncherService</c>, unchanged), then calls this. Nothing here validates or widens what can
/// be launched; it only decides which token the child gets.
/// </para>
/// <para>
/// FALLBACK: anything other than <see cref="UnelevatedLaunchResult.Launched"/> - elevation required,
/// no shell running, not elevated in the first place, Linux, or an unexpected failure - goes to the
/// standard launch, so nothing that opened before this change stops opening. A <c>null</c> launcher
/// (the embedded host is not running in this process) does the same. Exceptions from the standard
/// launch propagate, exactly as the <c>Process.Start</c> calls this replaced did.
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
    public static UserLaunchRoute Launch(
        IUnelevatedLauncher? unelevated,
        string target,
        string? workingDirectory = null,
        Action<ProcessStartInfo>? standardStart = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(target);

        if (unelevated is not null
            && unelevated.TryLaunch(target, arguments: null, workingDirectory) == UnelevatedLaunchResult.Launched)
        {
            return UserLaunchRoute.Unelevated;
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

    private static void StartWithShell(ProcessStartInfo psi) => Process.Start(psi)?.Dispose();
}
