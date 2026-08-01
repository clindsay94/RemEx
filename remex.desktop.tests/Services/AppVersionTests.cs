using System.Reflection;
using System.Text.RegularExpressions;
using Remex.Desktop.Services;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Tests for <see cref="AppVersion"/> — the About page and the splash both display what this
/// resolves, and the point of the helper is that a PC release is labelled the same way Android
/// labels it ("2.4.0"), not as a four-part <c>Version.ToString()</c> ("2.4.0.0").
/// </summary>
public sealed class AppVersionTests
{
    private static readonly Assembly DesktopAssembly = typeof(AppVersion).Assembly;

    [Fact]
    public void Display_HasThreeComponents_MatchingAndroidVersionNameForm()
    {
        // Android's About screen renders BuildConfig.VERSION_NAME, which is versionName from
        // remex.android/app/version.properties — always major.minor.patch.
        // Deliberate tripwire on the release process: every release so far has been a plain
        // three-part <Version>, and shipping a prerelease ("2.5.0-rc1") or two-part version would
        // fail here on purpose, because Android's versionName cannot express either.
        Assert.Matches(new Regex(@"^\d+\.\d+\.\d+$"), AppVersion.Display);
    }

    [Fact]
    public void Display_OmitsSourceRevisionSuffix()
    {
        // The SDK appends "+<git sha>" to the informational version by default since .NET 8; a
        // commit hash means nothing to the target user and must not reach the About page.
        Assert.DoesNotContain('+', AppVersion.Display);
    }

    [Fact]
    public void Display_IsNotTheFourPartAssemblyVersion()
    {
        // The regression this guards: AboutViewModel used to render
        // Assembly.GetName().Version.ToString(), which always widens to four components.
        var fourPart = DesktopAssembly.GetName().Version?.ToString();

        Assert.NotNull(fourPart);
        Assert.NotEqual(fourPart, AppVersion.Display);
        Assert.StartsWith(AppVersion.Display, fourPart);
    }

    [Fact]
    public void Resolve_AgreesWithDisplay_ForTheDesktopAssembly()
    {
        // Display is a cached Resolve of this assembly; keep the test seam honest about that.
        Assert.Equal(AppVersion.Display, AppVersion.Resolve(DesktopAssembly));
    }

    [Fact]
    public void Resolve_HandlesAnAssemblyOtherThanOurOwn()
    {
        // Exercises the seam on an assembly this repo does not version, so the test cannot pass by
        // accident on our own <Version>. The BCL ships an informational version with a "+<sha>"
        // suffix too, so the trimming path is what is under test here.
        var corelib = AppVersion.Resolve(typeof(object).Assembly);

        Assert.False(string.IsNullOrEmpty(corelib));
        Assert.DoesNotContain('+', corelib);
    }

    [Fact]
    public void Resolve_DerivesFromInformationalVersion_NotAssemblyVersion()
    {
        var informational = DesktopAssembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        Assert.False(string.IsNullOrEmpty(informational));

        // Was a hand-rolled copy of Resolve's old '+' trimming, which passed only because our
        // informational version happens to be three-part; with a four-part or prerelease <Version>
        // its expectation and Resolve would diverge and it would fail for a reason unrelated to what
        // it claims to test. Now it asks the same reduction Resolve uses (RemEx-8jzu).
        Assert.Equal(AppVersion.Normalize(informational), AppVersion.Resolve(DesktopAssembly));

        // And the property the name actually promises, which the mirror never checked.
        Assert.NotEqual(DesktopAssembly.GetName().Version!.ToString(), AppVersion.Resolve(DesktopAssembly));
    }

    /// <summary>
    /// <see cref="AppVersion.Normalize"/> reduces a version reported by a REMOTE host to the same
    /// display form this app uses for its own (RemEx-8jzu).
    /// </summary>
    /// <remarks>
    /// The host builds its capabilities version from <c>GetName().Version</c>, which is always
    /// widened to four components, so the About page showed "2.4.0" for this app and "2.4.0.0" for
    /// the machine it was running on, one divider apart. Normalising at the display layer keeps the
    /// wire payload — which the Android client parses — untouched.
    /// </remarks>
    [Theory]
    [InlineData("2.4.0.0", "2.4.0")]
    [InlineData("2.4.0", "2.4.0")]
    [InlineData("2.4.1.7", "2.4.1")]
    [InlineData("2.4.0+abc123", "2.4.0")]
    [InlineData("2.4.0.0+abc123", "2.4.0")]
    [InlineData("2.4", "2.4")]
    public void Normalize_ReducesToTheDisplayForm(string raw, string expected)
    {
        Assert.Equal(expected, AppVersion.Normalize(raw));
    }

    [Fact]
    public void Normalize_LeavesAValueThatIsNotAVersionAlone()
    {
        // The host sends the literal "unknown" when it cannot determine its own version, and the
        // About page tests for exactly that string to decide what to render. Blanking or mangling it
        // would turn a diagnosable value into an empty row.
        Assert.Equal("unknown", AppVersion.Normalize("unknown"));
        Assert.Equal("2.4.0-beta", AppVersion.Normalize("2.4.0-beta"));
    }

    [Fact]
    public void Normalize_TreatsMissingInputAsAbsentRatherThanThrowing()
    {
        Assert.Equal(string.Empty, AppVersion.Normalize(null));
        Assert.Equal(string.Empty, AppVersion.Normalize(""));
        Assert.Equal(string.Empty, AppVersion.Normalize("   "));
    }

    [Fact]
    public void Normalize_AgreesWithResolve_ForThisAssembly()
    {
        // The property that actually matters: the two rows on the About page cannot disagree,
        // because both now go through the same reduction. Feeding Resolve's own output back through
        // Normalize must be a no-op, and the four-part assembly version must reduce to it.
        Assert.Equal(AppVersion.Display, AppVersion.Normalize(AppVersion.Display));
        Assert.Equal(
            AppVersion.Display,
            AppVersion.Normalize(DesktopAssembly.GetName().Version!.ToString()));
    }
}
