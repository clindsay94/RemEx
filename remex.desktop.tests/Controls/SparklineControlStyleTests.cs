using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Controls;

/// <summary>
/// Guards the app-level Style that supplies SparklineControl's theme-derived defaults (RemEx-qljv).
/// </summary>
/// <remarks>
/// SparklineControl's two <c>StyledProperty</c> registrations (<c>AccentColorProperty</c>,
/// <c>SecondaryAccentColorProperty</c>) still default to hex literals — a property default is baked
/// in at static registration, which runs before any theme is loaded, so plumbing cannot reach it
/// there. The <c>Style</c> in App.axaml is where the theme answer actually lands instead. Asserted on
/// the source, because the failure mode here is silent: a mistyped resource key still compiles and
/// still resolves to something (the fallback literal), and nothing at runtime reports it.
/// </remarks>
public class SparklineControlStyleTests
{
    [Fact]
    public void TheStyleSetsBothAccentsFromTheTheme()
    {
        var style = StyleFor("ctrl|SparklineControl");

        style.Should().MatchRegex(
            @"<Setter\s+Property=""AccentColor""\s+Value=""\{DynamicResource AccentPrimary\}""\s*/>",
            "the primary default must be the theme's own accent — the same move that settled "
            + "AccentForegroundBrush in RemEx-tq2e — not a re-invented literal");

        style.Should().MatchRegex(
            @"<Setter\s+Property=""SecondaryAccentColor""\s+Value=""\{DynamicResource PaletteTertiary\}""\s*/>",
            "the secondary default must be the palette's Tertiary role, per RemEx-qljv's decision");

        style.Should().NotContain("AccentPressed",
            "binding to AccentPressed(Brush) would couple a chart series to a button's pressed "
            + "state; PaletteTertiary is the same colour published under its own name for this");
    }

    [Fact]
    public void TheControlsNamespaceIsDeclared()
    {
        var app = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "App.axaml"));

        app.Should().MatchRegex(
            @"xmlns:ctrl=""using:Remex\.Desktop\.Controls""",
            "the ctrl: prefix the style above resolves through must be declared on the root element, "
            + "or the selector fails to bind and the Style is silently inert");
    }

    private static string StyleFor(string selector)
    {
        var app = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "App.axaml"));

        var match = Regex.Match(
            app, $@"<Style Selector=""{Regex.Escape(selector)}"">.*?</Style>", RegexOptions.Singleline);

        match.Success.Should().BeTrue($"App.axaml has to carry the shared {selector} rule");
        return match.Value;
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
