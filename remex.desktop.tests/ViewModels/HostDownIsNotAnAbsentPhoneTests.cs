using System;
using System.Collections.Generic;
using FluentAssertions;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// A dead embedded host does not read as "no phone paired" (RemEx-t6s1).
/// </summary>
/// <remarks>
/// The shell's status dot means "a phone is attached", and with the drawer COLLAPSED the two text
/// lines beside it are hidden — so the dot is the entire indicator. The host starts inside a
/// try/catch and can fail; when it does, nothing registers IClientSessionSource and the reading
/// collapsed to the same one a healthy host with no paired phone produces. The user then checks
/// their phone, their Wi-Fi and their pairing while the fault is on the PC.
/// </remarks>
public class HostDownIsNotAnAbsentPhoneTests : IDisposable
{
    private readonly IServiceProvider? _saved = App.EmbeddedHostServices;

    public void Dispose()
    {
        App.EmbeddedHostServices = _saved;
        PhonePresenceMonitor.Instance.Refresh();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void WithNoHostRegisteredItSaysTHATRatherThanReportingPhoneAbsence()
    {
        App.EmbeddedHostServices = new Provider(null);

        PhonePresenceMonitor.Instance.Refresh();

        var expected = LocalizationService.Instance["Shell_PhonePresenceHostDown"];
        PhonePresenceMonitor.Instance.PresenceText.Should().Be(expected);
        PhonePresenceMonitor.Instance.IsPhoneAttached.Should().BeFalse();

        // THE SCREEN READER HEARS THE SAME THING. Reusing the no-phone accessible name would say
        // "no phone" while the row said the host is down — an indicator that disagrees with itself
        // is worse than one that says nothing.
        PhonePresenceMonitor.Instance.PresenceAccessibleName.Should().Be(expected);
    }

    [Fact]
    public void AHOSTWithNoSessionsStillReadsAsNoPhone()
    {
        // THE OTHER SIDE, and the one that keeps the branch honest: a healthy host with nothing
        // paired must NOT claim the PC is broken. Evaluate(null) cannot tell these apart, which is
        // why the branch is on the source rather than on the snapshot.
        App.EmbeddedHostServices = new Provider(new EmptySource());

        PhonePresenceMonitor.Instance.Refresh();

        PhonePresenceMonitor.Instance.PresenceText.Should()
            .NotBe(LocalizationService.Instance["Shell_PhonePresenceHostDown"]);
        PhonePresenceMonitor.Instance.IsPhoneAttached.Should().BeFalse();
    }

    private sealed class EmptySource : IClientSessionSource
    {
        public IReadOnlyList<ClientSession> Snapshot() => [];
    }

    private sealed class Provider(IClientSessionSource? source) : IServiceProvider
    {
        public object? GetService(Type serviceType)
            => serviceType == typeof(IClientSessionSource) ? source : null;
    }
}
