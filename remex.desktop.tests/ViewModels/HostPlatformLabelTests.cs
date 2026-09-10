using System.Linq;
using FluentAssertions;
using Remex.Core.Models;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// Pins that the host's platform reaches the user as a name rather than as a wire token
/// (RemEx-6s34).
/// </summary>
/// <remarks>
/// <para>
/// <c>HostCapabilitiesProvider.GetPlatform</c> emits lowercase <c>windows</c> / <c>linux</c> /
/// <c>macos</c>, and two user-facing strings interpolated one of those directly: the About page's
/// host row and <c>HostRuntimeSummary</c>. The result read "2.4.0 (windows, Interactive)" — lowercase
/// mid-sentence next to a correctly-cased label, and untranslated in all eight non-English locales,
/// which the repo treats as a regression rather than a cosmetic issue.
/// </para>
/// <para>
/// THE SECOND DEFECT IN THE SAME LINE WAS THE WORSE ONE. <c>HostRuntimeSummary</c> built its text as
/// <c>$"{runtimeLabel} on {platform}"</c> — an English word hardcoded between two localized values,
/// and a word ORDER that several languages do not use. Hindi and Turkish both place the platform
/// first, so no amount of translating "on" would have produced a correct sentence; it had to become
/// a format string with positional arguments.
/// </para>
/// </remarks>
public class HostPlatformLabelTests
{
    private static ConnectionViewModel ConnectedTo(string platform, string runtimeMode = "interactive")
        => new()
        {
            IsConnected = true,
            HostCapabilities = new HostCapabilities
            {
                Version = "2.4.0.0",
                Platform = platform,
                RuntimeMode = runtimeMode,
            },
        };

    [Theory]
    [InlineData("windows", "Windows")]
    [InlineData("linux", "Linux")]
    [InlineData("macos", "macOS")]
    public void EachKnownPlatformTokenBecomesItsProperName(string wireToken, string expected)
    {
        ConnectedTo(wireToken).HostPlatformLabel.Should().Be(expected);
    }

    [Fact]
    public void AnUnrecognisedPlatformKeepsItsRawToken()
    {
        // Deliberately NOT a generic "Unknown". A host running on something this client has no name
        // for should still say what it is: "freebsd" is imperfect but diagnosable, whereas throwing
        // it away leaves the user and a support log with nothing.
        ConnectedTo("freebsd").HostPlatformLabel.Should().Be("freebsd");
    }

    [Fact]
    public void AMissingPlatformFallsBackToAWordRatherThanEmptiness()
    {
        var connection = new ConnectionViewModel { IsConnected = true };
        connection.HostPlatformLabel.Should().NotBeNullOrWhiteSpace(
            "an absent capabilities payload must still render something in the row");
    }

    [Fact]
    public void TheRuntimeSummaryNamesBothHalvesAndHardcodesNeither()
    {
        var summary = ConnectedTo("windows").HostRuntimeSummary;

        summary.Should().Contain("Windows");
        summary.Should().NotContain("windows,",
            "the raw lowercase token must not survive into the sentence");
    }

    [Fact]
    public void TheRuntimeSummaryPutsItsPartsWhereTheLanguageWantsThem()
    {
        // THE POINT OF THE FORMAT STRING. Hindi places the platform before the runtime label, which
        // interpolating an English "on" between them could never express. Asserting on ORDER rather
        // than on the translated words keeps this from being a spellcheck of the resx.
        var previous = LocalizationService.Instance.CultureTag;
        try
        {
            LocalizationService.Instance.SetCulture("hi");
            var hindi = ConnectedTo("windows").HostRuntimeSummary;
            hindi.Should().StartWith("Windows",
                "Hindi places the platform FIRST — an interpolated English \" on \" could not have "
                + "produced this word order at all, whatever it was translated to");

            LocalizationService.Instance.SetCulture("en");
            var english = ConnectedTo("windows").HostRuntimeSummary;
            english.Should().EndWith("Windows", "and English places it last");

            // The two orders differ, which is the whole justification for a positional format
            // string rather than a translated connector word.
            hindi.Should().NotBe(english);
        }
        finally
        {
            LocalizationService.Instance.SetCulture(previous);
        }
    }
}
