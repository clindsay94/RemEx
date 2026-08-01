using System;
using System.Collections.Generic;
using FluentAssertions;
using Remex.Core.Models;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// Pins that the About page shows the host's version in the same form it shows this app's
/// (RemEx-8jzu).
/// </summary>
/// <remarks>
/// <para>
/// The host builds its capabilities version from <c>GetName().Version</c>, which .NET always widens
/// to four components. RemEx-nll1 fixed the local row to render "2.4.0" and left this one, so the
/// same machine was labelled "2.4.0" and "2.4.0.0" one divider apart on a single screen — the sort of
/// difference a non-technical user reads as two different things being installed.
/// </para>
/// <para>
/// TESTED AT THE VIEW MODEL, not at the helper. Asserting only that
/// <see cref="AppVersion.Normalize"/> shortens a string proves the helper works and says nothing
/// about whether the About page calls it — reverting the call site to use the wire value verbatim
/// leaves every helper test green. This drives the property the page actually binds to.
/// </para>
/// </remarks>
public class AboutHostVersionTests : IDisposable
{
    private readonly List<AboutViewModel> _created = [];

    /// <summary>
    /// AboutViewModel is IDisposable and subscribes to the static LocalizationService in its
    /// constructor, so an undisposed one keeps receiving locale changes for the life of the test
    /// assembly — and another test class in this assembly does switch the culture. Nothing on that
    /// handler's path can throw today; disposing anyway keeps that from being load-bearing.
    /// </summary>
    public void Dispose()
    {
        foreach (var about in _created) about.Dispose();
        GC.SuppressFinalize(this);
    }

    private AboutViewModel ConnectedTo(string hostVersion)
    {
        var connection = new ConnectionViewModel { IsConnected = true };
        // null shell: AboutViewModel only stores it, and nothing on the version path reads it. The
        // real one needs five services with Guard.NotNull, which would be a lot of construction to
        // assert a string format.
        var about = new AboutViewModel(connection, null!);
        _created.Add(about);

        // Assigned AFTER construction so the view model's own change handler runs, which is the
        // path production takes when capabilities arrive on connect.
        connection.HostCapabilities = new HostCapabilities
        {
            Version = hostVersion,
            // Lowercase because that is what HostCapabilitiesProvider.GetPlatform actually returns.
            // The bead's prose said "Windows" and the first version of this test copied it, which
            // would have pinned a wire shape the host never sends.
            Platform = "windows",
            RuntimeMode = "interactive",
        };

        return about;
    }

    [Fact]
    public void AFourPartHostVersionIsShownInTheSameFormAsThisApp()
    {
        // THE BEAD. The trailing ".0" is noise, and it is noise that makes two rows describing one
        // machine disagree.
        ConnectedTo("2.4.0.0").HostVersion.Should().StartWith("2.4.0 (",
            "the host row must read like the app row, which shows three components");
        ConnectedTo("2.4.0.0").HostVersion.Should().NotContain("2.4.0.0");
    }

    [Fact]
    public void AVersionThatIsAlreadyShortIsLeftAlone()
    {
        ConnectedTo("2.4.0").HostVersion.Should().StartWith("2.4.0 (");
    }

    [Fact]
    public void ASourceRevisionSuffixIsNotShownToTheUser()
    {
        ConnectedTo("2.4.0+abc123").HostVersion.Should().NotContain("abc123");
    }

    [Fact]
    public void AHostThatCannotReportItsVersionStillNamesItsPlatform()
    {
        // "unknown" is the host's literal for "I could not determine this", and the page branches on
        // it. Normalising must not mangle it into something that branch no longer recognises, or the
        // row silently becomes "unknown (Windows, ...)" instead of naming just the platform.
        var about = ConnectedTo("unknown");

        about.HostVersion.Should().NotContain("unknown");
        about.HostVersion.Should().StartWith("windows (");
    }
}
