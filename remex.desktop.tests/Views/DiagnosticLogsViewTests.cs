using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Guards the DiagnosticLogsView Material migration (RemEx-peagx): floating labels on every filter
/// field, both hand-painted log surfaces becoming <c>material:Card</c>, and a severity icon that lets
/// the log list read without relying on colour.
/// </summary>
/// <remarks>
/// A SOURCE-TEXT TEST for the usual reason in this folder: no headless render in this suite, so a
/// regression here (a label silently dropped, a Border creeping back in) throws nothing and paints
/// wrong instead.
/// </remarks>
public class DiagnosticLogsViewTests
{
    private const string Avalonia = "https://github.com/avaloniaui";
    private static readonly string Source = ViewSource();

    [Fact]
    public void SixLabelAssistAttributes_ArePresent()
    {
        // Verbosity, Preset, Capture level, export format and export scope, plus the search
        // TextBox's floating label — six ComboBoxAssist/TextFieldAssist.Label attributes to
        // replace the standalone label TextBlocks that were deleted (Logs_ServiceDesc becomes a
        // Body2 caption rather than a field label). The export-scope ComboBox picked up its own
        // Logs_ExportScope resx key (all nine locale .resx files) so every ComboBox on the page
        // carries a floating label and an accessible name (RemEx-peagx, fix round 3).
        var comboBoxLabelCount = CountOccurrences(Source, "assists:ComboBoxAssist.Label=");
        var textFieldLabelCount = CountOccurrences(Source, "assists:TextFieldAssist.Label=");

        (comboBoxLabelCount + textFieldLabelCount).Should().Be(6,
            "five ComboBoxes and the search TextBox each carry a floating label");
    }

    [Fact]
    public void NoStandaloneFilterLabelTextBlocksRemain()
    {
        Source.Should().NotContain("Logs_VerbosityFilter}\" FontSize");
        Source.Should().NotContain("Logs_Preset}\" FontSize");
        Source.Should().NotContain("Logs_CaptureLevel}\" FontSize");
        Source.Should().NotContain("Logs_ExportAs}\" FontSize");
    }

    [Fact]
    public void NeitherLogSurface_IsAGlassBorderAnyMore()
    {
        Source.Should().NotContain("GlassBaseDarkBrush",
            "both log panes moved off the hand-painted glass Border onto material:Card");
    }

    [Fact]
    public void ExactlyTwoSurfaceCards_HostTheLogPanes()
    {
        var doc = XDocument.Parse(Source);
        var surfaceCards = doc.Descendants(XName.Get("Card", "clr-namespace:Material.Styles.Controls;assembly=Material.Styles"))
            .Where(e => (e.Attribute("Classes")?.Value ?? "").Split(' ').Contains("surface"))
            .ToList();

        surfaceCards.Should().HaveCount(2, "the live log list and the service output pane");
    }

    [Fact]
    public void NoLiteralConsolasFontFamily_Remains()
    {
        Source.Should().NotContain("FontFamily=\"Consolas\"",
            "monospace text must route through {StaticResource JetBrainsMono} for Linux parity");
    }

    [Fact]
    public void LogRows_CarryASeverityIcon()
    {
        Source.Should().Contain("LogLevelToIconKindConverter.Instance",
            "each row needs an icon so severity reads without relying on colour");
    }

    [Fact]
    public void TheCtrlCKeyBinding_Survives()
    {
        Source.Should().Contain("Gesture=\"Ctrl+C\"");
        Source.Should().Contain("Command=\"{Binding CopySelectedCommand}\"");
    }

    [Fact]
    public void TheLiveLogListBox_IsHostedDirectlyInsideACard()
    {
        var doc = XDocument.Parse(Source);
        var listBoxes = doc.Descendants(XName.Get("ListBox", Avalonia))
            .Where(e => e.Attribute("ItemsSource")?.Value == "{Binding VisibleEntries}")
            .ToList();

        listBoxes.Should().ContainSingle();
        var parent = listBoxes[0].Parent;
        parent.Should().NotBeNull();
        parent!.Name.LocalName.Should().Be("Card",
            "the ListBox must sit directly in the Card's star row, not wrapped in a ScrollViewer, " +
            "so it virtualizes against a real viewport (RemEx-3oy7x)");
    }

    [Fact]
    public void EntryCountTextAndCopySelected_NoLongerShareAGridColumn()
    {
        var doc = XDocument.Parse(Source);
        var actionBarGrids = doc.Descendants(XName.Get("Grid", Avalonia))
            .Where(e => (e.Attribute("ColumnDefinitions")?.Value ?? "").Contains("Auto,Auto,*,Auto,Auto,Auto,Auto"))
            .ToList();

        actionBarGrids.Should().ContainSingle();
        var grid = actionBarGrids[0];

        var entryCount = grid.Elements(XName.Get("TextBlock", Avalonia))
            .Single(e => e.Attribute("Text")?.Value == "{Binding EntryCountText}");
        var copySelected = grid.Elements(XName.Get("Button", Avalonia))
            .Single(e => e.Attribute("Command")?.Value == "{Binding CopySelectedCommand}");

        entryCount.Attribute("Grid.Column")?.Value.Should().NotBe(copySelected.Attribute("Grid.Column")?.Value);
    }

    [Fact]
    public void ClearLogsButton_UsesTheDangerTintClassRatherThanAForegroundOverride()
    {
        var doc = XDocument.Parse(Source);
        var clearButton = doc.Descendants(XName.Get("Button", Avalonia))
            .Single(e => e.Attribute("Command")?.Value == "{Binding ClearLogsCommand}");

        clearButton.Attribute("Foreground").Should().BeNull(
            "danger tint comes from the Classes vocabulary, not a hard-coded Foreground");
        (clearButton.Attribute("Classes")?.Value ?? "").Split(' ').Should().Contain("danger");
    }

    // ─────────────────── RemEx-a8du: copy variants, follow tail, open folder ───────────────────

    [Fact]
    public void PerRowContextMenu_OffersCopyMessageAndCopyWithStack()
    {
        Source.Should().Contain("CopyEntryMessageCommand");
        Source.Should().Contain("CopyEntryWithStackCommand");
    }

    [Fact]
    public void FollowTailToggle_JumpToNewestChip_AndOpenFolderButton_ArePresent()
    {
        Source.Should().Contain("{Binding IsFollowingTail}");
        Source.Should().Contain("{Binding JumpToNewestCommand}");
        Source.Should().Contain("{Binding OpenExportFolderCommand}");
    }

    /// <summary>
    /// RemEx-df08 owns the sweep of this view's PRE-EXISTING controls, which carry no
    /// AutomationProperties.Name today (RemEx-a8du's bead text is explicit that nothing new should
    /// add to that backlog). So this checks only the controls THIS bead adds, by Command binding
    /// rather than by counting every Button/ToggleButton/MenuItem in the file — the latter would
    /// fail against df08's existing backlog and this bead does not own clearing it.
    /// </summary>
    [Theory]
    [InlineData("CopyEntryMessageCommand")]
    [InlineData("CopyEntryWithStackCommand")]
    [InlineData("JumpToNewestCommand")]
    [InlineData("OpenExportFolderCommand")]
    public void EveryNewInteractiveControl_CarriesAnAutomationName(string commandBinding)
    {
        var doc = XDocument.Parse(Source);
        var element = doc.Descendants()
            .Single(e => e.Attribute("Command")?.Value == $"{{Binding {commandBinding}}}"
                         || (e.Attribute("Command")?.Value ?? "").EndsWith($").{commandBinding}}}", StringComparison.Ordinal));

        // AutomationProperties.Name is an attached property with no separate xmlns declared in this
        // file, so Avalonia/XDocument sees it as a plain attribute in the element's own namespace.
        element.Attribute("AutomationProperties.Name").Should().NotBeNull(
            $"the control bound to {commandBinding} is new in this bead and must carry a localized " +
            "accessible name so RemEx-df08's backlog does not grow");
    }

    [Fact]
    public void FollowTailToggle_CarriesAnAutomationName()
    {
        var doc = XDocument.Parse(Source);
        var toggle = doc.Descendants(XName.Get("ToggleSwitch", Avalonia))
            .Single(e => e.Attribute("IsChecked")?.Value == "{Binding IsFollowingTail}");

        toggle.Attribute("AutomationProperties.Name").Should().NotBeNull();
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, System.StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static string ViewSource()
        => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "DiagnosticLogsView.axaml"));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
