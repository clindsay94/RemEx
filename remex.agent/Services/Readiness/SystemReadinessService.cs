using Remex.Core.Guards;

namespace Remex.Agent.Services.Readiness;

/// <summary>
/// The raw facts the readiness rows are decided from.
/// </summary>
/// <remarks>
/// Separated from <see cref="SystemReadinessService"/> so the JUDGEMENTS — which state each fact
/// maps to, and how the rows combine — are testable without a listening socket, an elevated token
/// or a machine-wide certificate. Those judgements are the part a mistake actually misleads someone
/// with; the probes themselves are thin.
///
/// Every member returns a nullable, and <see langword="null"/> means "could not determine" rather
/// than "no". That distinction is the entire feature: see <see cref="ReadinessState.Unknown"/>.
/// </remarks>
public interface IReadinessProbe
{
    /// <summary>
    /// Whether the process holds an elevated/high-integrity token.
    /// </summary>
    /// <remarks>
    /// THE ONE MEMBER WHERE NULL IS AMBIGUOUS. Everywhere else null means "could not determine";
    /// here it may also mean "this operating system has nothing to elevate". The service decides
    /// between them by asking the OS directly rather than by trusting the null, so an implementer
    /// who follows the general contract and returns null on a failed Windows check gets Unknown -
    /// not a silently deleted row.
    /// </remarks>
    bool? IsElevated();

    /// <summary>Whether the machine-wide certificate exists and could be read, or null if unknowable.</summary>
    bool? IsCertificateReadable();

    /// <summary>Whether anything is listening on <paramref name="port"/>, or null if unknowable.</summary>
    bool? IsPortListening(int port);

    /// <summary>Whether the logon task / autostart entry is registered, or null if unknowable.</summary>
    bool? IsAutostartRegistered();
}

/// <summary>
/// Answers "is this machine actually ready for the phone to reach it?" (RemEx-gpe3).
/// </summary>
/// <remarks>
/// <para>
/// STRICTLY READ-ONLY. Nothing here repairs anything, and in particular nothing here touches the
/// certificate or its ACLs — those are High-Risk per CLAUDE.md, and an automatic repair of a cert
/// problem is how every paired phone gets bricked at once. The certificate row REPORTS. Fixes are
/// user-invoked, elsewhere, one at a time.
/// </para>
/// <para>
/// The reason this exists at all: first run shows a splash and a tutorial but never verifies the
/// machine is ready, so "my phone can't find my PC" support cases start from zero information.
/// </para>
/// </remarks>
public sealed class SystemReadinessService
{
    private readonly IReadinessProbe _probe;
    private readonly int _port;

    public SystemReadinessService(IReadinessProbe probe, int port)
    {
        _probe = Guard.NotNull(probe);
        _port = port;
    }

    /// <summary>
    /// Runs every check and returns the report.
    /// </summary>
    /// <remarks>
    /// A probe that THROWS is treated as <see cref="ReadinessState.Unknown"/>, not as a failure.
    /// The difference matters: a readiness card that reports "your certificate is broken" because
    /// its own check crashed would send a non-technical user to repair something that was never
    /// wrong — and the one repair available for a certificate is the one that bricks every paired
    /// phone. "Could not check" is the honest answer and the safe one.
    /// </remarks>
    public SystemReadinessReport Run()
    {
        return new SystemReadinessReport(new List<ReadinessCheck>
        {
            // ELEVATION IS FIRST BECAUSE IT IS THE ONE THAT SILENTLY POISONS THE OTHERS. A
            // medium-integrity start cannot read the machine-wide cert.pfx, so it would ALSO report
            // a certificate problem - and a user told to fix their certificate would reach for the
            // one action that bricks every paired phone. Reporting elevation first, and loudly,
            // points at the actual cause. Not elevated is a Problem, never a Warning: pairings do
            // not degrade, they stop.
            Evaluate(ReadinessCheckId.Elevation, _probe.IsElevated,
                whenTrue: ReadinessState.Ok,
                whenFalse: ReadinessState.Problem,
                trueDetail: "running elevated",
                falseDetail: "NOT elevated - cannot read the machine-wide cert.pfx, which bricks every SPKI-pinned pairing",
                // Elevation is a Windows concept. On Linux the agent is an ordinary user process by
                // design, so there is nothing to elevate and the row is omitted rather than sitting
                // amber forever - an always-amber row is how a card teaches people to ignore it.
                //
                // GATED ON THE OS RATHER THAN ASSUMED FROM THE NULL. The interface contract says
                // null means "could not determine", and this row is the single place it can also
                // mean "does not apply here" - so the two readings must be told apart by something
                // that actually knows which is which. Mapping every null to NotApplicable would
                // DELETE the elevation row whenever a probe could not answer on Windows, and a
                // deleted row does not stop the card going green. Elevation is the load-bearing
                // security state; a Windows machine whose elevation was never established must
                // report Unknown, not vanish.
                whenNull: OperatingSystem.IsWindows()
                    ? ReadinessState.Unknown
                    : ReadinessState.NotApplicable,
                nullDetail: OperatingSystem.IsWindows()
                    ? "elevation could not be determined on a Windows host - treat as unverified"
                    : "not applicable: the Linux agent runs as an ordinary user process"),

            Evaluate(ReadinessCheckId.Certificate, _probe.IsCertificateReadable,
                whenTrue: ReadinessState.Ok,
                whenFalse: ReadinessState.Problem,
                trueDetail: "certificate present and readable",
                falseDetail: "certificate missing or unreadable - report only, never regenerate; see the CertificateService brick canary"),

            Evaluate(ReadinessCheckId.PortListening, () => _probe.IsPortListening(_port),
                whenTrue: ReadinessState.Ok,
                whenFalse: ReadinessState.Problem,
                trueDetail: $"listening on {_port}",
                falseDetail: $"nothing is listening on {_port} - the phone has nothing to connect to"),

            // AUTOSTART IS A WARNING, NOT A PROBLEM, and the distinction is the honest one: the
            // machine works right now. It just will not after the next reboot, and the user will
            // experience that as the app having broken by itself. Calling it a Problem while the
            // connection is live would teach them to ignore red rows.
            Evaluate(ReadinessCheckId.Autostart, _probe.IsAutostartRegistered,
                whenTrue: ReadinessState.Ok,
                whenFalse: ReadinessState.Warning,
                trueDetail: "autostart registered",
                falseDetail: "autostart not registered - RemEx works now but will not start again after a reboot")
        });
    }

    private static ReadinessCheck Evaluate(
        ReadinessCheckId id,
        Func<bool?> probe,
        ReadinessState whenTrue,
        ReadinessState whenFalse,
        string trueDetail,
        string falseDetail,
        ReadinessState whenNull = ReadinessState.Unknown,
        string nullDetail = "could not be checked on this system")
    {
        bool? result;
        try
        {
            result = probe();
        }
        catch (Exception ex)
        {
            // Deliberately broad. Every probe reaches something that can fail in a way this layer
            // cannot enumerate - the registry, the Task Scheduler COM API, the filesystem, a socket
            // table - and the correct answer to all of them is the same: we did not check.
            return new ReadinessCheck(id, ReadinessState.Unknown,
                $"check failed: {ex.GetType().Name}: {ex.Message}");
        }

        return result switch
        {
            true => new ReadinessCheck(id, whenTrue, trueDetail),
            false => new ReadinessCheck(id, whenFalse, falseDetail),
            null => new ReadinessCheck(id, whenNull, nullDetail)
        };
    }
}
