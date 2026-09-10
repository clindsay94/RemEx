using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Remex.Desktop.Services.Security;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Pins that the PC renders its own certificate fingerprint the way the phone renders the one it
/// pinned, so the user can compare the two screens without converting anything (RemEx-n8xk).
/// </summary>
/// <remarks>
/// The Android dialog from RemEx-vnps asks the user to judge whether a certificate change is
/// legitimate by checking the fingerprint against their PC. That instruction is only followable if
/// both ends spell the value identically — a different truncation length, or groups of five, and
/// the user is comparing two strings that differ for a machine that did nothing.
/// </remarks>
public class SpkiFingerprintDisplayTests
{
    // The same two pins the Kotlin suite uses (SpkiFingerprintTest.kt), so a divergence shows up as
    // a difference in the EXPECTED text rather than in the inputs.
    private const string PinA = "n8Kq2LxV9dR4tYbF7mJ3wZcA1sQeH6uP0iO5gT8kX2M=";
    private const string PinB = "n8Kq2LxV9dR4tYbF0000000000000000000000000A0=";

    [Fact]
    public void TheDisplayFormIsShortAndGroupedTheSameWayTheAndroidClientShowsIt()
    {
        // Byte-for-byte the string SpkiFingerprintTest.kt asserts for the same input.
        Assert.Equal("n8Kq 2LxV 9dR4 tYbF", SpkiFingerprintDisplay.ForDisplay(PinA));
    }

    [Fact]
    public void ThePrefixedAndBareSpellingsOfOnePinRenderIdentically()
    {
        // The codebase writes pins both ways. Two spellings of ONE certificate must not look like
        // two certificates to someone comparing this row against their phone.
        Assert.Equal(
            SpkiFingerprintDisplay.ForDisplay(PinA),
            SpkiFingerprintDisplay.ForDisplay("sha256/" + PinA));
        Assert.Equal(
            SpkiFingerprintDisplay.ForDisplay(PinA),
            SpkiFingerprintDisplay.ForDisplay("  sha256/" + PinA + "  "));
    }

    [Fact]
    public void Base64CaseSurvivesTheRender()
    {
        // Base64 is case-significant. A formatter that upper-cased for tidiness would show the same
        // text for two different certificates.
        Assert.NotEqual(
            SpkiFingerprintDisplay.ForDisplay(PinA),
            SpkiFingerprintDisplay.ForDisplay(PinA.ToLowerInvariant()));
    }

    [Fact]
    public void TwoCertificatesSharingAPrefixLookTheSameHere_WhichIsWhyNothingComparesThisString()
    {
        // Documents the hazard rather than a behaviour: these two pins differ, yet their DISPLAY
        // forms are identical because the display is truncated. That is fine for a human reading one
        // value off a screen and unacceptable as an equality test, which is why this class offers no
        // comparison method and the pinning decision works on whole hashes (CertificatePinPolicy).
        Assert.Equal(
            SpkiFingerprintDisplay.ForDisplay(PinA),
            SpkiFingerprintDisplay.ForDisplay(PinB));
        Assert.NotEqual(PinA, PinB);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sha256/")]
    public void AnAbsentFingerprintShowsAMarkerRatherThanABlank(string? pin)
    {
        // A blank row reads as "this PC has no certificate", which is a different and more alarming
        // statement than "RemEx cannot tell you yet".
        Assert.Equal(SpkiFingerprintDisplay.Unavailable, SpkiFingerprintDisplay.ForDisplay(pin));
    }

    [Fact]
    public void AFingerprintShorterThanTheDisplayLengthIsShownInFullRatherThanPadded()
    {
        // Defensive, and matched to the Kotlin case: render what is there, never invent characters
        // the user might then try to compare.
        Assert.Equal("abcd ef", SpkiFingerprintDisplay.ForDisplay("abcdef"));
    }

    /// <summary>
    /// THE TEST THIS FILE EXISTS FOR: the two implementations are pinned to each other, not merely
    /// written to match once.
    /// </summary>
    /// <remarks>
    /// Read out of the Kotlin source rather than duplicated as a literal, because a duplicated
    /// literal is what drift looks like. Someone widening the Android display to 20 characters gets
    /// a failing PC test naming the file they changed, instead of a silent mismatch that only shows
    /// up when a worried user is holding a phone next to a monitor.
    /// </remarks>
    [Fact]
    public void TheAndroidFormatterAndThisOneAgreeOnLengthGroupingAndTheAbsentMarker()
    {
        var kotlin = File.ReadAllText(Path.Combine(
            RepoRoot, "remex.android", "app", "src", "main", "java", "com", "clindsay94",
            "remex", "security", "SpkiFingerprint.kt"));

        Assert.Equal(SpkiFingerprintDisplay.DisplayLength, KotlinIntConstant(kotlin, "DisplayLength"));
        Assert.Equal(SpkiFingerprintDisplay.GroupSize, KotlinIntConstant(kotlin, "GroupSize"));
        Assert.Equal(SpkiFingerprintDisplay.Unavailable, KotlinStringConstant(kotlin, "Unavailable"));

        // The grouping character is a plain space on both sides. Kotlin spells it in the
        // joinToString call, so it cannot be read as a constant.
        Assert.Contains("joinToString(\" \")", kotlin);
    }

    private static int KotlinIntConstant(string source, string name)
    {
        var match = Regex.Match(source, $@"const\s+val\s+{Regex.Escape(name)}\s*(?::\s*Int\s*)?=\s*(\d+)");
        Assert.True(match.Success, $"SpkiFingerprint.kt no longer declares an Int constant named {name}");
        return int.Parse(match.Groups[1].Value);
    }

    private static string KotlinStringConstant(string source, string name)
    {
        var match = Regex.Match(source, $@"const\s+val\s+{Regex.Escape(name)}\s*(?::\s*String\s*)?=\s*""([^""]*)""");
        Assert.True(match.Success, $"SpkiFingerprint.kt no longer declares a String constant named {name}");
        return match.Groups[1].Value;
    }

    private static string RepoRoot =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(GetThisFilePath())!, "..", ".."));

    private static string GetThisFilePath([CallerFilePath] string path = "") => path;
}
