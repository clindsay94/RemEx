using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using System.Security.Principal;
using Remex.Core.Guards;

namespace Remex.Agent.Services.Readiness;

/// <summary>
/// The real probes behind <see cref="SystemReadinessService"/> (RemEx-gpe3).
/// </summary>
/// <remarks>
/// <para>
/// STRICTLY READ-ONLY, and deliberately so. Nothing here opens the certificate, writes a registry
/// value, or touches an ACL — the certificate probe checks that the file EXISTS AND CAN BE OPENED
/// FOR READING and nothing more, because CLAUDE.md makes cert and ACL handling High-Risk and a
/// readiness check is the last place that should be discovering it has side effects.
/// </para>
/// <para>
/// Every method answers <see langword="null"/> for "could not determine", which
/// <see cref="SystemReadinessService"/> turns into <see cref="ReadinessState.Unknown"/> rather than
/// into a failure.
/// </para>
/// </remarks>
public sealed class SystemReadinessProbe : IReadinessProbe
{
    private readonly Func<bool?> _isAutostartRegistered;
    private readonly string _certificatePath;

    /// <param name="isAutostartRegistered">
    /// Injected rather than constructed so the readiness layer does not acquire a dependency on the
    /// service that WRITES autostart — this side only ever reads.
    /// <para>
    /// TYPED <c>bool?</c> AND NOT <c>bool</c>, DELIBERATELY. The obvious implementation is
    /// <c>IStartupRegistrationService.IsEnabled</c>, and it swallows its own failures: the Windows
    /// branch shells out to <c>schtasks /Query</c> under <c>catch { return false; }</c>, and the
    /// Linux branch does the same around the autostart file. Passing that in directly makes this the
    /// one probe that STRUCTURALLY cannot answer "unknown", so an EDR that blocks
    /// <c>schtasks.exe</c>, an unavailable Task Scheduler RPC endpoint, or an unreadable
    /// <c>~/.config/autostart</c> would each tell the user "RemEx will not start again after a
    /// reboot" — a claim about their machine that was never established. Wrap it so a failed check
    /// answers null.
    /// </para>
    /// </param>
    /// <param name="certificatePath">Absolute path to the machine-wide <c>cert.pfx</c>.</param>
    public SystemReadinessProbe(Func<bool?> isAutostartRegistered, string certificatePath)
    {
        _isAutostartRegistered = Guard.NotNull(isAutostartRegistered);
        _certificatePath = Guard.NotNullOrWhiteSpace(certificatePath);
    }

    /// <summary>
    /// Whether the process holds an elevated token — Windows only.
    /// </summary>
    /// <remarks>
    /// NOT APPLICABLE ON LINUX, where this returns <see langword="null"/> and the service maps that
    /// to <see cref="ReadinessState.NotApplicable"/> rather than to
    /// <see cref="ReadinessState.Unknown"/> — the elevation row is the one place null means "does
    /// not apply here" instead of "could not determine", which is why the mapping is stated at the
    /// call site rather than left to the default.
    /// On Windows the agent must run high-integrity or it cannot read the machine-wide
    /// <c>cert.pfx</c>, which refuses every SPKI-pinned pairing. On Linux it is an ordinary user
    /// process by design and there is nothing to elevate; asking would produce a row that is amber
    /// forever and teaches the user to ignore the card.
    /// </remarks>
    public bool? IsElevated()
    {
        if (!OperatingSystem.IsWindows()) return null;
        return IsWindowsElevated();
    }

    [SupportedOSPlatform("windows")]
    private static bool IsWindowsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Whether <c>cert.pfx</c> exists and can be opened for reading.
    /// </summary>
    /// <remarks>
    /// THIS IS THE BRICK CANARY, READ-ONLY. A present-but-unreadable cert.pfx is the exact state
    /// <c>CertificateService</c> logs Critical for and refuses to regenerate around, because
    /// regenerating it would invalidate every pinned client at once. Surfacing it here is reporting,
    /// never repair — the row exists so a human can be told, not so anything can act.
    ///
    /// Opening the file is the check rather than merely testing existence: on Windows a
    /// non-elevated start sees Administrators as deny-only and File.Exists still succeeds, so
    /// existence alone would report green on precisely the machine that is broken.
    /// </remarks>
    public bool? IsCertificateReadable()
    {
        if (!File.Exists(_certificatePath)) return false;

        try
        {
            using var stream = File.Open(_certificatePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            // Locked by something else. Not the same as unreadable-by-permission, and not something
            // this layer can distinguish reliably, so it is honestly unknown rather than broken.
            return null;
        }
    }

    /// <summary>
    /// Whether anything in this machine is listening on <paramref name="port"/>.
    /// </summary>
    /// <remarks>
    /// Reads the OS socket table rather than dialling the port. Connecting to it would be a live
    /// request against the agent's own listener from inside the agent, which is both wasteful and
    /// capable of perturbing what it measures; the table is the fact itself.
    ///
    /// LOOPBACK-ONLY ENDPOINTS DO NOT COUNT, and that is not a nicety. CLAUDE.md fixes the
    /// connection as always non-loopback Android-to-PC, so a listener on <c>127.0.0.1</c> can never
    /// serve the phone — counting it would report green on a machine the phone provably cannot
    /// reach, which is the exact support case this card exists to eliminate.
    /// </remarks>
    /// <remarks>
    /// WHAT THIS STILL CANNOT TELL YOU, stated so the localized text does not overclaim. It matches
    /// the machine's socket table by port, so it cannot prove the listener is THIS PROCESS. That
    /// matters here specifically because the agent drifts: <c>HostBootstrapper</c> walks
    /// <c>port + attempt</c> when the canonical port is held by a non-RemEx occupant, which
    /// <c>StalePortReclaimer</c> deliberately leaves alone. A foreign process on the canonical port
    /// therefore satisfies this probe while the agent is actually on the next one and the phone —
    /// which dials the port it saved at pairing — reaches the wrong listener. Asking the running
    /// host which port it actually bound is the check that would answer the user's real question,
    /// and it is filed rather than faked here (RemEx-ksbm carries the firewall row; the bound-port
    /// question is its own follow-up). Until then a green row means "something non-loopback is
    /// listening on the port the phone dials", which is weaker than "you are reachable" — the
    /// firewall is still between them — and the card's wording must say only that.
    /// </remarks>
    public bool? IsPortListening(int port)
    {
        var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
        return listeners.Any(endpoint =>
            endpoint.Port == port && !IPAddress.IsLoopback(endpoint.Address));
    }

    /// <summary>
    /// Whether the logon task (Windows) or XDG autostart entry (Linux) is actually registered.
    /// </summary>
    /// <remarks>
    /// The READ-BACK the Settings toggle never had: it binds write-only, so a task deleted
    /// externally — by an uninstall, a cleanup tool, or an updater — leaves the switch showing "on"
    /// while nothing will start at logon. RemEx-q0j7 is a real instance of that drift, and this is
    /// the check that would have caught it.
    /// </remarks>
    public bool? IsAutostartRegistered() => _isAutostartRegistered();
}
