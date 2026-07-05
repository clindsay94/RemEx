using Remex.Agent.Services.ScreenCapture;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Tests for <see cref="CaptureBackendPreference.Parse"/> — the pure string→enum core of the HKLM
/// capture-backend toggle (<c>HKLM\SOFTWARE\RemEx\Capture\Backend</c>). The registry read itself is
/// Windows-only and not CI-testable; the parse is where the fail-open contract lives, so it is covered
/// deterministically here. Every unrecognized / absent value must resolve to <see cref="CaptureBackend.Auto"/>
/// so a bad config can never disable capture.
/// </summary>
public class CaptureBackendPreferenceTests
{
    [Theory]
    [InlineData("Wgc", CaptureBackend.Wgc)]
    [InlineData("Dxgi", CaptureBackend.Dxgi)]
    [InlineData("Gdi", CaptureBackend.Gdi)]
    [InlineData("Auto", CaptureBackend.Auto)]
    public void Parse_KnownValues_MapToBackend(string value, CaptureBackend expected)
    {
        Assert.Equal(expected, CaptureBackendPreference.Parse(value));
    }

    [Theory]
    [InlineData("wgc")]
    [InlineData("WGC")]
    [InlineData("  Dxgi  ")]
    [InlineData("gDi")]
    public void Parse_IsCaseInsensitiveAndTrimmed(string value)
    {
        // All of these are recognized regardless of case/whitespace — i.e. they do NOT fall through to Auto
        // (except the explicit Auto cases, which are covered above).
        var parsed = CaptureBackendPreference.Parse(value);
        Assert.NotEqual(CaptureBackend.Auto, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nonsense")]
    [InlineData("d3d11")]
    [InlineData("0")]
    public void Parse_UnknownOrMissing_FailsOpenToAuto(string? value)
    {
        Assert.Equal(CaptureBackend.Auto, CaptureBackendPreference.Parse(value));
    }
}
