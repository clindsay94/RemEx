using System;
using System.Collections.Generic;
using FluentAssertions;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// Pins <see cref="ShellConnectionState"/> — the refinement that lets the collapsed nav-drawer
/// status control say more than <see cref="PhonePresenceMonitor.IsPhoneAttached"/>'s one bit
/// (RemEx-44gc6). Follows the same <c>App.EmbeddedHostServices</c> swap-and-restore pattern as
/// <see cref="HostDownIsNotAnAbsentPhoneTests"/>, which this bead's host-down branch already covers
/// for <c>IsPhoneAttached</c>/<c>PresenceText</c> — these tests are the <c>State</c>-flavoured twin.
/// </summary>
public class ShellConnectionStateTests : IDisposable
{
    private readonly IServiceProvider? _saved = App.EmbeddedHostServices;

    public void Dispose()
    {
        App.EmbeddedHostServices = _saved;
        PhonePresenceMonitor.Instance.Refresh();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void NoRegisteredSourceIsHostDown()
    {
        App.EmbeddedHostServices = new Provider(null);

        PhonePresenceMonitor.Instance.Refresh();

        PhonePresenceMonitor.Instance.State.Should().Be(ShellConnectionState.HostDown);
        PhonePresenceMonitor.Instance.IsHostDown.Should().BeTrue();
        PhonePresenceMonitor.Instance.HasNoPhone.Should().BeFalse();
        PhonePresenceMonitor.Instance.HasPhone.Should().BeFalse();
        PhonePresenceMonitor.Instance.DeviceName.Should().BeNull();
        PhonePresenceMonitor.Instance.RemoteAddress.Should().BeNull();
    }

    [Fact]
    public void AHealthyHostWithNoSessionsIsNoPhone()
    {
        App.EmbeddedHostServices = new Provider(new Source([]));

        PhonePresenceMonitor.Instance.Refresh();

        PhonePresenceMonitor.Instance.State.Should().Be(ShellConnectionState.NoPhone);
        PhonePresenceMonitor.Instance.IsHostDown.Should().BeFalse();
        PhonePresenceMonitor.Instance.HasNoPhone.Should().BeTrue();
        PhonePresenceMonitor.Instance.HasPhone.Should().BeFalse();
    }

    [Fact]
    public void OnePhoneIsPhoneAttachedWithNameAndAddress()
    {
        App.EmbeddedHostServices = new Provider(new Source([Phone("192.168.1.42", "Pixel 9")]));

        PhonePresenceMonitor.Instance.Refresh();

        PhonePresenceMonitor.Instance.State.Should().Be(ShellConnectionState.PhoneAttached);
        PhonePresenceMonitor.Instance.HasPhone.Should().BeTrue();
        PhonePresenceMonitor.Instance.IsHostDown.Should().BeFalse();
        PhonePresenceMonitor.Instance.HasNoPhone.Should().BeFalse();
        PhonePresenceMonitor.Instance.DeviceName.Should().Be("Pixel 9");
        PhonePresenceMonitor.Instance.RemoteAddress.Should().Be("192.168.1.42");
    }

    // ─── SummaryTooltip: the collapsed drawer's only information channel ───

    [Fact]
    public void HostDownTooltipIsJustThePresenceLine()
    {
        App.EmbeddedHostServices = new Provider(null);

        PhonePresenceMonitor.Instance.Refresh();

        PhonePresenceMonitor.Instance.SummaryTooltip.Should()
            .Be(PhonePresenceMonitor.Instance.PresenceText);
    }

    [Fact]
    public void NoPhoneTooltipIsJustThePresenceLine()
    {
        // No address to append — the same reason RemoteAddress is null here — so the tooltip must
        // not degrade into a format string with a hole in it.
        App.EmbeddedHostServices = new Provider(new Source([]));

        PhonePresenceMonitor.Instance.Refresh();

        PhonePresenceMonitor.Instance.SummaryTooltip.Should()
            .Be(PhonePresenceMonitor.Instance.PresenceText);
    }

    [Fact]
    public void OnePhoneTooltipAppendsTheAddress()
    {
        App.EmbeddedHostServices = new Provider(new Source([Phone("192.168.1.42", "Pixel 9")]));

        PhonePresenceMonitor.Instance.Refresh();

        var tooltip = PhonePresenceMonitor.Instance.SummaryTooltip;
        tooltip.Should().Contain(PhonePresenceMonitor.Instance.PresenceText);
        tooltip.Should().Contain("192.168.1.42");
        tooltip.Should().NotBe(PhonePresenceMonitor.Instance.PresenceText,
            "the address line has to actually be appended, not silently dropped");
    }

    // ─── THE DRIFT GUARD (spec §9): IsPhoneAttached and State can never disagree ───

    public static IEnumerable<object[]> SessionSources()
    {
        yield return [null!];
        yield return [Array.Empty<ClientSession>()];
        yield return [new[] { Phone("192.168.1.42", "Pixel 9") }];
        yield return [new[] { Phone("192.168.1.42", "Pixel 9"), Phone("192.168.1.43", "Pixel 8") }];
    }

    [Theory]
    [MemberData(nameof(SessionSources))]
    public void IsPhoneAttachedAgreesWithStateForEveryShapeOfSource(ClientSession[]? sessions)
    {
        // Without this guard, the new State-flavoured indicator could contradict the four indicators
        // RemEx-7zzw already lined up on IsPhoneAttached — the exact drift that bead was filed for.
        // A null row means NO SOURCE AT ALL — the host-down branch — not an empty one; collapsing it
        // to [] would leave the branch this bead was filed for unguarded (review, round 1).
        App.EmbeddedHostServices = new Provider(sessions is null ? null : new Source(sessions));

        PhonePresenceMonitor.Instance.Refresh();

        PhonePresenceMonitor.Instance.IsPhoneAttached.Should().Be(
            PhonePresenceMonitor.Instance.State == ShellConnectionState.PhoneAttached);
    }

    private static ClientSession Phone(string address, string? name) => new(address, name);

    private sealed class Source(IReadOnlyList<ClientSession> sessions) : IClientSessionSource
    {
        public IReadOnlyList<ClientSession> Snapshot() => sessions;
    }

    private sealed class Provider(IClientSessionSource? source) : IServiceProvider
    {
        public object? GetService(Type serviceType)
            => serviceType == typeof(IClientSessionSource) ? source : null;
    }
}
