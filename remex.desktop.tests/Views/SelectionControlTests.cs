using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Guards the selection controls after RemEx-x3vom put ToggleSwitch, CheckBox and RadioButton on
/// RemEx's palette, and the <c>:is(Button)</c> correction that came out of the same bead.
/// </summary>
/// <remarks>
/// <para>
/// Both failures here are invisible ones. Material's <c>MaterialToggleSwitch</c> sets
/// <c>SwitchTrackOffBackground</c> to the literal string <c>Black</c>, rendered at 0.26 opacity —
/// over RemEx's dark glass an OFF switch is very nearly the surface itself, so a settings row
/// looks like it is simply missing its control. Nothing throws, nothing logs, and only SolarFlare
/// (light) escapes it.
/// </para>
/// <para>
/// The second is worse because it looks like it works: a bare <c>Button</c> type selector in
/// Avalonia matches the EXACT type, so every vocabulary class silently skipped DropDownButton,
/// SplitButton, RepeatButton and ToggleButton. A control wearing <c>Classes="tertiary"</c> would
/// render completely unstyled with no indication that the class had not matched.
/// </para>
/// </remarks>
public class SelectionControlTests
{
    private const string Avalonia = "https://github.com/avaloniaui";

    private static readonly string[] SwitchAssists =
    {
        "SwitchTrackOnBackground", "SwitchTrackOffBackground",
        "SwitchThumbOnBackground", "SwitchThumbOffBackground",
    };

    [Fact]
    public void TheToggleSwitchTakesEveryTrackAndThumbColourFromTheTheme()
    {
        // ALL FOUR, not just the off track that was the actual defect. Material sets each of them
        // from its own palette, and ThemeService only pushes the seed's PRIMARY and SECONDARY into
        // MaterialTheme.CurrentTheme — so anything left unset drifts from the app's own colours
        // the moment a user picks a seed. Leaving three of four overridden is the state that reads
        // as "the switch is nearly right", which is the hardest kind to notice.
        var setters = SettersOf("ToggleSwitch");

        foreach (var assist in SwitchAssists)
        {
            var value = setters
                .Where(setter => setter.Property.EndsWith("." + assist, StringComparison.Ordinal))
                .Select(setter => setter.Value)
                .SingleOrDefault();

            value.Should().NotBeNull(
                $"ToggleSwitchAssist.{assist} has to be set from App.axaml or it keeps Material's own value");
            value.Should().StartWith("{DynamicResource ",
                $"{assist} has to be a theme token so it follows the palette across all four themes");
        }
    }

    [Fact]
    public void TheOffTrackIsNeverAColourLiteral()
    {
        // THE DEFECT, pinned by name. Material ships SwitchTrackOffBackground="Black" — a literal,
        // not a resource — and the regression that reintroduces it is deleting one setter, not
        // typing a colour. Asserting "not a literal" catches both that and the well-meant
        // "#FF1A1A1A looks about right on my theme" fix.
        var offTrack = SettersOf("ToggleSwitch")
            .Single(setter => setter.Property.EndsWith(".SwitchTrackOffBackground", StringComparison.Ordinal))
            .Value;

        offTrack.Should().MatchRegex(@"^\{(Dynamic|Static)Resource \w+\}$",
            "an OFF switch drawn in Material's literal Black over RemEx's dark glass is a control "
            + "the user cannot see; the off track has to be a theme token");
    }

    [Fact]
    public void TheVocabularySelectorsUseIsButtonSoTheyReachButtonsSubclasses()
    {
        // FOUND BY A REAL MISS. RemEx-z7pnx moved AppLauncherView's DropDownButton onto .tertiary
        // and the build was green, the class was spelled correctly, and the control rendered
        // completely unstyled — Avalonia's bare type selector matches the exact type only.
        // ToggleButton, RepeatButton and SplitButton were in the same position.
        var bare = AppStyles()
            .Select(style => style.Attribute("Selector")?.Value ?? string.Empty)
            .Where(selector => Regex.IsMatch(selector, @"(^|[,\s])Button\.[\w-]+"))
            .ToList();

        bare.Should().BeEmpty(
            "a vocabulary selector written as Button.x matches the exact type and silently skips "
            + "DropDownButton, SplitButton, RepeatButton and ToggleButton; write :is(Button).x");
    }

    // The :checked activator rule that keeps a toggled chip's glyph visible is RemEx-xpfls's, and
    // TrayChipIconVisibilityTests still owns it — it followed the styles from TrayFlyoutWindow to
    // App.axaml rather than being reimplemented here. Two guards for one invariant is how one of
    // them ends up quietly weakened.

    [Fact]
    public void TheKeyboardFocusRingStillReachesEverySelectionControl()
    {
        // THE BEAD'S ACCEPTANCE CRITERION, and it survives for a reason worth recording: all three
        // Material templates root on a Border that template-binds BorderBrush and BorderThickness,
        // so setting them on the control itself draws a ring around the whole control including
        // its label. Material also nulls FocusAdorner, so these ARE the focus indication — losing
        // them leaves keyboard traversal with nothing visible at all, which is exactly the shape
        // RemEx-3e65x flagged.
        var app = File.ReadAllText(AppPath());

        foreach (var control in new[] { "ToggleSwitch", "CheckBox", "RadioButton" })
        {
            app.Should().Contain($"<Style Selector=\"{control}:focus-visible\">",
                $"{control} has no focus adorner under Material, so this style is its only "
                + "keyboard-focus indication");
        }
    }

    // ─────────────────────────── plumbing ───────────────────────────

    private static (string Property, string Value)[] SettersOf(string selector)
        => AppStyles()
            .Where(style => style.Attribute("Selector")?.Value == selector)
            .SelectMany(style => style.Elements(XName.Get("Setter", Avalonia)))
            .Select(setter => (setter.Attribute("Property")?.Value ?? string.Empty,
                               setter.Attribute("Value")?.Value ?? string.Empty))
            .ToArray();

    private static XElement[] AppStyles()
        => XDocument.Parse(File.ReadAllText(AppPath()))
            .Descendants(XName.Get("Style", Avalonia))
            .ToArray();

    private static string AppPath() => Path.Combine(RepoRoot(), "remex.desktop", "App.axaml");

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
