using System;
using FluentAssertions;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Pins the PC UI's claim on the host sensor sampler (perf audit P0-10): one lease while the main
/// window is visible, the tray flyout shows sensor cards, or a sensor alert is armed - and none
/// while all three are false.
/// </summary>
/// <remarks>
/// The armed-alert case is the one that matters most and is easiest to lose: alerts exist to catch
/// what nobody is watching, so they must keep the sampler running with every window hidden. Losing
/// that is silent - the alert simply never fires.
/// </remarks>
public class TelemetryDemandCoordinatorTests
{
    /// <summary>Counts live leases the way the host's SamplingDemand does.</summary>
    private sealed class LeaseCounter
    {
        public int Live { get; private set; }
        public int Taken { get; private set; }

        public IDisposable? Acquire()
        {
            Live++;
            Taken++;
            return new Lease(this);
        }

        private sealed class Lease(LeaseCounter owner) : IDisposable
        {
            private bool _released;

            public void Dispose()
            {
                if (_released) return;
                _released = true;
                owner.Live--;
            }
        }
    }

    [Fact]
    public void NothingShowingAndNoAlertsHoldsNoDemand()
    {
        var host = new LeaseCounter();
        using var demand = new TelemetryDemandCoordinator(host.Acquire);

        demand.IsWindowVisible = false;
        demand.IsFlyoutShowingSensors = false;
        demand.HasArmedAlerts = false;

        host.Live.Should().Be(0);
        demand.IsHoldingDemand.Should().BeFalse();
    }

    [Fact]
    public void AnArmedAlertAloneKeepsSamplingWithEverythingHidden()
    {
        var host = new LeaseCounter();
        using var demand = new TelemetryDemandCoordinator(host.Acquire);

        demand.IsWindowVisible = true;
        demand.IsWindowVisible = false; // hidden to the tray
        demand.IsFlyoutShowingSensors = false;
        host.Live.Should().Be(0);

        demand.HasArmedAlerts = true;

        host.Live.Should().Be(1, "an armed alert needs samples while hidden");
        demand.IsHoldingDemand.Should().BeTrue();

        demand.HasArmedAlerts = false;
        host.Live.Should().Be(0, "removing the last alert with nothing on screen must let the sampler idle");
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void AnyOneSignalIsEnough(bool window, bool flyout, bool alerts)
    {
        var host = new LeaseCounter();
        using var demand = new TelemetryDemandCoordinator(host.Acquire);

        demand.IsWindowVisible = window;
        demand.IsFlyoutShowingSensors = flyout;
        demand.HasArmedAlerts = alerts;

        host.Live.Should().Be(1);
    }

    [Fact]
    public void OverlappingSignalsShareOneLeaseAndOnlyTheLastOneOffReleasesIt()
    {
        // ONE LEASE, NOT ONE PER SIGNAL: the window hiding while the flyout is open must not take
        // the flyout's samples away, and toggling signals must not churn leases.
        var host = new LeaseCounter();
        using var demand = new TelemetryDemandCoordinator(host.Acquire);

        demand.IsWindowVisible = true;
        demand.IsFlyoutShowingSensors = true;
        demand.HasArmedAlerts = true;
        host.Live.Should().Be(1);
        host.Taken.Should().Be(1);

        demand.IsWindowVisible = false;
        demand.HasArmedAlerts = false;
        host.Live.Should().Be(1, "the open flyout still needs samples");

        demand.IsFlyoutShowingSensors = false;
        host.Live.Should().Be(0);

        // Sampling resumes when a consumer reappears.
        demand.IsWindowVisible = true;
        host.Live.Should().Be(1);
        host.Taken.Should().Be(2);
    }

    [Fact]
    public void DisposingReleasesTheLeaseAndLateSignalsDoNotRetakeIt()
    {
        var host = new LeaseCounter();
        var demand = new TelemetryDemandCoordinator(host.Acquire);
        demand.IsWindowVisible = true;

        demand.Dispose();
        host.Live.Should().Be(0);

        demand.HasArmedAlerts = true;
        host.Live.Should().Be(0, "a disposed coordinator must not leak a lease from a late event");
    }
}
