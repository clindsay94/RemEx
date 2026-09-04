using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Guards the Material outline notch fix (RemEx-x6a70.4): Material.Avalonia only clips the outline
/// border behind a floated label when the TextBox carries BOTH the "outline" class AND
/// <c>UseFloatingPlaceholder="True"</c> (Material.Styles/Resources/Themes/TextBox.axaml, tag 3.19.0,
/// selector <c>TextBox.outline[UseFloatingPlaceholder=True] /template/ Border#PART_BackgroundTextField</c>,
/// lines 184-195). App.axaml's shared <c>TextBox</c> style applies the outline theme but deliberately
/// does not set either, so every labelled call site must set them itself.
/// </summary>
/// <remarks>
/// A SOURCE-TEXT TEST for the usual reason in this folder: no headless render in this suite, so a
/// TextBox that carries a floating label but not the notch keys compiles and renders a border
/// straight through the label text, silently, with no test failure pointing back at the cause.
/// </remarks>
public class FloatingLabelNotchTests
{
    private const string AvaloniaNs = "https://github.com/avaloniaui";

    [Fact]
    public void EveryLabelledTextBoxCarriesTheOutlineClassAndUsesFloatingPlaceholder()
    {
        var viewsDir = Path.Combine(RepoRoot(), "remex.desktop", "Views");
        var offenders = new System.Collections.Generic.List<string>();
        var labelledCount = 0;

        foreach (var path in Directory.EnumerateFiles(viewsDir, "*.axaml", SearchOption.AllDirectories))
        {
            var doc = XDocument.Load(path);
            var root = doc.Root;
            if (root is null)
            {
                continue;
            }

            var assistsNs = root.GetNamespaceOfPrefix("assists");
            if (assistsNs is null)
            {
                continue;
            }

            var labelAttrName = assistsNs + "TextFieldAssist.Label";

            foreach (var textBox in doc.Descendants(XName.Get("TextBox", AvaloniaNs)))
            {
                if (textBox.Attribute(labelAttrName) is null)
                {
                    continue;
                }

                labelledCount++;

                var classes = (textBox.Attribute("Classes")?.Value ?? string.Empty)
                    .Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                var hasOutlineClass = classes.Contains("outline");
                var usesFloatingPlaceholder = textBox.Attribute("UseFloatingPlaceholder")?.Value == "True";

                if (!hasOutlineClass || !usesFloatingPlaceholder)
                {
                    offenders.Add(
                        $"{Path.GetFileName(path)}: {(hasOutlineClass ? "" : "missing Classes=\"outline\" ")}" +
                        $"{(usesFloatingPlaceholder ? "" : "missing UseFloatingPlaceholder=\"True\"")}".Trim());
                }
            }
        }

        // Anti-vacuity: this fix touches four known call sites (AddProgramWindow x3,
        // DiagnosticLogsView x1). If the count drops below that, the scan itself is broken -
        // labels moved, TextBoxes were deleted, or the assists prefix stopped resolving - and a
        // green "no offenders" result would be a false pass.
        labelledCount.Should().BeGreaterThanOrEqualTo(4,
            "at least four labelled TextBoxes are known to exist across Views/*.axaml; " +
            "fewer than that means the scan did not run, not that the fix regressed");

        offenders.Should().BeEmpty(
            "a TextBox with TextFieldAssist.Label needs both Classes=\"outline\" and " +
            "UseFloatingPlaceholder=\"True\" for Material's notch clip selector to match");
    }

    [Fact]
    public void SharedTextBoxStyleDoesNotSetUseFloatingPlaceholderAppWide()
    {
        // Pins the deliberate decision (App.axaml comment above the TextBox style, RemEx-x6a70.4):
        // setting UseFloatingPlaceholder globally would re-margin PART_TextContainer on every
        // unlabelled field too (upstream TextBox.axaml:411), so each labelled call site opts in
        // for itself instead.
        var app = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "App.axaml"));

        var style = Regex.Match(app, @"<Style Selector=""TextBox"">.*?</Style>", RegexOptions.Singleline);

        style.Success.Should().BeTrue("App.axaml has to carry the shared TextBox rule");
        style.Value.Should().NotContain("UseFloatingPlaceholder",
            "the app-wide TextBox style must not set UseFloatingPlaceholder; it is opted into per labelled call site");
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
