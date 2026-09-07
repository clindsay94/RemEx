using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Guards the one text-input rule established by RemEx-5w9ws.
/// </summary>
/// <remarks>
/// <para>
/// The state this replaced was not broken markup, which is why nothing caught it: Material's
/// default <c>MaterialTextBox</c> is the UNDERLINE field — no border, no fill — but its template
/// does bind <c>Background</c>, <c>BorderBrush</c>, <c>BorderThickness</c> and <c>CornerRadius</c>
/// through <c>PART_RootBorder</c>. So a call site could paint itself a box, and 36 of the app's 52
/// inputs did, each with its own copy of the same attributes. The other 16 did not. The same
/// control was a bordered rounded box on one screen and a transparent underline field on the next,
/// and every one of them was individually reasonable.
/// </para>
/// <para>
/// Connor chose the outline field (2026-08-27). The guard is that no view goes back to painting
/// its own.
/// </para>
/// </remarks>
public class TextInputTests
{
    private static readonly string[] InputTypes = { "TextBox", "ComboBox", "NumericUpDown", "AutoCompleteBox" };

    /// <summary>Properties whose per-call-site copies the single rule replaced.</summary>
    private static readonly string[] SurfaceProperties =
        { "Background", "BorderBrush", "BorderThickness", "CornerRadius", "Padding" };

    /// <summary>
    /// Inputs that deliberately have no <c>TextFieldAssist</c>/<c>ComboBoxAssist</c> Label or
    /// Hints (RemEx-5w9ws.1). Matched by a substring unique to the tag rather than a line number,
    /// so an unrelated edit above the field doesn't silently widen or break the allow-list. Each
    /// entry names the reason a floating label is wrong here, not merely missing.
    /// </summary>
    private static readonly Dictionary<string, string> LabelExemptInputs = new()
    {
        // Search/filter fields: the bead's own exception (Btn_Discover's caption is the label,
        // a filter box's placeholder is the label - "Search" floating above an empty filter box
        // says less than the placeholder text it would replace).
        ["Binding SearchQuery"] = "FileTransferView - search field, placeholder is the affordance",
        ["Binding WindowSearchText"] = "RemoteDesktopView - search field, placeholder is the affordance",
        ["x:Name=\"SearchBox\""] = "SecondMetricDialog - search field, placeholder is the affordance",
        ["Binding SearchText}\" PlaceholderText=\"{local:Localize TaskMgr_SearchPlaceholder"] =
            "TaskManagerView - search field, placeholder is the affordance",
        ["AppLauncher_SearchWatermark"] = "AppLauncherView - search field, placeholder is the affordance",

        // Dense single-row toolbars: a floating label needs vertical headroom above the field that
        // a fixed-height horizontal control bar does not have. Each of these keeps the caption
        // TextBlock beside it instead (unlike the vertical settings-row fields this bead converted).
        ["Binding RemoteRoots"] = "FileTransferView - toolbar row (Row 0: root selector + search + volumes)",
        ["Binding AvailableDisplayTargets"] = "RemoteDesktopView - toolbar row (display picker)",
        ["Binding SelectedScaleIndex"] = "RemoteDesktopView - toolbar row (scale picker)",
        ["Binding Connection.HostAddress"] = "RemoteDesktopView - toolbar row (connection panel host field)",

        // Read-only display, not data entry: a floating label asks "what do I type here", which is
        // the wrong question for a terminal-style output pane the user never types into.
        ["Binding ServiceLogsText"] = "DiagnosticLogsView - read-only log viewer, not a data-entry field",

        // Inline edit-in-place overlay: the sensor's own title is already visible on the card this
        // overlays, so a floating "Title" label would repeat information rather than add it.
        ["Binding Sensor.CustomTitle"] = "CanvasView - inline rename overlay of a title already on the card",

        // NumericUpDown, not TextBox/ComboBox (Opus review round 2, MEDIUM): confirmed against
        // Material.Avalonia 3.19.0's actual source (Material.Styles/Resources/Themes/
        // NumericUpDown.axaml) that OutlineNumericUpDown template-binds TextFieldAssist.Label
        // down to an inner PART_TextBox themed MaterialOnlyPresenterTextBox - a stripped template
        // with no label element - while the outer template's own styles target
        // Border#PART_LabelRootBorder, an element the outer template never declares either. The
        // value is bound through but never painted; a caption TextBlock stays for this one field.
        ["SetAlert_ThresholdValue"] = "SetAlertDialog - OutlineNumericUpDown never renders TextFieldAssist.Label in Material.Avalonia 3.19.0; a caption TextBlock stays",
    };

    [Fact]
    public void NoViewPaintsAnInputItself()
    {
        var offenders = new List<string>();

        foreach (var (file, tag, kind) in Inputs())
        {
            foreach (var property in SurfaceProperties)
            {
                if (Regex.IsMatch(tag, $@"\b{property}="""))
                {
                    offenders.Add($"{Path.GetFileName(file)}: <{kind} … {property}=…>");
                }
            }
        }

        offenders.Distinct().Should().BeEmpty(
            "the input surface is one rule in App.axaml; a per-call-site copy is how 36 of them "
            + "each grew their own and the other 16 stayed on Material's underline field");
    }

    [Fact]
    public void PaddingIsNeverSetOnATextBoxBecauseItDoesNothing()
    {
        // A SEPARATE ASSERTION FROM THE ONE ABOVE, because the reason differs and reasons are what
        // stop a rule being widened later. Background and friends were removed for consistency —
        // they DID apply. Padding was removed because MaterialTextBox's template has no Padding
        // TemplateBinding at all, so every Padding on a TextBox in this app was decoration with no
        // effect. Someone re-adding it would see nothing happen and reasonably conclude the value
        // was too small.
        var offenders = Inputs()
            .Where(input => input.Kind == "TextBox")
            .Where(input => Regex.IsMatch(input.Tag, @"\bPadding="""))
            .Select(input => Path.GetFileName(input.File))
            .Distinct()
            .ToList();

        offenders.Should().BeEmpty(
            "Padding on a TextBox is inert under Material's template — spacing inside the field "
            + "belongs to the theme");
    }

    [Fact]
    public void EveryInputTypeAdoptsAnOutlineTheme()
    {
        // ANTI-VACUITY for the test above: "no view paints an input" is also satisfied by nobody
        // styling inputs anywhere, which would leave every field on Material's transparent
        // underline default — the look Connor did not pick.
        var app = File.ReadAllText(AppPath());

        var expected = new Dictionary<string, string>
        {
            ["TextBox"] = "OutlineTextBox",
            ["ComboBox"] = "MaterialOutlineComboBox",
            ["NumericUpDown"] = "OutlineNumericUpDown",
        };

        foreach (var (control, theme) in expected)
        {
            var style = Regex.Match(app, $@"<Style Selector=""{control}"">.*?</Style>", RegexOptions.Singleline);

            style.Success.Should().BeTrue($"App.axaml has to carry the shared {control} rule");
            style.Value.Should().Contain($"{{DynamicResource {theme}}}",
                $"{control} takes Material's outline variant; without the Theme setter it stays on "
                + "the underline default and the per-view paint that used to hide that is gone");
        }
    }

    [Fact]
    public void NoStyleTargetsAFluentTemplatePart()
    {
        // THE DEAD-SELECTOR CLASS OF BUG, and this one had already happened: SettingsView styled
        // "TextBox.modern:focus /template/ Border#PART_BorderElement". PART_BorderElement is
        // FLUENT's part name. Material's TextBox has no element by that name, so that focus
        // highlight stopped rendering the day RemEx-prkot removed Fluent, with no error anywhere —
        // an Avalonia selector that matches nothing simply never applies.
        var fluentParts = new[] { "PART_BorderElement", "PART_ContentPresenterBorder", "PART_LayoutRoot" };

        var offenders = XamlFiles()
            .SelectMany(pair => Regex
                .Matches(pair.Text, @"Selector=""([^""]*)""")
                .Select(match => (File: pair.File, Selector: match.Groups[1].Value)))
            .Where(row => fluentParts.Any(part => row.Selector.Contains(part, StringComparison.Ordinal)))
            .Select(row => $"{Path.GetFileName(row.File)}: {row.Selector}")
            .ToList();

        offenders.Should().BeEmpty(
            "these are Fluent's template part names; Material has no elements by them, so a "
            + "selector naming one is inert and looks like working code");
    }

    [Fact]
    public void ThePairingErrorIsAttachedToTheFieldRatherThanFloatingBelowIt()
    {
        // The bead's acceptance criterion, at the one place in the app with inline field
        // validation. The message used to be a red TextBlock underneath the box: correct
        // information, visually unattached to the input it described, and announced by nothing.
        // TextFieldAssist.Hints renders it inside the field's own template, so it moves with the
        // field and belongs to it.
        var dialog = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "PairingDialog.axaml"));

        dialog.Should().Contain(@"assists:TextFieldAssist.Hints=""{Binding ErrorText}""",
            "the PIN error belongs to the field, not to a TextBlock near it");

        dialog.Should().Contain("Classes.invalid=",
            "the field also has to CHANGE, not just gain text — border plus message is two "
            + "channels, which is what 'not colour alone' means here");

        Regex.Matches(dialog, @"<TextBlock[^>]*Text=""\{Binding ErrorText\}""").Should().BeEmpty(
            "the floating error TextBlock is what this replaced; two copies of the message would "
            + "be worse than either");
    }

    [Fact]
    public void EveryInputCarriesALabelOrIsAListedException()
    {
        // THE ACCEPTANCE CRITERION RemEx-5w9ws.1 WAS FILED FOR: every TextBox/ComboBox/
        // NumericUpDown either floats its own Label (or Hints), or is one of the LabelExemptInputs
        // above with a reason attached. An unlisted bare field is exactly the state 34 inputs were
        // in before this bead - a per-screen judgement nobody had made yet, not a rule anyone was
        // deliberately breaking.
        var offenders = new List<string>();
        var exemptedSeen = new HashSet<string>();

        foreach (var (file, tag, kind) in Inputs())
        {
            if (IsPropertyElementTag(tag, kind))
            {
                continue;
            }

            var hasAssist = Regex.IsMatch(
                tag, @"(TextFieldAssist\.(Label|Hints)|ComboBoxAssist\.(Label|Hints))\s*=");
            if (hasAssist)
            {
                continue;
            }

            var exemption = LabelExemptInputs.Keys.FirstOrDefault(key => tag.Contains(key));
            if (exemption is not null)
            {
                exemptedSeen.Add(exemption);
                continue;
            }

            offenders.Add($"{Path.GetFileName(file)}: <{kind} …> has no Label/Hints and matches no listed exception");
        }

        // ANTI-VACUITY: every listed exception has to actually match something, or the list is
        // quietly protecting nothing (a rename, a deleted field) while still looking exhaustive.
        var unmatchedExceptions = LabelExemptInputs.Keys.Except(exemptedSeen).ToList();
        unmatchedExceptions.Should().BeEmpty(
            "every LabelExemptInputs entry has to match a real field, or the allow-list is stale: "
            + string.Join(", ", unmatchedExceptions));

        offenders.Should().BeEmpty(
            "every input needs a floating Label/Hints, or has to be added to LabelExemptInputs "
            + "with a reason - a search field's placeholder or a toolbar row's inline caption, not "
            + "silence");
    }

    [Fact]
    public void EveryInputAnnouncesWhatItEdits()
    {
        // THE ACCEPTANCE CRITERION RemEx-5w9ws.2 WAS FILED FOR. Avalonia's TextBox automation peer
        // does not fall back to Watermark/PlaceholderText, and RemEx-5w9ws.1's own TextFieldAssist
        // Label does not become the UIA Name either - verified live with ui-snapshot -Tree against
        // SettingsView's labelled "PC Address" field, which announced an empty name. So every input
        // needs an explicit AutomationProperties.Name; there is no floating-label shortcut around it.
        //
        // NO EXEMPTIONS, unlike the fact above: a search field still has to say "search", a toolbar
        // combo still has to say what it pickers, and a read-only log viewer still has to say what
        // it is showing. The reasons that let a field skip a floating LABEL (no vertical room, the
        // placeholder already reads fine visually) do not apply to whether it announces anything to
        // a screen reader at all.
        var offenders = Inputs()
            .Where(input => !IsPropertyElementTag(input.Tag, input.Kind))
            .Where(input => !Regex.IsMatch(input.Tag, @"AutomationProperties\.Name\s*="))
            .Select(input => $"{Path.GetFileName(input.File)}: <{input.Kind} …>")
            .Distinct()
            .ToList();

        offenders.Should().BeEmpty(
            "every TextBox/ComboBox/NumericUpDown needs AutomationProperties.Name so a screen "
            + "reader announces what it edits, not just that it is an edit control");
    }

    /// <summary>
    /// True for a property-element tag such as <c>&lt;ComboBox.ItemTemplate&gt;</c>. Inputs()'s
    /// pattern stops at the first '&gt;', which that tag also satisfies because '.' is a word
    /// boundary - a real ComboBox with an ItemTemplate produces two matches, only one of which is
    /// an actual input. The Background/BorderBrush facts above never noticed because a
    /// property-element tag never carries those; a Label/Name presence check would flag every one
    /// of them as an offender without this filter.
    /// </summary>
    private static bool IsPropertyElementTag(string tag, string kind)
        => Regex.IsMatch(tag, $@"^<{Regex.Escape(kind)}\.");

    // ─────────────────────────── plumbing ───────────────────────────

    private static (string File, string Tag, string Kind)[] Inputs()
    {
        var pattern = $@"<({string.Join("|", InputTypes)})\b[^>]*?/?>";

        var inputs = XamlFiles()
            .SelectMany(pair => Regex
                .Matches(pair.Text, pattern, RegexOptions.Singleline)
                .Select(match => (pair.File, Tag: match.Value, Kind: match.Groups[1].Value)))
            .ToArray();

        inputs.Should().NotBeEmpty(
            "if this finds nothing every assertion above is vacuous — the scan or the views moved");
        return inputs;
    }

    private static (string File, string Text)[] XamlFiles()
        => Directory
            .EnumerateFiles(Path.Combine(RepoRoot(), "remex.desktop"), "*.axaml", SearchOption.AllDirectories)
            .Where(file => Path.GetFileName(file) != "App.axaml")
            .Select(file => (file, File.ReadAllText(file)))
            .ToArray();

    private static string AppPath() => Path.Combine(RepoRoot(), "remex.desktop", "App.axaml");

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
