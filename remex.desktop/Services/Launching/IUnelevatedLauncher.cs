namespace Remex.Desktop.Services.Launching;

/// <summary>
/// Opens a program, document, folder or web link as the signed-in user at their NORMAL permission
/// level, even though RemEx itself runs as administrator (RemEx-pp4cm.2).
/// </summary>
/// <remarks>
/// <para>
/// WHY THIS EXISTS: remex.agent runs elevated on purpose (CLAUDE.md "Elevation is load-bearing"),
/// and a plain <c>Process.Start(new ProcessStartInfo { UseShellExecute = true })</c> from an elevated
/// process hands the child the same administrator token with no UAC prompt. Every app launched from
/// the phone or the Apps page, and every browser a link opened, ran as administrator.
/// </para>
/// <para>
/// THIS IS ONLY THE MECHANISM. Callers never use it directly: they go through
/// <see cref="UserLauncher.Launch"/>, which owns the fallback to the old (elevated) launch so that a
/// target that genuinely needs administrator rights, or a PC with no desktop shell running, keeps
/// working exactly as before. The interface lives in remex.desktop, not remex.agent, because the
/// About/Home/Logs view models are call sites too and remex.desktop cannot reference remex.agent;
/// the Windows implementation is registered in the host container by <c>HostBootstrapper</c>.
/// </para>
/// <para>
/// Implementations must not throw for ordinary failures: report them through
/// <see cref="UnelevatedLaunchResult"/> and let the caller fall back.
/// </para>
/// </remarks>
public interface IUnelevatedLauncher
{
    /// <summary>
    /// Tries to open <paramref name="target"/> at the interactive user's normal integrity level.
    /// </summary>
    /// <param name="target">An absolute file or folder path, or an absolute URL. Already validated by
    /// the caller; this method performs no allowlist checks of its own.</param>
    /// <param name="arguments">Command-line arguments for an executable target, or <c>null</c>.</param>
    /// <param name="workingDirectory">The working directory for an executable target, or <c>null</c>
    /// to let the implementation choose (the target's own folder).</param>
    /// <returns>What happened. Anything other than <see cref="UnelevatedLaunchResult.Launched"/> means
    /// nothing was started and the caller should use its standard launch.</returns>
    UnelevatedLaunchResult TryLaunch(string target, string? arguments, string? workingDirectory);
}

/// <summary>Outcome of <see cref="IUnelevatedLauncher.TryLaunch"/>.</summary>
public enum UnelevatedLaunchResult
{
    /// <summary>The target was started (or handed to the desktop shell) at normal permissions.</summary>
    Launched,

    /// <summary>The target itself asks for administrator rights (its manifest, a compatibility
    /// setting, or a shortcut marked "Run as administrator"). The standard elevated launch is what
    /// made it work before, so the caller falls back to it.</summary>
    ElevationRequired,

    /// <summary>No desktop shell (Explorer) is running in this session at all (<c>GetShellWindow</c>
    /// returned nothing), so there is no normal-level process to borrow permissions from. The caller
    /// falls back. A shell that exists but is busy or restarting is <see cref="Failed"/>, not this.</summary>
    NoShell,

    /// <summary>Nothing to drop: this process is not elevated, or this platform has no split
    /// administrator token. The standard launch already runs at the user's level.</summary>
    NotNeeded,

    /// <summary>A PROGRAM could not be started with the shell's token (command line too long for
    /// <c>CreateProcessWithTokenW</c>, Secondary Logon unavailable, or the shell's token is elevated or
    /// in another session). The caller falls back to the elevated launch: for a program the owner put
    /// on the launcher list that is no worse than before RemEx-pp4cm.2, and it keeps working.</summary>
    ProgramRouteUnavailable,

    /// <summary>A link, document or folder could not be opened at normal permissions even after a
    /// retry and the <c>explorer.exe</c> last resort. The caller must NOT fall back to the elevated
    /// launch (that would open an administrator browser, the very bug this fixes); it reports the
    /// failure instead.</summary>
    Failed,
}
