using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Structural guards over the PC Routines page (routines spec §2.3, §13.7): no editing (R-UX-35), names
/// and button classes (R-UX-47), keyboard order and gestures (R-UX-48), not colour alone (R-UX-50),
/// reduced motion (R-UX-52), no colour literals (R-UX-53), destructive tint (R-UX-54), Pause all (R-UX-21),
/// and the fail-closed confirmation (R-UX-36). remex.desktop.tests has no headless render, so these read
/// the markup; RoutinesViewRenderTests renders it.
/// </summary>
public sealed class RoutinesViewTests
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";

    private static readonly string[] RoutinesSources =
    [
        Path.Combine("remex.desktop", "Views", "RoutinesView.axaml"),
        Path.Combine("remex.desktop", "Views", "RoutinesView.axaml.cs"),
        Path.Combine("remex.desktop", "ViewModels", "RoutinesViewModel.cs"),
        Path.Combine("remex.desktop", "ViewModels", "RoutineItemViewModels.cs"),
        Path.Combine("remex.desktop", "Services", "Routines", "RoutinePresentation.cs"),
    ];

    [Fact]
    public void NoEditingControls()
    {
        var xaml = Doc();
        xaml.Descendants().Where(e => e.Name.LocalName is "TextBox" or "AutoCompleteBox" or "NumericUpDown" or "ComboBox")
            .Should().BeEmpty("the PC never edits a routine (D8, R-UX-35)");

        var commands = Regex.Matches(Markup(), @"Command=""\{Binding (\w+)\}""").Select(m => m.Groups[1].Value).Distinct().ToList();
        commands.Should().NotContain(c => Regex.IsMatch(c, "Add|Edit|Delete|Rename|Duplicate|Create"),
            "the only actions are enable, Run now, Pause all, block, cancel and history");

        Markup().Should().Contain("{local:Localize Routines_Subheader}", "the header says the phone is the editor");
    }

    [Fact]
    public void EveryButtonAndSwitchHasAnAutomationNameAndEveryButtonAVocabularyClass()
    {
        var controls = Doc().Descendants().Where(e => e.Name.LocalName is "Button" or "ToggleSwitch").ToList();
        controls.Should().NotBeEmpty();
        foreach (var control in controls)
        {
            (control.Attribute("AutomationProperties.Name")?.Value).Should().NotBeNullOrWhiteSpace(
                $"{control.Name.LocalName} {control.Attribute("Content")?.Value} needs a UIA name (R-UX-47)");
        }

        foreach (var button in controls.Where(e => e.Name.LocalName == "Button"))
        {
            (button.Attribute("Classes")?.Value ?? "").Split(' ')
                .Count(c => c == "primary" || c == "secondary" || c == "tertiary")
                .Should().Be(1, "every button carries exactly one emphasis from docs/BUTTON-VOCABULARY.md");
        }
    }

    [Fact]
    public void TabOrderIsPauseAllThenTheListThenTheDetail()
    {
        var names = Doc().Descendants()
            .Select(NameOf)
            .Where(n => n is not null)
            .ToList();

        var pause = names.IndexOf("PauseAllSwitch");
        var list = names.IndexOf("RoutineGroups");
        var detail = names.IndexOf("DetailPane");
        var replay = names.IndexOf("CoachReplayButton");

        pause.Should().BeGreaterThan(-1);
        pause.Should().BeLessThan(list);
        list.Should().BeLessThan(detail);
        detail.Should().BeLessThan(replay, "the ? button paints in the header but is declared last for tab order (R-UX-48)");
    }

    [Fact]
    public void TheListTakesSpaceToToggleAndOpensTheContextMenuFromTheKeyboard()
    {
        var code = CodeBehind();
        code.Should().Contain("AddHandler(KeyDownEvent, OnListKeyDown, RoutingStrategies.Tunnel)");
        code.Should().Contain("e.Key != Key.Space");
        code.Should().Contain("card.ToggleEnabledCommand.Execute(null)");
        code.Should().Contain("AddHandler(ContextRequestedEvent, OnContextRequested");
        code.Should().Contain("Routines_Menu_TurnOff").And.Contain("Routines_Menu_History").And.Contain("Routines_RunNow");
        Doc().Descendants(Avalonia + "ListBox").Should().OnlyContain(l => l.Attribute("SelectionMode")!.Value == "Single");
    }

    [Fact]
    public void RunNowConfirmsThroughConfirmationDialogHostWhichDeclinesWithoutAWindow()
    {
        var code = CodeBehind();
        code.Should().Contain("ConfirmationDialogHost.ForTinted(this)");

        var host = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "ConfirmationDialogHost.cs"));
        host.Should().Contain("is not Window { IsVisible: true } parentWindow");
        host.Should().MatchRegex(@"declining the destructive action[\s\S]{0,80}return false;");
    }

    [Fact]
    public void PauseAllIsASwitchWithABannerAndAResume()
    {
        var doc = Doc();
        var toggle = doc.Descendants(Avalonia + "ToggleSwitch").Single(e => NameOf(e) == "PauseAllSwitch");
        (toggle.Attribute("IsChecked")?.Value).Should().Be("{Binding HostPaused}");

        var banner = doc.Descendants(Avalonia + "Border").Single(e => NameOf(e) == "PausedBanner");
        (banner.Attribute("Classes.shown")?.Value).Should().Be("{Binding HostPaused}");
        banner.Descendants(Avalonia + "Button").Single().Attribute("Command")!.Value.Should().Be("{Binding ResumeAllCommand}");
    }

    [Fact]
    public void TheDryRunBannerIsBoundToTheDryRunFlag()
    {
        var banner = Doc().Descendants(Avalonia + "Border").Single(e => NameOf(e) == "DryRunBanner");
        (banner.Attribute("IsVisible")?.Value).Should().Be("{Binding IsDryRun}");
        banner.ToString().Should().Contain("Routine_Countdown_DryRunNote");
    }

    [Fact]
    public void OutcomesShowAnIconAndTextNeverColourAlone()
    {
        var statusIcons = Doc().Descendants().Where(e => e.Name.LocalName == "MaterialIcon"
            && (e.Attribute("Classes")?.Value ?? "").Split(' ').Contains("status")).ToList();
        statusIcons.Should().HaveCountGreaterThanOrEqualTo(3);
        statusIcons.Should().OnlyContain(i => i.Attribute("Kind")!.Value.StartsWith("{Binding"),
            "every outcome has its own glyph, not only a colour");

        // Each icon sits beside its text in the same row.
        foreach (var icon in statusIcons)
        {
            icon.Parent!.Descendants(Avalonia + "TextBlock").Should().NotBeEmpty();
        }
    }

    [Fact]
    public void ADiscardsWorkChipTakesTheErrorContainerRole()
    {
        var markup = Markup();
        markup.Should().MatchRegex(@"Selector=""Border\.chip\.destructive"">\s*<Setter Property=""Background"" Value=""\{DynamicResource PaletteErrorContainerBrush\}""");
        markup.Should().MatchRegex(@"Selector=""Border\.chip\.destructive > TextBlock"">\s*<Setter Property=""Foreground"" Value=""\{DynamicResource PaletteOnErrorContainerBrush\}""");
        markup.Should().Contain("Classes.destructive=\"{Binding IsDestructive}\"");
    }

    [Fact]
    public void RoutinesReducedMotionScan()
    {
        var markup = Markup();

        // Every Transitions setter lives under a style gated on the root's reduced-motion class.
        foreach (Match style in Regex.Matches(markup, @"<Style Selector=""([^""]+)"">(?:(?!</Style>)[\s\S])*?<Setter Property=""Transitions"">"))
        {
            style.Groups[1].Value.Should().StartWith("Grid#RoutinesRoot:not(.reduced-motion) ",
                "every routines transition is class-gated on IsReducedMotion (R-UX-52)");
        }

        // Every keyframe animation is the entrance, which only plays when StaggeredEntrance says so.
        foreach (Match style in Regex.Matches(markup, @"<Style Selector=""([^""]+)"">\s*<Style.Animations>"))
        {
            style.Groups[1].Value.Should().Contain(".entrance");
        }

        markup.Should().Contain("Classes.reduced-motion=\"{Binding IsReducedMotion}\"");
        markup.Should().NotContain("Property=\"RenderTransform\"", "RemEx-qolhg: animate TranslateTransform.Y, never RenderTransform");

        var code = CodeBehind();
        code.Should().Contain("StaggeredEntrance.ShouldPlay(nameof(RoutinesView), vm.IsReducedMotion)");
        code.Should().MatchRegex(@"PageTransition = reducedMotion\s*\?\s*null");
    }

    [Fact]
    public void RoutinesColorLiteralScan()
    {
        foreach (var relative in RoutinesSources)
        {
            var text = File.ReadAllText(Path.Combine(RepoRoot(), relative));
            text.Should().NotMatchRegex(@"#[0-9A-Fa-f]{6,8}\b", $"{relative}: roles only, no hex colours (R-UX-53)");
            text.Should().NotMatchRegex(@"Color\.(FromArgb|FromRgb|Parse)|Colors\.\w+|new SolidColorBrush", $"{relative}: roles only");
        }
    }

    [Fact]
    public void RoutinesNoConcatScan()
    {
        // Sentences come from format strings (RoutineStrings.Format), never from gluing localized
        // pieces together with + (R-UX-56).
        foreach (var relative in RoutinesSources.Where(p => p.EndsWith(".cs")))
        {
            var text = File.ReadAllText(Path.Combine(RepoRoot(), relative));
            text.Should().NotMatchRegex(@"Instance\[""[^""]+""\]\s*\+|\+\s*LocalizationService\.Instance\[",
                $"{relative}: build sentences with a format string");
            text.Should().NotMatchRegex(@"loc\[""[^""]+""\]\s*\+\s*""|\+\s*loc\[", $"{relative}: build sentences with a format string");
        }
    }

    [Fact]
    public void TheNarrowLayoutExpandsTheSelectedCardInlineWithTheSameDetailTemplate()
    {
        var markup = Markup();
        Regex.Matches(markup, @"ContentTemplate=""\{StaticResource RoutineDetailTemplate\}""").Count.Should().Be(2,
            "the wide pane and the narrow inline detail share one template (P1, P2)");
        CodeBehind().Should().Contain("internal const double WideBreakpoint = 900;");
    }

    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string? NameOf(XElement e) => e.Attribute(Xaml + "Name")?.Value ?? e.Attribute("Name")?.Value;

    private static XDocument Doc() => XDocument.Parse(Markup());

    private static string Markup() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "RoutinesView.axaml"));

    private static string CodeBehind() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "RoutinesView.axaml.cs"));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
