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
