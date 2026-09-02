using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows.Input;
using FluentAssertions;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Pins what "unchanged" means for AboutView's Material type-scale sweep (RemEx-ep10v), the same
/// way <c>HomeViewCharacterisationTests</c> does for the dashboard: a source-text test, because
/// there is no headless render here and Avalonia binding failures are silent.
/// </summary>
public class AboutViewCharacterisationTests
{
    /// <summary>Every distinct <c>{Binding X}</c> / <c>Command="{Binding X}"</c> path AboutView uses today.</summary>
    private static readonly string[] ExpectedBindingPaths =
    [
        "!IsCheckingForUpdate",
        "#AboutCards.Bounds.Width",
        "Answer",
        "CheckForUpdatesCommand",
        "ClientBuildId",
        "ClientVersion",
        "Description",
        "DownloadUpdateCommand",
        "FaqItems",
        "HasClientBuildId",
        "HasHostBuildId",
        "HasUpdateStatus",
        "HostBuildId",
        "HostFingerprint",
        "HostVersion",
        "IsShowShortcutsOpen",
        "IsUpdateAvailable",
        "NavigateBackCommand",
        "OpenGitHubCommand",
        "Question",
        "ToggleShortcutsCommand",
        "UpdateStatusText",
        "Version",
        "WhatsNewItems",
    ];

    [Fact]
    public void EveryBindingPathResolvesToARealMemberOfAboutViewModel()
    {
        var unresolved = BindingPaths()
            .Distinct(StringComparer.Ordinal)
            .Where(path => !IsSpecial(path) && Resolve(path) is null)
            .ToArray();

        unresolved.Should().BeEmpty(
            "a binding to a path that does not exist on AboutViewModel resolves to nothing at "
            + "runtime with no error anywhere, and a Command bound that way gives a dead button");
    }

    [Fact]
    public void EveryCommandBindingIsActuallyACommand()
    {
        var notCommands = CommandBindings()
            .Distinct(StringComparer.Ordinal)
            .Select(path => (path, type: Resolve(path)))
            .Where(x => x.type is not null && !typeof(ICommand).IsAssignableFrom(x.type))
            .Select(x => $"{x.path} is {x.type!.Name}")
            .ToArray();

        notCommands.Should().BeEmpty("Command must be bound to an ICommand, not to a value");
    }

    [Fact]
    public void TheViewStillBindsTheSameSetOfPaths()
    {
        BindingPaths()
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .Should()
            .BeEquivalentTo(ExpectedBindingPaths.OrderBy(x => x, StringComparer.Ordinal),
                options => options.WithStrictOrdering(),
                "the type-scale sweep (RemEx-ep10v) may restyle AboutView but must not silently "
                + "change a binding, a command, or a localized key's wiring");
    }

    [Fact]
    public void TheViewHasExactlyOneMaterialHeroColorZone()
    {
        var about = About();

        Count(about, @"<material:ColorZone\b").Should().Be(1,
            "the logo block became a single Material ColorZone hero (RemEx-ep10v)");

        var zone = ElementWithAttributes(about, "material:ColorZone");
        zone.Should().Contain(@"Mode=""PrimaryMid""");
        zone.Should().Contain(@"assists:ShadowAssist.ShadowDepth=""Depth0""");
    }

    [Fact]
    public void EveryTextOrIconInsideTheHeroZoneSetsForegroundExplicitly()
    {
        // "An unset property is not a neutral property" (REGRESSION-GUARDS): Foreground does not
        // reliably inherit onto Material controls, so every TextBlock/MaterialIcon inside the
        // PrimaryMid zone must carry MaterialPrimaryMidForegroundBrush itself.
        var about = About();
        var zoneBody = Between(about, "<material:ColorZone", "</material:ColorZone>");
        var elements = Elements(zoneBody)
            .Where(e => e.StartsWith("<TextBlock", StringComparison.Ordinal)
                     || e.StartsWith("<mi:MaterialIcon", StringComparison.Ordinal))
            .ToArray();

        elements.Should().NotBeEmpty("the hero zone should still carry its tagline and version text");
        elements.Where(e => !e.Contains(@"Foreground=""{DynamicResource MaterialPrimaryMidForegroundBrush}""",
                StringComparison.Ordinal))
            .Should().BeEmpty("every element inside the PrimaryMid zone must set its own Foreground");
    }

    [Fact]
    public void InlineFontSizeIsDownToTheSelectableTextBlockExceptions()
    {
        // The two build-id rows and the fingerprint row are SelectableTextBlocks: a Theme on one
        // replaces its own theme and kills SelectionBrush, so they keep inline FontSize on purpose.
        Count(About(), @"FontSize=""\d").Should().BeLessOrEqualTo(3,
            "AboutView should be down to at most the SelectableTextBlock exceptions after the "
            + "Material type-scale sweep (RemEx-ep10v)");
    }

    [Fact]
    public void NoThemeIsSetOnASelectableTextBlock()
    {
        // A Theme on a SelectableTextBlock silently replaces its control theme (and SelectionBrush)
        // with the TextBlock one — this is the trap the conventions brief calls out by name.
        var offenders = Elements(About())
            .Where(e => e.StartsWith("<SelectableTextBlock", StringComparison.Ordinal)
                     && e.Contains("Theme=", StringComparison.Ordinal))
            .ToArray();

        offenders.Should().BeEmpty("a SelectableTextBlock must never carry a TextBlock Theme");
    }

    [Fact]
    public void NoLegacyHeightOneDividerBordersRemain()
    {
        About().Should().NotMatchRegex(@"<Border Height=""1""",
            "the three hand-rolled divider Borders became Material Separator elements");

        Count(About(), @"<Separator\b").Should().Be(3,
            "AboutView's version card has three section dividers");
    }

    // ─────────────────────────── plumbing ───────────────────────────

    private static bool IsSpecial(string path) => path.StartsWith('!') || path.StartsWith('#');

    private static string[] BindingPaths()
    {
        var found = Regex.Matches(About(), @"(?<![\w.])(?:Command=)?""\{(?:Compiled)?Binding ([^,}]+)(?:,[^}]*(?:\{[^}]*\}[^}]*)*)?\}""")
            .Select(m => m.Groups[1].Value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        found.Should().NotBeEmpty(
            "a matcher that finds nothing makes every assertion built on it pass vacuously");

        return found;
    }

    private static string[] CommandBindings() =>
        [.. Regex.Matches(About(), @"(?<![\w.])Command=""\{(?:Compiled)?Binding ([^}]+)\}""")
            .Select(m => m.Groups[1].Value.Trim())];

    /// <summary>
    /// Item templates (WhatsNewItems / FaqItems) rebind DataContext to their record element, so a
    /// bare "Version"/"Description"/"Question"/"Answer" never lives on AboutViewModel itself — try
    /// those record types before giving up.
    /// </summary>
    private static readonly Type[] TemplateItemTypes = [typeof(WhatsNewItem), typeof(FaqItem)];

    private static Type? Resolve(string path)
    {
        var segments = path.Split('.');

        foreach (var root in (Type[])[typeof(AboutViewModel), .. TemplateItemTypes])
        {
            Type current = root;
            var resolved = true;

            foreach (var segment in segments)
            {
                var property = current.GetProperty(segment, BindingFlags.Public | BindingFlags.Instance);
                if (property is null) { resolved = false; break; }
                current = property.PropertyType;
            }

            if (resolved) return current;
        }

        return null;
    }

    private static string[] Elements(string axaml) =>
        [.. Regex.Matches(axaml, @"<[A-Za-z][\w:.]*(?:\s[^<>]*)?/?>", RegexOptions.Singleline)
            .Select(m => Regex.Replace(m.Value, @"\s+", " "))];

    private static string ElementWithAttributes(string axaml, string tagName)
    {
        var match = Regex.Match(axaml, $@"<{Regex.Escape(tagName)}\b[^>]*>", RegexOptions.Singleline);
        match.Success.Should().BeTrue($"AboutView should contain a {tagName} element");
        return Regex.Replace(match.Value, @"\s+", " ");
    }

    private static string Between(string text, string open, string close)
    {
        var from = text.IndexOf(open, StringComparison.Ordinal);
        var to = text.IndexOf(close, StringComparison.Ordinal);
        (from >= 0 && to > from).Should().BeTrue($"AboutView should still contain a {open} block");
        return text[from..to];
    }

    private static int Count(string text, string pattern) => Regex.Matches(text, pattern).Count;

    private static string About()
        => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "AboutView.axaml"));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
