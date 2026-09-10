using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Guards the Material surface migration of <c>TrayFlyoutWindow.axaml</c> (RemEx-tzl85): the type
/// scale, the presence <c>material:Badged</c> and the <c>MaterialIcon</c> close button. Source scan,
/// not a rendering test — there is no headless Avalonia harness here (see
/// <see cref="TypographyVocabularyTests"/>).
/// </summary>
public class TrayFlyoutSurfaceTests
{
    private static readonly string ViewPath = Path.Combine(RepoRoot(), "remex.desktop", "Views", "TrayFlyoutWindow.axaml");

    [Fact]
    public void NoInlineFontSizeSurvives()
    {
        var text = File.ReadAllText(ViewPath);
        Regex.Matches(text, @"FontSize=""").Should().BeEmpty(
            "every TextBlock in TrayFlyoutWindow.axaml should be on a type-scale Theme, not an inline size");
    }

    [Fact]
    public void EveryTextBlockCarriesATypeScaleTheme()
    {
        var text = File.ReadAllText(ViewPath);
        var textBlocks = Regex.Matches(text, @"<TextBlock(?:\.[A-Za-z]+)?\b[^>]*>", RegexOptions.Singleline)
            .Select(m => Regex.Replace(m.Value, @"\s+", " "));

        foreach (var element in textBlocks)
        {
            // TextBlock.Text property-element syntax (e.g. <TextBlock.Text>) carries no attributes
            // of its own and is not the element the Theme lives on - skip it.
            if (element.StartsWith("<TextBlock.")) continue;

            element.Should().MatchRegex(@"Theme=""\{StaticResource \w+TextBlock\}""",
                $"every TextBlock should carry a vocabulary Theme, but found: {element}");
        }
    }

    [Fact]
    public void PresenceIsAMaterialBadgedBoundToPhonePresence()
    {
        var text = File.ReadAllText(ViewPath);

        text.Should().NotContain("<Ellipse", "the status-dot Ellipse should be replaced by material:Badged");

        var badged = Regex.Match(text, @"<material:Badged\b[^>]*>", RegexOptions.Singleline);
        badged.Success.Should().BeTrue("TrayFlyoutWindow.axaml should carry a material:Badged presence indicator");

        var normalized = Regex.Replace(badged.Value, @"\s+", " ");
        normalized.Should().Contain("Classes=\"presence\"");
        normalized.Should().MatchRegex(@"Classes\.connected=""\{Binding Presence\.IsPhoneAttached\}""");
    }

    [Fact]
    public void CloseButtonUsesAMaterialIconNotAGlyph()
    {
        var text = File.ReadAllText(ViewPath);

        text.Should().NotContain("&#x2715;", "the literal close glyph should be replaced by a MaterialIcon");
        text.Should().NotContain("✕", "no raw close-glyph character should survive either");

        var closeButton = Regex.Match(text,
            @"<Button Grid\.Column=""4""[^>]*Click=""OnCloseFlyout""[^>]*>.*?</Button>",
            RegexOptions.Singleline);
        closeButton.Success.Should().BeTrue("the close button should still exist");
        closeButton.Value.Should().Contain("Kind=\"Close\"");
    }

    [Fact]
    public void NoMaterialIconCarriesAFillAttribute()
    {
        var text = File.ReadAllText(ViewPath);
        Regex.Matches(text, @"<mi:MaterialIcon\b[^>]*>", RegexOptions.Singleline)
            .Select(m => m.Value)
            .Should().OnlyContain(icon => !icon.Contains("Fill="),
                "MaterialIcon does not honour Fill - it does nothing and is a leftover from Path icons");
    }

    [Fact]
    public void BehaviouralAnchorsAreUntouched()
    {
        var text = File.ReadAllText(ViewPath);

        // OnDeactivated is wired in the code-behind constructor (Deactivated += OnDeactivated), not
        // in markup - it has no Click handler here, so it is not asserted against the .axaml text.
        text.Should().Contain("OnHeaderPressed");
        text.Should().Contain("OnResizePressed");
        text.Should().Contain("OnTogglePin");
        text.Should().Contain("IsChecked=\"{Binding IsPinned, Mode=OneWay}\"");
    }

    /// <summary>
    /// Repo root via <c>[CallerFilePath]</c> - matches the pattern used by
    /// <see cref="TypographyVocabularyTests"/> so this file survives being moved or run from a
    /// different working directory.
    /// </summary>
    private static string RepoRoot([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));
}
