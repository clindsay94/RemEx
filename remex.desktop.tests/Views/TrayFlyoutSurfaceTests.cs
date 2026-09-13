using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using Remex.Desktop.Services;
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
    public void PinnedSensorsAreCanvasCardsInAScrollingWrapPanel()
    {
        var text = File.ReadAllText(ViewPath);

        text.Should().Contain("ctrl:SensorCardContent",
            "the flyout should render pinned sensors with the shared card control, not its own markup");
        text.Should().Contain("<WrapPanel",
            "cards should lay out in a wrapping grid, not a horizontal strip");

        var scrollViewer = Regex.Match(text, @"<ScrollViewer\b[^>]*>", RegexOptions.Singleline);
        scrollViewer.Success.Should().BeTrue("the cards should sit inside a ScrollViewer");
        var normalizedScrollViewer = Regex.Replace(scrollViewer.Value, @"\s+", " ");
        normalizedScrollViewer.Should().Contain("x:Name=\"CardsScrollViewer\"",
            "ApplyMode (fix round 2) needs a named element to relax MaxHeight to PositiveInfinity while pinned");
        normalizedScrollViewer.Should().Contain("HorizontalScrollBarVisibility=\"Disabled\"");
        normalizedScrollViewer.Should().MatchRegex(
            @"MaxHeight=""\{x:Static svc:TrayFlyoutGeometry\.CardsMaxHeight\}""",
            "this is only the TRANSIENT starting value; ApplyMode overrides it at runtime per mode");

        var card = Regex.Match(text, @"<Border Classes=""flyout-card""[^>]*>", RegexOptions.Singleline);
        card.Success.Should().BeTrue("the card host should be a Border.flyout-card");
        var normalizedCard = Regex.Replace(card.Value, @"\s+", " ");
        normalizedCard.Should().Contain("Width=\"200\"");
        normalizedCard.Should().Contain("Height=\"150\"");
        normalizedCard.Should().Contain("Classes.family-primary=\"{Binding IsPrimaryFamily}\"");
        normalizedCard.Should().Contain("Classes.family-secondary=\"{Binding IsSecondaryFamily}\"");
        normalizedCard.Should().Contain("Classes.family-tertiary=\"{Binding IsTertiaryFamily}\"");
        normalizedCard.Should().Contain("Classes.family-neutral=\"{Binding IsNeutralFamily}\"");
        normalizedCard.Should().Contain("Classes.family-custom=\"{Binding IsCustomTheme}\"");

        text.Should().NotContain("Classes=\"tray-sensor\"", "the old strip's host class should be gone");
        text.Should().NotContain("Width=\"44\" Height=\"16\"", "the old strip's 44x16 sparkline should be gone");

        // Structural nesting (fix round 3, whole-branch review): a flat regex scan cannot tell
        // apart "these elements all exist somewhere in the file" from "they nest the way the spec
        // describes" — ScrollViewer > ItemsControl > ItemsPanel WrapPanel, ItemTemplate >
        // Border.flyout-card > ctrl:SensorCardContent. Parsed once so dropping or misplacing the
        // Sensor binding (the one thing that actually puts a sensor's data on the card) fails this
        // test instead of surviving a substring check.
        var doc = XDocument.Parse(text);
        XNamespace av = "https://github.com/avaloniaui";
        XNamespace xNs = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace ctrlNs = "using:Remex.Desktop.Controls";

        var namedScrollViewer = doc.Descendants(av + "ScrollViewer")
            .SingleOrDefault(sv => (string?)sv.Attribute(xNs + "Name") == "CardsScrollViewer");
        namedScrollViewer.Should().NotBeNull("the cards ScrollViewer must carry x:Name=\"CardsScrollViewer\"");

        var scrollViewerItemsControl = namedScrollViewer!.Elements(av + "ItemsControl").SingleOrDefault();
        scrollViewerItemsControl.Should().NotBeNull(
            "CardsScrollViewer's content must be exactly one ItemsControl, not some other wrapper");

        var itemsPanelProperty = scrollViewerItemsControl!.Element(av + "ItemsControl.ItemsPanel");
        itemsPanelProperty.Should().NotBeNull();
        itemsPanelProperty!.Descendants(av + "WrapPanel").Should().ContainSingle(
            "the ItemsControl's ItemsPanel must be a WrapPanel, not the old horizontal StackPanel");

        var itemTemplateProperty = scrollViewerItemsControl.Element(av + "ItemsControl.ItemTemplate");
        itemTemplateProperty.Should().NotBeNull();
        var dataTemplate = itemTemplateProperty!.Element(av + "DataTemplate");
        dataTemplate.Should().NotBeNull("the item template must be a DataTemplate");

        var itemBorder = dataTemplate!.Element(av + "Border");
        itemBorder.Should().NotBeNull("the item template's root must be the flyout-card Border directly");
        ((string?)itemBorder!.Attribute("Classes")).Should().Be("flyout-card");

        var cardContentElement = itemBorder.Element(ctrlNs + "SensorCardContent");
        cardContentElement.Should().NotBeNull(
            "the Border's content must be exactly one ctrl:SensorCardContent - the shared card control");
        ((string?)cardContentElement!.Attribute("Sensor")).Should().Be("{Binding}",
            "every flyout card must bind Sensor to the item - drop this and every card renders empty");

        // Padding (fix round 3): reproduces DraggableCard's own content inset
        // (Themes/Shared/DraggableCard.axaml:57) so the card's title/sparkline/plates sit inset
        // from the rounded corners instead of edge-to-edge.
        ((string?)itemBorder.Attribute("Padding")).Should().Be(
            "{Binding $self.CornerRadius, Converter={x:Static conv:CornerRadiusToMarginConverter.Instance}}",
            "the flyout card needs the same corner-radius content inset DraggableCard gives the canvas card");
    }

    /// <summary>
    /// Fix round 3 (whole-branch review): <see cref="TrayFlyoutGeometry.ChromeSideInset"/> and
    /// <see cref="TrayFlyoutGeometry.CardsPanelInset"/> are hand-derived from four XAML margins.
    /// This reads those four literals back out of the file and checks the arithmetic against the
    /// constants, so a margin changed in the XAML without updating the constant (or vice versa)
    /// fails here instead of silently making the column-fit promise (DefaultWidth/MaxWidth) wrong.
    /// </summary>
    [Fact]
    public void ChromeInsetConstantsMatchTheXamlMarginsTheyMirror()
    {
        var text = File.ReadAllText(ViewPath);

        // Outer Border (the transparent-window shadow-and-rounding frame around the card).
        var outerBorderMargin = double.Parse(Regex.Match(text,
                @"<Border Background=""\{DynamicResource GlassBaseDarkBrush\}""[^>]*Margin=""(?<v>[\d.]+)""")
            .Groups["v"].Value);

        // ContentGrid (the named Grid the header/cards/tiles rows live in).
        var contentGridMargin = double.Parse(Regex.Match(text,
                @"<Grid x:Name=""ContentGrid""[^>]*Margin=""(?<v>[\d.]+)""")
            .Groups["v"].Value);

        // The cards ItemsControl's own Margin="H,V" - only the horizontal half feeds CardsPanelInset.
        var itemsControlHorizontalMargin = double.Parse(Regex.Match(text,
                @"<ItemsControl ItemsSource=""\{Binding PinnedSensors\}"" Margin=""(?<h>[\d.]+),[\d.]+""")
            .Groups["h"].Value);

        // The flyout-card Border's own Margin (all four sides equal - single-value shorthand).
        var cardMargin = double.Parse(Regex.Match(text,
                @"<Border Classes=""flyout-card""[^>]*Margin=""(?<v>[\d.]+)""")
            .Groups["v"].Value);

        TrayFlyoutGeometry.ChromeSideInset.Should().Be(2 * outerBorderMargin + 2 * contentGridMargin,
            "ChromeSideInset is the outer Border's Margin plus the content Grid's Margin, each side");
        TrayFlyoutGeometry.CardsPanelInset.Should().Be(2 * itemsControlHorizontalMargin,
            "CardsPanelInset is the cards ItemsControl's own horizontal Margin, both sides");
        TrayFlyoutGeometry.CardPitch.Should().Be(200 + 2 * cardMargin,
            "CardPitch is the flyout-card's own Width (200, asserted elsewhere) plus its Margin, both sides");
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
