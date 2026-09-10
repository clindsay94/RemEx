using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Remex.Core.Services.Readiness;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests;

/// <summary>
/// Pins the System status card's keys, its affordances, and its ordering (RemEx-id37).
/// </summary>
/// <remarks>
/// <para>
/// **THE KEY TESTS ENUMERATE THE ENUMS, THEY DO NOT LIST THE KEYS.** A test that repeated the thirty
/// key names by hand would need updating in step with two enums, and would pass while a newly added
/// check rendered blank rows — which is the silent-gap failure this card exists to remove. Adding a
/// value to <see cref="ReadinessCheckId"/> now fails these tests until all nine languages have a
/// sentence for it.
/// </para>
/// <para>
/// A MISSING KEY IS SILENT AT RUNTIME. <c>LocalizationService</c>'s indexer returns the key back when
/// it cannot resolve, so the card would render "SystemStatus_Firewall_Unknown" at the user rather
/// than throwing. Nothing else would report it.
/// </para>
/// </remarks>
public class SystemStatusPresentationTests
{
    /// <summary>The nine files that must agree, named the way the repo names them.</summary>
    private static readonly string[] Languages =
        ["", "es", "fr", "hi", "id", "pl", "pt-BR", "tr", "uk"];

    /// <summary>
    /// The states a rendered row can actually be in.
    /// </summary>
    /// <remarks>
    /// <see cref="ReadinessState.NotApplicable"/> is excluded because
    /// <see cref="SystemReadinessReport.Applicable"/> filters those rows out before the card ever
    /// sees them — elevation on Linux is the live example. Demanding a sentence for a row that cannot
    /// render would be thirty-six strings nobody reads.
    /// </remarks>
    private static readonly ReadinessState[] RenderableStates =
        [ReadinessState.Ok, ReadinessState.Warning, ReadinessState.Problem, ReadinessState.Unknown];

    private static string ResxPath(string language) =>
        Path.Combine(RepoRoot(), "remex.desktop", "Localization",
            language.Length == 0 ? "Strings.resx" : $"Strings.{language}.resx");

    private static HashSet<string> KeysIn(string language) =>
        [.. XDocument.Load(ResxPath(language))
            .Root!.Elements("data")
            .Select(d => d.Attribute("name")?.Value)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)];

    [Fact]
    public void EveryCheckAndRenderableStateHasASentenceInAllNineLanguages()
    {
        var required = (from id in Enum.GetValues<ReadinessCheckId>()
                        from state in RenderableStates
                        select SystemStatusPresentation.SentenceKey(id, state)).ToList();

        // A floor against the enums being emptied or the query going wrong: five checks, four states.
        Assert.Equal(20, required.Count);

        var missing = new List<string>();
        foreach (var language in Languages)
        {
            var present = KeysIn(language);
            missing.AddRange(required.Where(k => !present.Contains(k))
                .Select(k => $"{(language.Length == 0 ? "en" : language)}:{k}"));
        }

        Assert.True(missing.Count == 0, "sentences missing: " + string.Join(", ", missing));
    }

    [Fact]
    public void EveryCheckHasAHeadingInAllNineLanguages()
    {
        var required = Enum.GetValues<ReadinessCheckId>()
            .Select(SystemStatusPresentation.TitleKey).ToList();
        Assert.Equal(5, required.Count);

        var missing = (from language in Languages
                       let present = KeysIn(language)
                       from key in required
                       where !present.Contains(key)
                       select $"{(language.Length == 0 ? "en" : language)}:{key}").ToList();

        Assert.True(missing.Count == 0, "headings missing: " + string.Join(", ", missing));
    }

    [Fact]
    public void TheCardsOwnStringsExistInAllNineLanguages()
    {
        string[] chrome =
        [
            // No SystemStatus_Explain: the Explain affordance was dropped because there is nowhere
            // for it to go, and an unused resource in nine files is the kind of thing that outlives
            // the reason for it. A help destination is a separate bead.
            "SystemStatus_Title", "SystemStatus_AllReady", "SystemStatus_Fix", "SystemStatus_Recheck",
            "SystemStatus_Unavailable",
        ];

        var missing = (from language in Languages
                       let present = KeysIn(language)
                       from key in chrome
                       where !present.Contains(key)
                       select $"{(language.Length == 0 ? "en" : language)}:{key}").ToList();

        Assert.True(missing.Count == 0, "card strings missing: " + string.Join(", ", missing));
    }

    [Fact]
    public void TheCertificateRowIsNEVEROfferedAFix()
    {
        // THE ONE RULE IN THIS FILE THAT IS ABOUT SAFETY RATHER THAN TIDINESS. The only repair anyone
        // would write for a broken certificate regenerates it, and that invalidates the SPKI hash
        // every paired phone pinned - one button would un-pair every device at once, with no undo
        // except pairing each again by hand. CLAUDE.md classes cert handling as high-risk for exactly
        // this. Reporting it is useful; offering to fix it is not.
        // **ASSERTS "NEVER A FIX", NOT "NEVER A BUTTON", AND THE DIFFERENCE IS THE WHOLE POINT.** This
        // used to assert ReportOnly, which was a proxy for the safety rule while ReportOnly was the
        // only alternative to Fix. RemEx-tb0a added Explain, and an Explain button here is safe and
        // useful - it says to restart RemEx as administrator, which regenerates nothing. Keeping the
        // proxy would have blocked that; dropping the test would have lost the rule. So the rule is
        // now stated directly, which also means a future affordance cannot slip past it.
        Assert.NotEqual(
            SystemStatusAffordance.Fix,
            SystemStatusPresentation.AffordanceFor(ReadinessCheckId.Certificate));

        foreach (var state in RenderableStates)
        {
            var row = new SystemStatusRowViewModel(
                new ReadinessCheck(ReadinessCheckId.Certificate, state, "detail"));

            Assert.False(row.ShowsFix, $"the certificate row offered a Fix button in state {state}");
        }
    }

    [Fact]
    public void AnOkRowOffersNothing_BecauseThereIsNothingToDo()
    {
        foreach (var id in Enum.GetValues<ReadinessCheckId>())
        {
            Assert.False(
                SystemStatusPresentation.ShowsAffordance(new ReadinessCheck(id, ReadinessState.Ok, "d")),
                $"{id} offered a button while it was already Ok");
        }
    }

    [Fact]
    public void WarningAndUnknownBOTHGetTheFix_NotJustProblem()
    {
        // Deliberately not "only Problem is actionable". Unknown means the check could not run, which
        // is the state most worth a user's attention precisely because nothing else will report it -
        // the same reasoning that stops IsFullyReady treating Unknown as passing. Asserted on
        // Autostart because it is the only row that offers anything at all.
        foreach (var state in new[] { ReadinessState.Warning, ReadinessState.Problem, ReadinessState.Unknown })
        {
            Assert.True(
                SystemStatusPresentation.ShowsAffordance(
                    new ReadinessCheck(ReadinessCheckId.Autostart, state, "d")),
                $"the autostart row offered nothing in state {state}");
        }
    }

    [Fact]
    public void EveryNotOkRowOffersSomething_AutostartFixesAndTheRestExplain()
    {
        // WAS ONLYAutostartOffersAnything, INVERTED BY RemEx-tb0a AS ITS OWN COMMENT SAID IT WOULD BE.
        // It pinned that only autostart offered a button, because Explain had nowhere to go and an
        // inert button teaches the user the whole card is inert. There is a destination now - an
        // in-app dialog per check - so every not-Ok row offers something, and the split is that
        // autostart FIXES (local, reversible, user-invoked) while the rest EXPLAIN, because they need
        // a person to change something outside RemEx.
        foreach (var id in Enum.GetValues<ReadinessCheckId>())
        {
            var check = new ReadinessCheck(id, ReadinessState.Problem, "d");

            Assert.True(SystemStatusPresentation.ShowsAffordance(check));
            Assert.Equal(
                id == ReadinessCheckId.Autostart
                    ? SystemStatusAffordance.Fix
                    : SystemStatusAffordance.Explain,
                SystemStatusPresentation.AffordanceFor(id));
        }
    }

    [Fact]
    public void AnOkRowStillOffersNothing()
    {
        // The other half of the rule, and the one the inversion above could have quietly dropped:
        // a row that is fine has nothing to fix OR explain, so offering a button there would invite
        // the user to act on something that is already working.
        foreach (var id in Enum.GetValues<ReadinessCheckId>())
        {
            Assert.False(
                SystemStatusPresentation.ShowsAffordance(
                    new ReadinessCheck(id, ReadinessState.Ok, "d")));
        }
    }

    [Fact]
    public void RowsAreOrderedWorstFirst_ByTheSameRankingTheHeadlineUses()
    {
        // If the card sorted by anything else, its headline state and its top row could disagree
        // about what is worst, and nothing would tell the user which to believe.
        var checks = new List<ReadinessCheck>
        {
            new(ReadinessCheckId.Autostart, ReadinessState.Ok, "d"),
            new(ReadinessCheckId.Firewall, ReadinessState.Problem, "d"),
            new(ReadinessCheckId.Elevation, ReadinessState.Warning, "d"),
            new(ReadinessCheckId.PortListening, ReadinessState.Unknown, "d"),
        };

        var ordered = SystemStatusPresentation.WorstFirst(checks);

        Assert.Equal(ReadinessState.Problem, ordered[0].State);
        Assert.Equal(ReadinessState.Unknown, ordered[1].State);
        Assert.Equal(ReadinessState.Warning, ordered[2].State);
        Assert.Equal(ReadinessState.Ok, ordered[3].State);

        // And the top row agrees with what the report calls Overall.
        Assert.Equal(new SystemReadinessReport(checks).Overall, ordered[0].State);
    }

    [Fact]
    public void NoKeyIsBuiltFromDetail_SoAnExceptionMessageCanNEVERReachTheUser()
    {
        // Detail is developer-facing English assembled for logs, sometimes straight from an exception.
        // The separation is the safeguard: because every user-facing string comes from a key derived
        // ONLY from the id and the state, there is no code path from Detail to the screen at all -
        // rather than a rule someone has to remember not to break.
        var alarming = new ReadinessCheck(
            ReadinessCheckId.Firewall, ReadinessState.Problem, "Access denied: C:\\ProgramData\\RemEx\\cert.pfx");

        Assert.Equal("SystemStatus_Firewall_Problem",
            SystemStatusPresentation.SentenceKey(alarming.Id, alarming.State));
        Assert.DoesNotContain("cert.pfx", SystemStatusPresentation.SentenceKey(alarming.Id, alarming.State));
        Assert.DoesNotContain("cert.pfx", SystemStatusPresentation.TitleKey(alarming.Id));
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, ".."));
}
