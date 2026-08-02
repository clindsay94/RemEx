using Remex.Core.Services.Readiness;
using Remex.Agent.Services.Readiness;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Tests for the system-readiness judgements (RemEx-gpe3).
/// </summary>
/// <remarks>
/// The probes are thin; the judgements are what a mistake actually misleads someone with. The
/// target user may not be technical, so the failure that matters is not "the card is wrong" but
/// "the card is confidently wrong" — telling somebody their machine is ready when nothing
/// established that, and thereby removing the first thing they would have looked at.
/// </remarks>
public class SystemReadinessTests
{
    /// <summary>A probe whose every answer is dictated by the test.</summary>
    private sealed class FakeProbe : IReadinessProbe
    {
        public bool? Elevated { get; set; } = true;
        public bool? CertificateReadable { get; set; } = true;
        public bool? PortListening { get; set; } = true;
        public bool? AutostartRegistered { get; set; } = true;
        public bool? FirewallAllows { get; set; } = true;

        public Func<bool?>? ElevatedThrows { get; set; }

        /// <summary>The port the service actually asked about, so a hardcoded probe cannot hide.</summary>
        public int? PortAsked { get; private set; }

        /// <summary>The port the FIREWALL check was asked about, which must be the same one.</summary>
        public int? FirewallPortAsked { get; private set; }

        public bool? IsElevated() => ElevatedThrows is not null ? ElevatedThrows() : Elevated;
        public bool? IsCertificateReadable() => CertificateReadable;

        public bool? IsPortListening(int port)
        {
            PortAsked = port;
            return PortListening;
        }

        public bool? IsAutostartRegistered() => AutostartRegistered;

        public bool? IsInboundAllowedByFirewall(int port)
        {
            FirewallPortAsked = port;
            return FirewallAllows;
        }
    }

    private static SystemReadinessReport Run(FakeProbe probe) =>
        new SystemReadinessService(probe, 5005).Run();

    private static ReadinessState StateOf(SystemReadinessReport report, ReadinessCheckId id) =>
        report.Checks.Single(c => c.Id == id).State;

    [Fact]
    public void AHealthyMachineIsFullyReadyAndCollapses()
    {
        var report = Run(new FakeProbe());

        Assert.True(report.IsFullyReady);
        Assert.Equal(ReadinessState.Ok, report.Overall);
        Assert.All(report.Checks, c => Assert.Equal(ReadinessState.Ok, c.State));
    }

    [Fact]
    public void AnUncheckedRowNeverCollapsesToGreen()
    {
        // THE TEST THIS CLASS EXISTS FOR. "I could not check" is not "you are fine". A card that
        // renders unknown as green makes the support case it exists to prevent actively worse: the
        // user has now been assured everything is ready, so they stop looking here.
        var report = Run(new FakeProbe { CertificateReadable = null });

        Assert.False(report.IsFullyReady);
        Assert.Equal(ReadinessState.Unknown, report.Overall);
        Assert.Equal(ReadinessState.Unknown, StateOf(report, ReadinessCheckId.Certificate));
    }

    [Fact]
    public void UnknownOutranksOkAndWarningButNotProblem()
    {
        // Severity is deliberately NOT the enum's declaration order. Unknown is declared last so Ok
        // can be the type's default value, so the obvious `Checks.Max(c => c.State)` would rank
        // Unknown above Problem AND above everything else - silently. This pins the intended order.
        Assert.True(SystemReadinessReport.Severity(ReadinessState.Unknown)
            > SystemReadinessReport.Severity(ReadinessState.Ok));
        Assert.True(SystemReadinessReport.Severity(ReadinessState.Unknown)
            > SystemReadinessReport.Severity(ReadinessState.Warning));
        Assert.True(SystemReadinessReport.Severity(ReadinessState.Problem)
            > SystemReadinessReport.Severity(ReadinessState.Unknown));
    }

    [Fact]
    public void AKnownBreakageOutranksAnUncheckedRow()
    {
        // Both present: the user should be pointed at the thing that is definitely broken.
        var report = Run(new FakeProbe { PortListening = false, AutostartRegistered = null });

        Assert.Equal(ReadinessState.Problem, report.Overall);
    }

    [Fact]
    public void NotBeingElevatedIsAProblemRatherThanAWarning()
    {
        // Pairings do not degrade when the token is medium-integrity, they stop: the process cannot
        // read the machine-wide cert.pfx, and every SPKI-pinned client is refused. CLAUDE.md calls
        // this out as load-bearing, so it can never be softened to an advisory.
        var report = Run(new FakeProbe { Elevated = false });

        Assert.Equal(ReadinessState.Problem, StateOf(report, ReadinessCheckId.Elevation));
        Assert.False(report.IsFullyReady);
    }

    [Fact]
    public void ElevationIsReportedBeforeTheCertificateItWouldAlsoBreak()
    {
        // A medium-integrity start ALSO fails to read cert.pfx, so both rows go red from one cause.
        // Order matters because the remedy differs enormously: re-elevating is harmless, whereas a
        // user who reads the certificate row first may reach for the one action that bricks every
        // paired phone at once. The row that names the actual cause has to come first.
        var report = Run(new FakeProbe { Elevated = false, CertificateReadable = false });

        var ids = report.Checks.Select(c => c.Id).ToList();
        Assert.True(ids.IndexOf(ReadinessCheckId.Elevation) < ids.IndexOf(ReadinessCheckId.Certificate));
    }

    [Fact]
    public void MissingAutostartIsAWarningBecauseTheMachineWorksRightNow()
    {
        // Honest severity. It works today; it will not after a reboot, which the user will
        // experience as the app having broken by itself. Calling that red while the phone is
        // connected teaches people to ignore red rows.
        var report = Run(new FakeProbe { AutostartRegistered = false });

        Assert.Equal(ReadinessState.Warning, StateOf(report, ReadinessCheckId.Autostart));
        Assert.Equal(ReadinessState.Warning, report.Overall);
        Assert.False(report.IsFullyReady, "a warning is not readiness");
    }

    [Fact]
    public void ANothingListeningPortIsAProblem()
    {
        var report = Run(new FakeProbe { PortListening = false });

        Assert.Equal(ReadinessState.Problem, StateOf(report, ReadinessCheckId.PortListening));
    }

    [Fact]
    public void AProbeThatThrowsIsUncheckedRatherThanFailed()
    {
        // The distinction is safety-critical for exactly one row. A certificate check that crashed
        // must not render as "your certificate is broken", because the repair for that is the one
        // that bricks every paired phone. "Could not check" is both the honest answer and the safe
        // one, and the same rule applies to every probe since none of them can enumerate what the
        // registry, the Task Scheduler COM API or a socket table might throw.
        var report = Run(new FakeProbe
        {
            ElevatedThrows = () => throw new UnauthorizedAccessException("access is denied")
        });

        var elevation = report.Checks.Single(c => c.Id == ReadinessCheckId.Elevation);
        Assert.Equal(ReadinessState.Unknown, elevation.State);
        Assert.Contains("UnauthorizedAccessException", elevation.Detail);
    }

    [Fact]
    public void AThrowingProbeDoesNotAbandonTheRemainingChecks()
    {
        // One broken probe must not cost the user every other answer - the rows they can still be
        // told about are exactly the ones that might explain their problem.
        var report = Run(new FakeProbe
        {
            ElevatedThrows = () => throw new InvalidOperationException("boom"),
            PortListening = false
        });

        // Counted against the enum rather than a literal. The point of this test is "no row was
        // abandoned", and a hardcoded number expresses that only until someone adds a row - at which
        // point it fails for the wrong reason and gets bumped without thought.
        Assert.Equal(Enum.GetValues<ReadinessCheckId>().Length, report.Checks.Count);
        Assert.Equal(ReadinessState.Problem, StateOf(report, ReadinessCheckId.PortListening));
    }

    [Fact]
    public void EveryCheckAppearsExactlyOnce()
    {
        // The card renders one row per id; a duplicate would show the same row twice and a missing
        // one would silently drop a check the user believes was performed.
        var report = Run(new FakeProbe());

        Assert.Equal(
            Enum.GetValues<ReadinessCheckId>().OrderBy(v => v).ToList(),
            report.Checks.Select(c => c.Id).OrderBy(v => v).ToList());
    }

    [Fact]
    public void TheReportedPortIsTheOneThatWasActuallyProbed()
    {
        // BOTH HALVES, because asserting only the detail string is the weaker test review caught:
        // hardcoding the probe to 5005 while still interpolating _port into the text would have
        // passed it. That is not a hypothetical mismatch here - the agent genuinely drifts ports
        // when the canonical one is held by a foreign process, so probing one port and reporting
        // another is exactly the bug this test is named for.
        var probe = new FakeProbe { PortListening = false };
        var report = new SystemReadinessService(probe, 8338).Run();

        Assert.Equal(8338, probe.PortAsked);
        Assert.Contains("8338", report.Checks.Single(c => c.Id == ReadinessCheckId.PortListening).Detail);
    }

    [Fact]
    public void AnUnreadableCertificateIsAProblemAndBlocksReadiness()
    {
        // THE ROW THAT HAD NO GUARD. Every other severity was pinned; this one was set by two tests
        // that asserted only ordering and the Unknown case, so downgrading whenFalse to Ok passed
        // all sixteen - and a present-but-unreadable cert.pfx, the exact state CertificateService's
        // brick canary logs Critical for, would have rendered as a clean bill of health. It is the
        // security-relevant row, so it is the last one that should have been undefended.
        var report = Run(new FakeProbe { CertificateReadable = false });

        Assert.Equal(ReadinessState.Problem, StateOf(report, ReadinessCheckId.Certificate));
        Assert.False(report.IsFullyReady);
        Assert.Equal(ReadinessState.Problem, report.Overall);
    }

    [Fact]
    public void AnUnrecognisedStateRanksAsUnknownRatherThanGreen()
    {
        // The `_ =>` arm, which the doc calls load-bearing and nothing exercised. A future state
        // added to the enum must not fall through to a rank that lets the card collapse; ranking it
        // with Unknown is the safe default and this pins it.
        Assert.Equal(
            SystemReadinessReport.Severity(ReadinessState.Unknown),
            SystemReadinessReport.Severity((ReadinessState)99));
    }

    [Fact]
    public void AnElevationRowThatDoesNotApplyIsOmittedRatherThanLeftAmber()
    {
        // A NULL ELEVATION MEANS DIFFERENT THINGS ON DIFFERENT OPERATING SYSTEMS, which is why the
        // service decides by asking the OS rather than by trusting the null - and why this test has
        // to branch the same way.
        //
        // On Linux the agent is an ordinary user process by design, so there is nothing to elevate:
        // reporting "could not check" would leave every Linux machine permanently amber and train
        // the user to ignore the card, a worse outcome than not asking. The row is omitted.
        //
        // On Windows a null means the check genuinely failed, and elevation is the load-bearing
        // security state - so it must report Unknown and hold the card open. Review caught that
        // mapping every null to NotApplicable would DELETE the row instead, and a deleted row does
        // not stop the card going green.
        var report = Run(new FakeProbe { Elevated = null });

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(ReadinessState.Unknown, StateOf(report, ReadinessCheckId.Elevation));
            Assert.Contains(report.Applicable, c => c.Id == ReadinessCheckId.Elevation);
            Assert.False(report.IsFullyReady);
            Assert.Equal(ReadinessState.Unknown, report.Overall);
        }
        else
        {
            Assert.Equal(ReadinessState.NotApplicable, StateOf(report, ReadinessCheckId.Elevation));
            Assert.DoesNotContain(report.Applicable, c => c.Id == ReadinessCheckId.Elevation);

            // And crucially it still collapses to green, unlike Unknown.
            Assert.True(report.IsFullyReady);
            Assert.Equal(ReadinessState.Ok, report.Overall);
        }
    }

    [Fact]
    public void ARowThatDoesNotApplyDoesNotMaskOneThatDoes()
    {
        // NotApplicable ranks below Ok, so it must never become the report's Overall and hide a
        // real failure sitting next to it.
        var report = Run(new FakeProbe { Elevated = null, PortListening = false });

        Assert.Equal(ReadinessState.Problem, report.Overall);
        Assert.False(report.IsFullyReady);
    }

    [Fact]
    public void AReportOfNothingButInapplicableRowsIsUnknownRatherThanReady()
    {
        // Otherwise an OS where nothing could be checked would render as a clean bill of health.
        var report = new SystemReadinessReport([
            new ReadinessCheck(ReadinessCheckId.Elevation, ReadinessState.NotApplicable, "n/a")
        ]);

        Assert.False(report.IsFullyReady);
        Assert.Equal(ReadinessState.Unknown, report.Overall);
    }

    [Fact]
    public void AnEmptyReportIsUnknownRatherThanReady()
    {
        // Vacuous truth is the wrong default here: `All` over an empty list is true, so a report
        // that ran no checks at all would otherwise claim the machine is fully ready.
        var report = new SystemReadinessReport([]);

        Assert.False(report.IsFullyReady);
        Assert.Equal(ReadinessState.Unknown, report.Overall);
    }

    [Fact]
    public void AListeningPortBehindABlockedFirewallIsNotReady()
    {
        // THE GAP THIS ROW CLOSES (RemEx-ksbm). Before it existed, every other check could pass on a
        // machine the phone provably cannot reach: the server IS up, the certificate IS readable,
        // the task IS registered - and the firewall is refusing the connection. The card would have
        // said "ready" and removed the first thing the user would otherwise have looked at.
        var report = Run(new FakeProbe { PortListening = true, FirewallAllows = false });

        Assert.Equal(ReadinessState.Problem, StateOf(report, ReadinessCheckId.Firewall));
        Assert.Equal(ReadinessState.Problem, report.Overall);
        Assert.False(report.IsFullyReady);
    }

    [Fact]
    public void AFirewallThatCouldNotBeCheckedStopsTheCardCollapsingToGreen()
    {
        // The COMMON case on Linux, not an edge case: an unprivileged agent cannot read ufw's rules,
        // and a machine filtering with bare nftables has nothing to ask. Unknown must keep the card
        // open, or the row would be worse than absent - it would look like a check that passed.
        var report = Run(new FakeProbe { FirewallAllows = null });

        Assert.Equal(ReadinessState.Unknown, StateOf(report, ReadinessCheckId.Firewall));
        Assert.Equal(ReadinessState.Unknown, report.Overall);
        Assert.False(report.IsFullyReady);
    }

    [Fact]
    public void TheFirewallIsAskedAboutTheSamePortThatWasProbedForAListener()
    {
        // The two rows are read together as "the server is up AND it is reachable", which is only
        // true if they are about the same port. The agent genuinely drifts ports when the canonical
        // one is held by a foreign process, so this is a live mismatch rather than a hypothetical.
        var probe = new FakeProbe();

        new SystemReadinessService(probe, 8338).Run();

        Assert.Equal(8338, probe.FirewallPortAsked);
        Assert.Equal(probe.PortAsked, probe.FirewallPortAsked);
    }

    [Fact]
    public void AFirewallProbeThatThrowsIsUnknownAndDoesNotAbandonTheOtherRows()
    {
        // Launching a process is the one probe that reaches something genuinely unpredictable - an
        // EDR, a policy block, a missing PATH entry. A throw there must not cost the user the checks
        // that would have told them what is actually wrong.
        var probe = new ThrowingFirewallProbe();
        var report = new SystemReadinessService(probe, 5005).Run();

        Assert.Equal(ReadinessState.Unknown, StateOf(report, ReadinessCheckId.Firewall));
        Assert.Equal(ReadinessState.Ok, StateOf(report, ReadinessCheckId.Certificate));
        Assert.Equal(ReadinessState.Ok, StateOf(report, ReadinessCheckId.Autostart));
    }

    private sealed class ThrowingFirewallProbe : IReadinessProbe
    {
        public bool? IsElevated() => true;
        public bool? IsCertificateReadable() => true;
        public bool? IsPortListening(int port) => true;
        public bool? IsAutostartRegistered() => true;

        public bool? IsInboundAllowedByFirewall(int port) =>
            throw new System.ComponentModel.Win32Exception("the query was refused");
    }
}
