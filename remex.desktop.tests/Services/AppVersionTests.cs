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

        var plus = informational!.IndexOf('+');
        var expected = plus >= 0 ? informational[..plus] : informational;
        Assert.Equal(expected, AppVersion.Resolve(DesktopAssembly));
    }
}
