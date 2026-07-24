using System;
using System.IO;
using System.Linq;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// Guards the What's-New parser against the RemEx-xtc2 regression: an empty (or unreleased-only)
/// leading CHANGELOG section made <see cref="AboutViewModel.ParseChangelog"/> return 0 items, so the
/// About page silently fell back to stale hardcoded 2.0-era highlights for four releases.
/// </summary>
public class AboutViewModelChangelogTests
{
    [Fact]
    public void ParseChangelog_SkipsEmptyUnreleased_ReturnsNewestRelease()
    {
        const string md = """
            # Changelog

            ## [Unreleased]

            ## [2.4.0] - 2026-07-22

            ### Fixed

            - **First real item.** Something shipped.
            - **Second real item.** More shipped.

            ## [2.3.0] - 2026-07-18

            - **Older item.** Should not appear.
            """;

        var items = AboutViewModel.ParseChangelog(md, maxItems: 12);

        Assert.Equal(2, items.Count);
        Assert.Equal("First real item", items[0].Version);
        Assert.DoesNotContain(items, i => i.Version.Contains("Older item"));
    }

    [Fact]
    public void ParseChangelog_SkipsNonEmptyUnreleased_NeverShowsUnshippedEntries()
    {
        const string md = """
            ## [Unreleased]

            ### Fixed

            - **Unshipped item.** Not released yet.

            ## [2.4.0] - 2026-07-22

            - **Shipped item.** In the release.
            """;

        var items = AboutViewModel.ParseChangelog(md, maxItems: 12);

        Assert.Single(items);
        Assert.Equal("Shipped item", items[0].Version);
    }

    [Fact]
    public void ParseChangelog_SkipsEmptyReleasedSection_FallsThroughToNextRelease()
    {
        const string md = """
            ## [Unreleased]

            ## [2.4.1] - 2026-07-23

            ### Fixed

            ## [2.4.0] - 2026-07-22

            - **Populated item.** The newest section with content.
            """;

        var items = AboutViewModel.ParseChangelog(md, maxItems: 12);

        Assert.Single(items);
        Assert.Equal("Populated item", items[0].Version);
    }

    [Fact]
    public void ParseChangelog_AllSectionsEmpty_ReturnsNoItems()
    {
        const string md = """
            ## [Unreleased]

            ## [2.4.0] - 2026-07-22
            """;

        Assert.Empty(AboutViewModel.ParseChangelog(md, maxItems: 12));
    }

    [Fact]
    public void ParseChangelog_OnRealChangelog_YieldsNewestReleasedContent()
    {
        var markdown = File.ReadAllText(FindRepoChangelog());

        var items = AboutViewModel.ParseChangelog(markdown, maxItems: 12);

        Assert.NotEmpty(items);

        // The first returned title must come from the newest *released* section, not [Unreleased].
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var releasedHeading = Array.FindIndex(lines, l =>
            l.StartsWith("## [", StringComparison.Ordinal) &&
            !l.Contains("[Unreleased]", StringComparison.OrdinalIgnoreCase));
        Assert.True(releasedHeading >= 0, "docs/CHANGELOG.md has no released section heading.");
        var nextHeading = Array.FindIndex(lines, releasedHeading + 1,
            l => l.StartsWith("## [", StringComparison.Ordinal));
        var releasedSection = string.Join('\n',
            lines.Skip(releasedHeading).Take((nextHeading < 0 ? lines.Length : nextHeading) - releasedHeading));

        Assert.Contains($"**{items[0].Version}", releasedSection, StringComparison.Ordinal);
    }

    /// <summary>Walks up from the test bin directory to the repo's docs/CHANGELOG.md (the bundled asset's source).</summary>
    private static string FindRepoChangelog()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "CHANGELOG.md");
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("docs/CHANGELOG.md not found above " + AppContext.BaseDirectory);
    }
}
