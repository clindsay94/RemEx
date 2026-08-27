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
/// Guards the button vocabulary established by RemEx-z7pnx and written down in
/// <c>docs/BUTTON-VOCABULARY.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// The vocabulary replaced 37 screen-named button classes spread across 16 files. Nothing about
/// that sprawl was an error: every one of those classes compiled, resolved and rendered. It grew
/// because there was no rule to point at, and it will grow back the same way — one screen at a
/// time, each addition locally reasonable — unless the rule is enforced rather than merely
/// written down.
/// </para>
/// <para>
/// So these tests assert the two things prose cannot: that no view has quietly reintroduced a
/// button style of its own, and that every class actually in use is one the vocabulary defines.
/// The exception list is asserted too, because an exception list nobody checks is just a wider
/// rule.
/// </para>
/// </remarks>
public class ButtonVocabularyTests
{
    /// <summary>Emphasis — exactly one per button.</summary>
    private static readonly string[] Emphasis = { "primary", "secondary", "tertiary" };

    /// <summary>Tints, modifiers and standalone roles. See docs/BUTTON-VOCABULARY.md.</summary>
    private static readonly string[] Vocabulary =
    {
        "primary", "secondary", "tertiary",
        "danger", "success", "warning",
        "compact", "pill", "icon-button",
        "tile", "card", "swatch",
        "selected", "interactive",
    };

    /// <summary>
    /// Classes that keep a bespoke style, each because a named bead owns that surface. Widening
    /// this list is how the sprawl came back last time, so it is asserted rather than assumed.
    /// </summary>
    private static readonly Dictionary<string, string> Exceptions = new()
    {
        ["nav-item"] = "RemEx-zi3ua — nav items become a Material list",
        ["nav-item-active"] = "RemEx-zi3ua — nav items become a Material list",
        ["gear-fab"] = "RemEx-bado6 — the gear becomes a Material FloatingButton",
    };

    [Fact]
    public void EveryButtonClassInUseIsInTheVocabularyOrIsADocumentedException()
    {
        // THE ANTI-SPRAWL RULE. A new screen-named class is not an error — it compiles, it
        // renders, and it looks fine on the one screen its author was looking at. This is the
        // only place that notices.
        var offenders = new List<string>();

        foreach (var (file, text) in XamlFiles())
        {
            foreach (Match match in Regex.Matches(
                         text, @"<(?:Button|ToggleButton|RepeatButton|DropDownButton|SplitButton)\b[^>]*?\bClasses=""([^""]+)"""))
            {
                foreach (var className in match.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (Vocabulary.Contains(className) || Exceptions.ContainsKey(className))
                    {
                        continue;
                    }

                    offenders.Add($"{Path.GetFileName(file)}: {className}");
                }
            }
        }

        offenders.Distinct().Should().BeEmpty(
            "every button class has to be a role from docs/BUTTON-VOCABULARY.md, or an exception "
            + "with a bead that owns it; a screen-named class is how 37 of them accumulated");
    }

    [Fact]
    public void NoButtonCarriesTwoEmphases()
    {
        // "primary secondary" is not louder, it is undefined: Avalonia has no specificity, so
        // whichever emphasis block sits lower in App.axaml wins, and the answer changes when
        // someone reorders the file. It also always means the author wanted a TINT or a MODIFIER
        // and reached for an emphasis, which is the mistake the three-column table exists to stop.
        var offenders = new List<string>();

        foreach (var (file, text) in XamlFiles())
        {
            foreach (Match match in Regex.Matches(
                         text, @"<(?:Button|ToggleButton|RepeatButton|DropDownButton|SplitButton)\b[^>]*?\bClasses=""([^""]+)"""))
            {
                var classes = match.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (classes.Count(Emphasis.Contains) > 1)
                {
                    offenders.Add($"{Path.GetFileName(file)}: {match.Groups[1].Value}");
                }
            }
        }

        offenders.Should().BeEmpty(
            "emphasis is a single choice; two of them resolve by document order in App.axaml "
            + "rather than by intent");
    }

    [Fact]
    public void NoViewDeclaresAButtonStyleOfItsOwn()
    {
        // The acceptance criterion the bead actually asked for: the per-view style blocks are
        // DELETED, not merely overridden. An overridden block still wins on the properties it
        // sets — Avalonia applies view styles after application styles — so leaving them in place
        // would have produced a vocabulary that was documented and inert.
        var offenders = new List<string>();

        foreach (var (file, text) in XamlFiles())
        {
            if (Path.GetFileName(file) == "App.axaml")
            {
                continue;
            }

            foreach (Match match in Regex.Matches(text, @"<Style\s+Selector=""([^""]*)"""))
            {
                var selector = match.Groups[1].Value;
                if (!Regex.IsMatch(selector, @"\bButton\b"))
                {
                    continue;
                }

                // Template-part selectors and the focus-ring rules are chrome and accessibility,
                // not button LOOKS — WindowChrome's minimise/maximise buttons are parts of a
                // window template rather than app buttons.
                if (selector.Contains("/template/", StringComparison.Ordinal)
                    || selector.Contains(":focus-visible", StringComparison.Ordinal))
                {
                    continue;
                }

                if (Exceptions.Keys.Any(exception =>
                        selector.Contains("." + exception, StringComparison.Ordinal)))
                {
                    continue;
                }

                offenders.Add($"{Path.GetFileName(file)}: {selector}");
            }
        }

        offenders.Should().BeEmpty(
            "a view that styles its own buttons is a 38th class waiting to happen; the roles live "
            + "in App.axaml and the exceptions are listed in docs/BUTTON-VOCABULARY.md");
    }

    [Fact]
    public void EveryVocabularyClassIsActuallyDeclaredInAppXaml()
    {
        // ANTI-VACUITY. The three tests above are all satisfied by a vocabulary that defines
        // nothing: an empty Vocabulary array makes every class an offender, but a vocabulary
        // listing classes App.axaml never declares makes every button inert instead — the exact
        // failure this epic already hit twice, where Classes="glass-card interactive" on a Button
        // matched a Border-only selector and rendered as nothing at all.
        var app = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "App.axaml"));

        foreach (var className in Vocabulary)
        {
            // Matched anywhere in a Button selector, not only immediately after "Button", because
            // `selected` and `interactive` are state modifiers that exist ONLY in combination —
            // Button.tile.selected, Button.card.interactive — and a bare Button.selected would be
            // a class that paints a state onto anything.
            // The tail is a negative lookahead, not \b: '-' is a non-word character, so \b would
            // have been satisfied by a selector named .pill-x for a vocabulary entry of .pill.
            // Injection found that — the guard passed with the style it was watching renamed away.
            app.Should().MatchRegex($@"<Style Selector=""[^""]*Button[\w.:()-]*\.{Regex.Escape(className)}(?![\w-])",
                $"the vocabulary names .{className}, so App.axaml has to declare it or every "
                + "button wearing it renders unstyled");
        }
    }

    [Fact]
    public void TheExceptionListMatchesTheDocumentation()
    {
        // An exception list that drifts from its documentation is worse than no list: the doc is
        // what a reader consults before adding a bespoke style, and the test is what stops them.
        // They have to agree, and each exception has to name the bead that will retire it.
        var doc = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "BUTTON-VOCABULARY.md"));

        foreach (var (className, reason) in Exceptions)
        {
            doc.Should().Contain(className,
                $"docs/BUTTON-VOCABULARY.md has to list the .{className} exception");

            var bead = Regex.Match(reason, @"RemEx-[a-z0-9.]+").Value;
            bead.Should().NotBeEmpty($"the .{className} exception has to name the bead that owns it");
            doc.Should().Contain(bead,
                $"the doc has to name {bead} as the owner of the .{className} exception");
        }
    }

    // ─────────────────────────── plumbing ───────────────────────────

    private static IEnumerable<(string File, string Text)> XamlFiles()
        => Directory
            .EnumerateFiles(Path.Combine(RepoRoot(), "remex.desktop"), "*.axaml", SearchOption.AllDirectories)
            .Select(file => (file, File.ReadAllText(file)));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
