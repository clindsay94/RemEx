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
/// Guards the progress indicators after RemEx-rcgmc put them on one rule.
/// </summary>
/// <remarks>
/// <para>
/// The rule the bead cared about most is not a colour: <b>determinate where a real percentage
/// exists, indeterminate only where it genuinely does not.</b> A determinate bar is a claim about
/// how far along something is, and a bar wired to a value nobody updates makes that claim falsely —
/// it sits at 0% while the work succeeds, or at 60% forever. Neither throws.
/// </para>
/// <para>
/// The inverse is just as bad and much easier to write by accident: a bar with neither a bound
/// <c>Value</c> nor <c>IsIndeterminate</c> renders as a permanently empty track, which reads as
/// "nothing is happening" at exactly the moment something is.
/// </para>
/// </remarks>
public class ProgressIndicatorTests
{
    [Fact]
    public void EveryProgressBarIsEitherDeterminateOrDeliberatelyIndeterminate()
    {
        // Both halves matter. A bar with a Value AND IsIndeterminate="True" ignores the value it
        // was given, which is the shape where someone wires up real progress and never sees it.
        var offenders = new List<string>();

        foreach (var (file, bar) in ProgressBars())
        {
            var hasValue = Regex.IsMatch(bar, @"\bValue=""\{Binding\b");
            var indeterminate = Regex.IsMatch(bar, @"\bIsIndeterminate=""True""");

            if (hasValue == indeterminate)
            {
                offenders.Add($"{Path.GetFileName(file)}: {Summarise(bar)}");
            }
        }

        offenders.Should().BeEmpty(
            "a progress bar has to bind a real Value or declare itself indeterminate — neither "
            + "leaves an empty track that reads as 'nothing is happening', and both makes the "
            + "bound value inert");
    }

    [Fact]
    public void TheTransferProgressBarIsDeterminate()
    {
        // NAMED, because it is the one the bead's acceptance criterion is about and the one a user
        // watches. Every other bar could be right and this one wrong, and the file-list screen is
        // where "is it stuck?" actually costs someone something.
        var transfer = ProgressBars()
            .Where(pair => Path.GetFileName(pair.File) == "FileTransferView.axaml")
            .Select(pair => pair.Markup)
            .ToList();

        transfer.Should().NotBeEmpty("FileTransferView draws the per-transfer progress");

        transfer.Should().Contain(bar => Regex.IsMatch(bar, @"\bValue=""\{Binding Progress\}"""),
            "the transfer bar reports a real percentage; the host sends it, so an indeterminate "
            + "bar here would be discarding information the app already has");
    }

    [Fact]
    public void EveryIndeterminateIndicatorHasAnAccessibleName()
    {
        // An indeterminate ring is pure visual: no text, no value, nothing for a screen reader to
        // announce. Without a name it is invisible to anyone not looking at it, which is the
        // population most affected by "is this screen busy or broken?".
        var unnamed = ProgressBars()
            .Where(pair => Regex.IsMatch(pair.Markup, @"\bIsIndeterminate=""True"""))
            .Where(pair => !pair.Markup.Contains("AutomationProperties.Name", StringComparison.Ordinal))
            .Select(pair => $"{Path.GetFileName(pair.File)}: {Summarise(pair.Markup)}")
            .ToList();

        unnamed.Should().BeEmpty(
            "an indeterminate indicator announces nothing on its own; it needs an accessible name");
    }

    [Fact]
    public void NoViewPaintsAProgressBarItself()
    {
        // The state this replaced: HomeView's two stat bars carried their own Foreground and
        // Background, FileTransferView's and PairingDialog's did not, so the same control wore
        // Material's primary-light track on two screens and RemEx's glass on another.
        var offenders = ProgressBars()
            .Where(pair => Regex.IsMatch(pair.Markup, @"\b(Foreground|Background)="))
            .Select(pair => $"{Path.GetFileName(pair.File)}: {Summarise(pair.Markup)}")
            .ToList();

        offenders.Should().BeEmpty(
            "progress colour is one rule in App.axaml; a per-view override is how the same control "
            + "ended up with two colour schemes");
    }

    [Fact]
    public void TheCircularClassActuallySwapsTheTemplate()
    {
        // ANTI-VACUITY, and the failure is specific rather than theoretical. Material implements
        // circular progress as a different ControlTheme, not as a variant of the linear template,
        // so .circular is only meaningful while it sets Theme. Lose that setter and every ring in
        // the app becomes a 22-pixel-wide linear bar — visible, wrong, and not obviously a bug.
        var app = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "App.axaml"));

        var style = Regex.Match(app, @"<Style Selector=""ProgressBar\.circular"">.*?</Style>",
            RegexOptions.Singleline);

        style.Success.Should().BeTrue("the .circular class has to exist for the views that use it");
        style.Value.Should().MatchRegex(
            @"<Setter Property=""Theme"" Value=""\{DynamicResource MaterialCircularProgressBar\}""/>",
            "circular progress is a template swap; without the Theme setter the class only "
            + "resizes a linear bar");
    }

    // ─────────────────────────── plumbing ───────────────────────────

    private static (string File, string Markup)[] ProgressBars()
    {
        var bars = Directory
            .EnumerateFiles(Path.Combine(RepoRoot(), "remex.desktop"), "*.axaml", SearchOption.AllDirectories)
            .SelectMany(file => Regex
                .Matches(File.ReadAllText(file), @"<ProgressBar\b[^>]*?/?>", RegexOptions.Singleline)
                .Select(match => (File: file, Markup: match.Value)))
            .ToArray();

        bars.Should().NotBeEmpty(
            "if this finds nothing every assertion above is vacuous — the element or the scan moved");
        return bars;
    }

    private static string Summarise(string markup)
    {
        var collapsed = Regex.Replace(markup, @"\s+", " ").Trim();
        return collapsed.Length > 110 ? collapsed[..110] + "…" : collapsed;
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
