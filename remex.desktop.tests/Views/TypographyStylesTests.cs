using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// RemEx-jt6w5.3. Source scan (this suite has no headless render — see <see cref="TypographyVocabularyTests"/>):
/// proves Styles/Typography.axaml re-declares every type-scale member with DynamicResource setters, that
/// App.axaml merges it after the Material theme, that the three kept classes bind the same keys, and —
/// the join — that every key the XAML binds is one <see cref="TypographyService"/> actually publishes.
/// </summary>
public class TypographyStylesTests
{
    private static readonly Dictionary<string, string> SectionOfMember = new()
    {
        ["Headline5"] = "Headers", ["Headline6"] = "Headers", ["Subtitle1"] = "Headers",
        ["Body2"] = "Body",
        ["Caption"] = "Small", ["Overline"] = "Small",
        ["SensorTitle"] = "Sensor", ["SensorMetricName"] = "Sensor",
    };

    [Fact]
    public void EveryTypeScaleMember_IsRedeclaredOnTheMaterialBase_WithDynamicSetters()
    {
        var markup = TypographyMarkup();

        foreach (var (member, section) in SectionOfMember)
        {
            var theme = Regex.Match(markup,
                $@"<ControlTheme x:Key=""{member}TextBlock"" TargetType=""TextBlock"" BasedOn=""\{{StaticResource MaterialTextBlock\}}"">(?<body>.*?)</ControlTheme>",
                RegexOptions.Singleline);

            theme.Success.Should().BeTrue($"{member}TextBlock must be re-declared BasedOn MaterialTextBlock");
            var body = theme.Groups["body"].Value;
            body.Should().Contain($@"<Setter Property=""FontSize"" Value=""{{DynamicResource Typo.{member}.FontSize}}""/>", member);
            body.Should().Contain($@"<Setter Property=""FontWeight"" Value=""{{DynamicResource Typo.{member}.FontWeight}}""/>", member);
            body.Should().Contain($@"<Setter Property=""Effect"" Value=""{{DynamicResource Typo.{section}.Effect}}""/>", member);
        }
    }

    [Fact]
    public void TheDefaultTextBlockTheme_CarriesTheEffectOnly()
    {
        // A FontSize or FontWeight setter here would outrank INHERITANCE and force 14/Regular onto the
        // TextBlock every ContentPresenter creates inside a Button — stripping the Medium Material
        // gives button labels at defaults. Untagged size goes through MaterialDesignFontSize and
        // untagged bold through TypographyService's runtime style (see its remarks).
        var theme = Regex.Match(TypographyMarkup(),
            @"<ControlTheme x:Key=""\{x:Type TextBlock\}"" TargetType=""TextBlock"" BasedOn=""\{StaticResource MaterialTextBlock\}"">(?<body>.*?)</ControlTheme>",
            RegexOptions.Singleline);

        theme.Success.Should().BeTrue();
        theme.Groups["body"].Value.Should().Contain(@"<Setter Property=""Effect"" Value=""{DynamicResource Typo.Body.Effect}""/>");
        theme.Groups["body"].Value.Should().NotContain(@"Property=""FontSize""").And.NotContain(@"Property=""FontWeight""");
    }

    [Fact]
    public void TypographyAxaml_HasNoInlineFontSize()
    {
        Regex.Matches(TypographyMarkup(), @"FontSize=""\d").Count.Should().Be(0,
            "every size is a DynamicResource; an inline number here would both bypass the slider and grow the ratchet");
    }

    [Fact]
    public void AppAxaml_IncludesTypography_AfterTheMaterialTheme_InsideApplicationStyles()
    {
        var app = AppMarkup();
        const string include = @"<StyleInclude Source=""avares://Remex.Desktop/Styles/Typography.axaml""/>";

        var includeAt = app.IndexOf(include, System.StringComparison.Ordinal);
        includeAt.Should().BeGreaterThan(app.IndexOf("<themes:MaterialTheme", System.StringComparison.Ordinal),
            "the overrides must load with Material's resources already reachable and must win the reverse-order lookup");
        includeAt.Should().BeLessThan(app.IndexOf("</Application.Styles>", System.StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("page-title", "PageTitle", "Headers", true)]
    [InlineData("page-subtitle", "PageSubtitle", "Body", true)]
    [InlineData("card-title", "CardTitle", "Headers", false)]
    public void TheKeptClasses_BindSizeWeightAndEffect_ToTypoKeys(string cls, string member, string section, bool carriesTheFontFamily)
    {
        var style = Regex.Match(AppMarkup(),
            $@"<Style Selector=""TextBlock\.{cls}"">(?<body>.*?)</Style>", RegexOptions.Singleline);

        style.Success.Should().BeTrue($"TextBlock.{cls} stays in App.axaml (TypographyVocabularyTests pins it)");
        var body = style.Groups["body"].Value;
        body.Should().Contain($@"<Setter Property=""FontSize"" Value=""{{DynamicResource Typo.{member}.FontSize}}""/>");
        body.Should().Contain($@"<Setter Property=""FontWeight"" Value=""{{DynamicResource Typo.{member}.FontWeight}}""/>");
        body.Should().Contain($@"<Setter Property=""Effect"" Value=""{{DynamicResource Typo.{section}.Effect}}""/>");
        if (carriesTheFontFamily) body.Should().Contain("PageTitleFontFamily", "the font picker's live binding must survive");
    }

    [Fact]
    public void EveryTypoKeyTheXamlBinds_IsOneTheServicePublishes()
    {
        var bound = Regex.Matches(AllAxamlMarkup(), @"\{DynamicResource (Typo\.[A-Za-z0-9]+\.[A-Za-z]+)\}")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToArray();
        var published = new TypographyService().Overrides.Keys.OfType<string>().ToHashSet();

        bound.Length.Should().BeGreaterThan(20, "if the scan finds nothing this test asserts nothing");
        bound.Where(k => !published.Contains(k)).Should().BeEmpty(
            "a key bound in XAML that the service never writes resolves to Unset forever, silently");
    }

    private static string TypographyMarkup() => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Styles", "Typography.axaml"));
    private static string AppMarkup() => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "App.axaml"));

    private static string AllAxamlMarkup()
    {
        var desktop = Path.Combine(RepoRoot(), "remex.desktop");
        return string.Concat(Directory.EnumerateFiles(desktop, "*.axaml", SearchOption.AllDirectories)
            .Select(File.ReadAllText));
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
