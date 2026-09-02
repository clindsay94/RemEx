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
/// Guards the type vocabulary (RemEx-9iz00.2) documented in <c>docs/TYPOGRAPHY-VOCABULARY.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// A RATCHET, NOT A GATE. The sweep this vocabulary exists for happens in the Phase 4 view beads, one
/// screen at a time, because collapsing the 11/12/13 band changes text density on every screen and
/// there is no headless render to check it against (RemEx-0e9eq). So the useful assertion today is
/// not "there are no inline font sizes" — there are 518 of them — it is "there are no MORE than
/// there were". A ratchet cannot be satisfied by adding a number, only by removing one, and it turns
/// every future view bead into a downward step instead of relying on anyone remembering to sweep.
/// </para>
/// <para>
/// The direction matters and is enforced in BOTH directions on purpose: the count going UP fails
/// because that is the regression, and the count going DOWN far enough also fails, telling whoever
/// did the sweep to lower the baseline in this file. Without the second half the baseline rots into
/// a number nobody trusts, which is how a ratchet quietly becomes decoration.
/// </para>
/// <para>
/// Why the survey on the bead said 435 and this says 518: the survey ran 2026-08-27, the vocabulary
/// landed 2026-08-31, and 83 more inline sizes were added in between — by the Phase 2 shell-chrome
/// beads, which is to say by work that was following the rules as they existed. That growth over
/// four days is the whole argument for pinning the number.
/// </para>
/// <para>
/// Source scan, not a rendering test, for the reason the whole suite is
/// (<see cref="ShellSettingsSideSheetTests"/>): there is no headless Avalonia harness here. This
/// cannot prove any screen looks right. It proves the count is not growing.
/// </para>
/// </remarks>
public class TypographyVocabularyTests
{
    /// <summary>
    /// Inline <c>FontSize="N"</c> occurrences across <c>remex.desktop</c>, measured 2026-09-01 after
    /// the RemEx-a6xnd sweep of <c>SettingsView.axaml</c> (63 of its 64 sites moved onto the Theme
    /// type scale; the one TextBox has no matching ControlTheme and stays inline) landed after the
    /// RemEx-enbqf sweep of <c>RemoteView.axaml</c> (27 sites; its SelectableTextBlock stays
    /// inline). 458 (the RemEx-oszfm baseline) minus both sweeps is 368. Re-pinned to 350 on
    /// 2026-09-02 after the RemEx-ep10v sweep of <c>AboutView.axaml</c> (18 sites; its three
    /// SelectableTextBlocks stay inline). LOWER THIS when a Phase 4 view bead sweeps a screen.
    /// Never raise it.
    /// </summary>
    private const int InlineFontSizeBaseline = 350;

    /// <summary>
    /// How far below the baseline the count may drift before the test asks for the baseline to be
    /// re-pinned. Wide enough that one view's sweep does not have to touch this file mid-review,
    /// tight enough that the number cannot go stale by a hundred.
    /// </summary>
    private const int RatchetSlack = 40;

    [Fact]
    public void InlineFontSizes_DoNotGrow()
    {
        var count = InlineFontSizeCount();

        count.Should().BeLessThanOrEqualTo(InlineFontSizeBaseline,
            $"inline FontSize is what the type vocabulary replaces — there are {InlineFontSizeBaseline} " +
            "left and the number may only go down. Use a Material type ControlTheme instead: " +
            "Theme=\"{StaticResource CaptionTextBlock}\" and friends. The mapping from every current " +
            "size is the table in docs/TYPOGRAPHY-VOCABULARY.md. If the new size genuinely is not " +
            "type — artwork, a control theme's own setter — add it to that file's exceptions list " +
            "and raise this baseline in the same commit, with the reason");

        count.Should().BeGreaterThan(InlineFontSizeBaseline - RatchetSlack,
            $"the count has dropped well below the pinned baseline of {InlineFontSizeBaseline}, which " +
            "means a sweep landed without re-pinning it. Set InlineFontSizeBaseline to the new count " +
            "so the ratchet keeps biting — a baseline far above reality stops catching anything");
    }

    [Fact]
    public void TheDeadTypographyClasses_StayDeleted()
    {
        // All three had zero usages when they were removed. .caption additionally collided by NAME
        // with Material.Avalonia's own Resources/Compatibility/TextBlockClasses.axaml, which defines
        // `:is(Control).caption` as well as `:is(Control).Caption`. RemEx does not merge that
        // dictionary today, so there was no live conflict — but re-adding a TextBlock.caption here
        // re-arms it for the day a Phase 4 sweep does merge it, at which point two definitions match
        // the same class and declaration order decides.
        var app = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "App.axaml"));

        foreach (var dead in new[] { "h1", "h2", "caption" })
        {
            app.Should().NotMatchRegex($@"<Style Selector=""TextBlock\.{dead}""",
                $"TextBlock.{dead} was deleted as unused — see docs/TYPOGRAPHY-VOCABULARY.md. " +
                "Material's type ControlThemes cover this role");
        }
    }

    [Fact]
    public void TheSurvivingClasses_KeepWhatTheThemeCannotSupply()
    {
        // page-title and page-subtitle are NOT retired by this bead precisely because they carry the
        // live font-family binding. CustomizationViewModel's picker reaches text through
        // PageTitleFontFamily; a Material type ControlTheme sets size and weight only. Anyone
        // "finishing the migration" by swapping these for Headline5TextBlock and deleting the class
        // silently breaks the page-title half of the font picker, and nothing else in this suite
        // would notice.
        var app = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "App.axaml"));

        foreach (var cls in new[] { "page-title", "page-subtitle" })
        {
            var style = Regex.Match(app,
                $@"<Style Selector=""TextBlock\.{cls}"">(?<body>.*?)</Style>",
                RegexOptions.Singleline);

            style.Success.Should().BeTrue($"TextBlock.{cls} is still in use and must still be declared");
            style.Groups["body"].Value.Should().Contain("PageTitleFontFamily",
                $"{cls} exists to carry the live font-family binding that a Material type " +
                "ControlTheme cannot — dropping it breaks the font picker on this text");
        }
    }

    private static int InlineFontSizeCount()
    {
        var desktop = Path.Combine(RepoRoot(), "remex.desktop");
        var pattern = new Regex(@"FontSize=""\d", RegexOptions.Compiled);

        return Directory
            .EnumerateFiles(desktop, "*.axaml", SearchOption.AllDirectories)
            .Sum(f => pattern.Matches(File.ReadAllText(f)).Count);
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
