using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// Guards <see cref="Remex.Desktop.ViewModels.SensorViewModel.SecondaryAccentHex"/>'s
/// no-secondary-sensor fallback (RemEx-qljv).
/// </summary>
/// <remarks>
/// <para>
/// CanvasView binds this property directly onto <c>SparklineControl.SecondaryAccentColor</c>, and a
/// Binding always produces a value — so the App.axaml Style that gives SparklineControl its own
/// theme-derived default never gets a chance to run there; a local value always outranks a Style
/// setter. The old hardcoded <c>"#FFB020"</c> literal would therefore have survived the rest of this
/// bead's fix untouched, one layer up. The fallback must resolve <c>PaletteTertiary</c> through
/// <c>ThemeResources</c> instead.
/// </para>
/// <para>
/// ASSERTED ON THE SOURCE, NOT BEHAVIOURALLY, and that is deliberate — this assembly carries no
/// <c>Avalonia.Headless</c> reference anywhere (see <c>HardwareAccentInjectionTests</c>,
/// <c>CommandPaletteLightDismissTests</c> and others for the same constraint), so there is no live
/// <c>Application</c> to install a <c>PaletteTertiary</c> resource into and observe the resolved
/// colour. A behavioural test that only checks the RESULTING colour is blind to the fix for exactly
/// that reason: with no <c>Application.Current</c>, <c>ThemeResources.Color</c> degrades to its own
/// fallback and produces the identical string whether the property calls through
/// <c>ThemeResources.Color("PaletteTertiary", ...)</c> or still reads the bare literal — the earlier
/// version of this test asserted "#FFB020" and passed against both. Only the SHAPE of the
/// expression tells the two apart, so that is what is pinned.
/// </para>
/// </remarks>
public class SensorViewModelSecondaryAccentTests
{
    [Fact]
    public void TheNoSecondarySensorFallbackGoesThroughThemeResources()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "remex.desktop", "ViewModels", "SensorViewModel.cs"));

        var property = Regex.Match(source,
            @"public string SecondaryAccentHex\s*=>.*?;", RegexOptions.Singleline);
        property.Success.Should().BeTrue("SecondaryAccentHex moved or changed shape");

        property.Value.Should().MatchRegex(
            @"ThemeResources\.Color\(\s*""PaletteTertiary""",
            "the no-secondary-sensor fallback must resolve PaletteTertiary from the active theme "
            + "through ThemeResources, not a bare hex literal — CanvasView binds this straight onto "
            + "SparklineControl.SecondaryAccentColor, bypassing the App.axaml Style entirely, since "
            + "a Binding always produces a value");

        property.Value.Should().NotMatchRegex(@"""#FFB020""",
            "a bare hex literal here is exactly the bug RemEx-qljv fixed, surviving one layer up");
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
