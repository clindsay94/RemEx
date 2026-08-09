using System.Globalization;
using System.Text.RegularExpressions;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Pins that every theme supplies a readable foreground for accent-, success- and error-filled
/// surfaces (RemEx-tq2e, RemEx-iegl, RemEx-xb3c).
/// </summary>
/// <remarks>
/// <para>
/// Nine call sites used a literal <c>Foreground="White"</c> on an accent-filled button. Measured
/// against each theme's own accent, that literal FAILS WCAG AA on three of the four — CyberNOC at
/// 1.38:1 is barely legible, SolarFlare 1.73:1, Monolith 3.65:1 — and passes only on BaseDarkGlass.
/// </para>
/// <para>
/// **THE VALUES ARE ARITHMETIC, NOT TASTE**, which is what let this be fixed rather than deferred:
/// the correct foreground is simply whichever of black or white contrasts better, and it differs per
/// theme. That difference is also why a single literal could never have worked.
/// </para>
/// <para>
/// A MISSING DynamicResource IS SILENT IN AVALONIA — it is neither a compile error nor a runtime
/// one, so a theme that dropped this key would render an unstyled default and nothing would say so.
/// This reads the theme files directly because that is the only way to catch it.
/// </para>
/// </remarks>
public class AccentForegroundContrastTests
{
    private static string ThemesDirectory() =>
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "remex.desktop", "Themes");

    /// <summary>Every theme on disk, not a list someone has to remember to extend.</summary>
    /// <remarks>
    /// A HARDCODED LIST IS SILENTLY INCOMPLETE. Adding a fifth theme without the tokens would give
    /// exactly the unresolved-DynamicResource failure this class exists to catch, with every test
    /// green, because the new file would never be looked at.
    /// </remarks>
    private static string[] Themes =>
        Directory.EnumerateFiles(ThemesDirectory(), "*.axaml")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

    private static string ThemePath(string theme) =>
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "remex.desktop", "Themes", theme + ".axaml");

    private static double RelativeLuminance(string hex)
    {
        var h = hex.TrimStart('#');
        if (h.Length == 8) h = h[2..];

        var channels = new double[3];
        for (var i = 0; i < 3; i++)
        {
            var v = Convert.ToInt32(h.Substring(i * 2, 2), 16) / 255.0;
            channels[i] = v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * channels[0] + 0.7152 * channels[1] + 0.0722 * channels[2];
    }

    private static double Contrast(string a, string b)
    {
        var (x, y) = (RelativeLuminance(a), RelativeLuminance(b));
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    [Fact]
    public void EveryThemeDeclaresAnAccentForeground()
    {
        // A DynamicResource that resolves to nothing is silent in Avalonia, so a theme that dropped
        // the key would render an unstyled default with no error anywhere.
        foreach (var theme in Themes)
        {
            var text = File.ReadAllText(ThemePath(theme));
            Assert.Contains("AccentForegroundBrush", text);
        }
    }

    [Fact]
    public void TheAccentForegroundMeetsWcagAAAgainstItsOwnAccent()
    {
        // THE PROPERTY, CHECKED RATHER THAN ASSERTED FROM A TABLE. If a future theme changes its
        // accent, this fails until its foreground is revisited - which is the whole reason the value
        // is per-theme rather than one literal.
        foreach (var theme in Themes)
        {
            var text = File.ReadAllText(ThemePath(theme));

            var accent = Extract(text, "<Color x:Key=\"AccentPrimary\">");
            var foreground = ExtractBrush(text, "AccentForegroundBrush");

            var ratio = Contrast(accent, foreground);
            Assert.True(ratio >= 4.5,
                $"{theme}: foreground {foreground} on accent {accent} is {ratio:F2}:1, below WCAG AA 4.5:1");
        }
    }

    [Fact]
    public void PlainWhiteWouldFailOnMostThemes_WhichIsWhyTheTokenExists()
    {
        // The control. Without it the test above could pass against a token that merely happens to
        // equal the literal it replaced, and the change would look justified when it was not.
        var failures = 0;
        foreach (var theme in Themes)
        {
            var accent = Extract(File.ReadAllText(ThemePath(theme)), "<Color x:Key=\"AccentPrimary\">");
            if (Contrast(accent, "#FFFFFF") < 4.5) failures++;
        }

        // EXPRESSED AGAINST THE LIST, NOT AS 3. Themes now comes from disk, so a literal 3 would
        // fail on a fifth theme for the wrong reason - it would look like a contrast regression when
        // it is only a bigger list. White passes on exactly one theme, BaseDarkGlass.
        Assert.Equal(Themes.Length - 1, failures);
    }

    /// <summary>Reads a colour literal, whether it is written #RRGGBB or #AARRGGBB.</summary>
    /// <remarks>
    /// THIS TOOK A FIXED 7 CHARACTERS UNTIL RemEx-1elh, AND THE TRUNCATION WAS SILENT. Every key it
    /// is called with is 6-digit today, so nothing was measuring the wrong colour — but 8-digit
    /// literals are ordinary in these files (<c>GlassBaseMedium</c>, <c>CardBackground</c>,
    /// <c>CardBorder</c> and more), Avalonia accepts <c>#AARRGGBB</c> for any Color key, and a
    /// 7-character cut of one yields the alpha and the first two channels read as red, green, blue.
    /// A composite test built on the old behaviour produced <c>#F40F1D</c> — a surface that exists
    /// nowhere — and failed CyberNOC at 4.26:1 for a reason that had nothing to do with the theme.
    /// Widening costs nothing, since <c>RelativeLuminance</c> and <c>Composite</c> both strip a
    /// leading alpha byte already. Note the asymmetry that made this worth chasing:
    /// <c>ExtractBrush</c>'s fixed 9 throws on a narrow literal, so it fails LOUDLY; this one
    /// returned a plausible wrong colour.
    /// </remarks>
    private static string Extract(string text, string marker)
    {
        var i = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(i >= 0, $"missing {marker}");

        var start = text.IndexOf('#', i);
        Assert.True(start >= 0, $"{marker}: no colour literal follows it");

        var end = start + 1;
        while (end < text.Length && Uri.IsHexDigit(text[end])) end++;

        var hex = text[start..end];
        Assert.True(hex.Length is 7 or 9, $"{marker}: unexpected colour literal {hex}");
        return hex;
    }

    private static string ExtractBrush(string text, string key)
    {
        // MATCHED ON THE DECLARATION, NOT THE BARE NAME. IndexOf(key) finds the first MENTION, and
        // the rationale comment above SuccessForegroundBrush names AccentForegroundBrush - so this
        // resolved correctly only because the accent declaration happens to come first in all four
        // files. A reorder would have silently made the accent test measure the green value and pass.
        var i = text.IndexOf($"x:Key=\"{key}\"", StringComparison.Ordinal);
        Assert.True(i >= 0, $"missing {key}");
        var start = text.IndexOf('#', i);
        return text.Substring(start, 9);
    }

    // ── The success surface, and the views themselves (RemEx-iegl) ─────────────────────────────

    [Fact]
    public void EveryThemeDeclaresASuccessForeground()
    {
        foreach (var theme in Themes)
        {
            Assert.Contains("SuccessForegroundBrush", File.ReadAllText(ThemePath(theme)));
        }
    }

    [Fact]
    public void TheSuccessForegroundMeetsWcagAAAgainstItsOwnSuccess()
    {
        // A SEPARATE TOKEN, NOT A REUSE OF THE ACCENT ONE, and measurement is why. White fails on
        // the success fill in ALL FOUR themes (2.28, 1.34, 2.02, 3.30), and AccentForegroundBrush
        // does not rescue it either: on BaseDarkGlass that token IS white, correctly, because its
        // purple accent wants white - so borrowing it would leave the green button at 2.28 while
        // looking fixed. Green needs dark text in every theme; the accent does not.
        foreach (var theme in Themes)
        {
            var text = File.ReadAllText(ThemePath(theme));
            var success = Extract(text, "<Color x:Key=\"SystemSuccess\">");
            var foreground = ExtractBrush(text, "SuccessForegroundBrush");

            var ratio = Contrast(success, foreground);
            Assert.True(ratio >= 4.5, $"{theme}: {foreground} on {success} is {ratio:F2}:1, below AA.");
        }
    }

    /// <summary>Every white-on-filled offence in one .axaml, in both spellings that occur here.</summary>
    /// <remarks>
    /// <para>
    /// **SHARED WITH THE ANTI-VACUITY TEST ON PURPOSE.** The first version of that test built a
    /// synthetic offender and re-implemented the matching beside it, so it proved <c>String.Split</c>
    /// worked and nothing about the scan — it would have stayed green through a wrong path, an empty
    /// brush list, or drift between the two copies. Probing the real function is the only version
    /// that means anything.
    /// </para>
    /// <para>
    /// **TWO SPELLINGS, BECAUSE THE FIRST VERSION ONLY SAW ONE.** Splitting on '&lt;' finds the
    /// inline form, where one element carries both attributes. It is structurally blind to the style
    /// form, where <c>&lt;Setter Property="Background"&gt;</c> and <c>&lt;Setter
    /// Property="Foreground"&gt;</c> land in different fragments — and review found SEVEN live
    /// buttons hiding in exactly that gap while the scan reported zero.
    /// </para>
    /// <para>
    /// <c>Fill</c> counts as well as <c>Foreground</c>, because a glyph inside an accent button is a
    /// <c>Path</c> that <c>Foreground</c> never reaches, and WCAG 1.4.11 still wants 3:1 for it. The
    /// resource key is matched with an optional <c>Brush</c> suffix because this repo writes both —
    /// <c>CanvasView</c> binds <c>{DynamicResource SystemError}</c>, the Color key, directly.
    /// </para>
    /// <para>
    /// **THE GAP THIS ONCE HAD, AND HOW IT CLOSED (RemEx-o9gd, then RemEx-ugvcr).** A glyph whose
    /// <c>Fill</c> is inline on a child element while its container is filled by a CLASS used to be
    /// invisible: the two live on different elements, and neither the split nor the style pass brings
    /// them together. <c>ShellView</c>'s gear button was exactly that shape, was fixed by hand, and
    /// reverting that fix left this scan green — measured at the time, not assumed. It is covered now
    /// by <c>ScanAncestry</c>, which tracks open elements on a stack rather than parsing XAML, and the
    /// same revert is the injection that proves it.
    /// <para>
    /// WHAT IS STILL OUT OF REACH, so this paragraph does not go stale the way the last one did: a
    /// class applied from another file, a literal inside a <c>ControlTemplate</c> whose target resolves
    /// at runtime, and a background that arrives through a theme's implicit styles rather than a class
    /// this file can see. Those genuinely need what a real parse and a live visual tree give.
    /// </para>
    /// </para>
    /// <para>
    /// ERROR RED IS NOW LISTED TOO, and the history is worth keeping. RemEx-tq2e tried to sweep red
    /// in with the accent token and had to revert: <c>AccentForegroundBrush</c> is dark on SolarFlare
    /// and took red from 4.83:1 down to 3.81:1. The answer on red genuinely inverts the accent one —
    /// three themes want DARK text and SolarFlare wants WHITE — so it needed a token of its own
    /// (<c>ErrorForegroundBrush</c>, RemEx-xb3c) rather than a borrowed one. Precisely: on
    /// BaseDarkGlass the accent wants white and red wants dark; on SolarFlare the accent wants dark
    /// and red wants white; and borrowing the accent token fails AA on both — 3.67:1 and 3.81:1. It
    /// is identical in the other two themes, which is a coincidence and not a licence. Only once a
    /// measured token existed could this rule cover red without lying.
    /// </para>
    /// <para>
    /// <c>SystemErrorBackgroundBrush</c> is NOT swept in by the <c>SystemError</c> entry, and that is
    /// deliberate rather than luck: the pattern requires the key to end there or at <c>Brush</c>, so
    /// the translucent variant does not match. It is a genuinely different surface — 15% red over
    /// glass, where white measures 15.96:1 on BaseDarkGlass and 1.38:1 on SolarFlare — and the error
    /// token would be catastrophic on it at 1.18–1.67:1. That surface is RemEx-1elh.
    /// </para>
    /// </remarks>
    private static List<string> ScanForWhiteOnFilled(string rawAxaml, string label)
    {
        // COMMENTS COME OUT ONCE, FOR EVERY PASS. The ancestry walk needs it — a commented-out opening
        // tag pushes a frame that never pops and leaks its class over everything below it — but the
        // other three wanted it all along and nobody had noticed: a commented-out offender used to be
        // reported by three passes and correctly ignored by the fourth.
        var axaml = Regex.Replace(rawAxaml, "<!--.*?-->", string.Empty, RegexOptions.Singleline);

        // LEFT-ANCHORED so an attribute merely ENDING in Foreground - SelectionForeground, say -
        // is not reported. None exist today; the guard should not create a false positive for the
        // first one that does.
        const string white = "\"(?:White|#FFFFFFFF|#FFFFFF)\"";
        // SystemErrorBackground JOINED THIS LIST WITH RemEx-1elh, and the timing is the point rather
        // than an oversight being corrected. It was excluded for a real reason — white was CORRECT on
        // three of the four themes there, so a guard banning it would have demanded the wrong fix and
        // been argued down. Once ErrorTintForegroundBrush existed and was measured, the exclusion
        // stopped being true, so the entry and the assertion pinning it flipped together.
        // The Hover variant is deliberately absent: the pattern below requires the key to end at the
        // brush name or at "Brush", so "SystemErrorBackgroundHoverBrush" never matches this entry, and
        // the hover style sets Background alone — there is no Foreground beside it to pair with.
        var filled = new[] { "AccentPrimary", "SystemSuccess", "SystemError", "SystemErrorBackground" };
        var offences = new List<string>();

        foreach (var brush in filled)
        {
            var resource = $"\"{{(?:Dynamic|Static)Resource {brush}(?:Brush)?}}\"";

            // The inline form: one element carrying both attributes.
            foreach (var element in axaml.Split('<'))
            {
                if (Regex.IsMatch(element, $"(?<![\\w.])(?:Foreground|Fill)={white}")
                    && Regex.IsMatch(element, $"Background={resource}"))
                {
                    offences.Add($"{label}: inline on {brush}");
                }
            }

            // The style form: two setters in one block, which the split above cannot see together.
            foreach (Match block in Regex.Matches(axaml, "<Style\\b.*?</Style>", RegexOptions.Singleline))
            {
                if (Regex.IsMatch(block.Value, $"Property=\"(?<![\\w.])(?:Foreground|Fill)\" Value={white}")
                    && Regex.IsMatch(block.Value, $"Property=\"Background\" Value={resource}"))
                {
                    var selector = Regex.Match(block.Value, "Selector=\"([^\"]+)\"");
                    offences.Add($"{label}: style {(selector.Success ? selector.Groups[1].Value : "?")} on {brush}");
                }
            }

            // THE CLASS FORM (RemEx-1elh), and it is here because injection proved the two passes above
            // are BLIND to the shape this bead was actually about. CanvasView's remove button carries
            // Foreground="White" inline while its background comes from Classes="card-action-danger" —
            // so the attributes live on different elements and neither pass can pair them. Putting the
            // literal back scored zero offences against the finished fix.
            //
            // THIS PASS COVERS THE SAME-ELEMENT CASE: the literal and the Classes attribute on one
            // element, the background on a style block elsewhere. The child-glyph case — a literal on a
            // DESCENDANT of the class-filled element — needs the tree, and ScanAncestry below handles
            // it. Same file only, which is where this repo declares its view-local classes.
            // A COMPOUND SELECTOR IS AND, NOT OR, and reading it as OR is a false-positive generator
            // rather than a theoretical one: TrayBalloonWindow declares Border.accent-stripe.problem
            // over a SystemError background, so flattening it would mark BOTH names dangerous and then
            // report any element wearing accent-stripe alone. Only the LAST part of the selector is
            // taken, because that is the element the setters apply to — in "Button.nav-item Path" the
            // fill lands on the Path, not on anything wearing nav-item.
            var dangerousSets = Regex.Matches(axaml, "<Style\\b.*?</Style>", RegexOptions.Singleline)
                .Where(block => Regex.IsMatch(block.Value, $"Property=\"Background\" Value={resource}"))
                .Select(block => Regex.Match(block.Value, "Selector=\"([^\"]+)\""))
                .Where(selector => selector.Success)
                .Select(selector => selector.Groups[1].Value.Split(new[] { ' ', '>' },
                    StringSplitOptions.RemoveEmptyEntries).Last())
                .Select(target => Regex.Matches(target, "\\.([A-Za-z0-9_-]+)")
                    .Select(m => m.Groups[1].Value).ToArray())
                .Where(set => set.Length > 0)
                .ToList();

            if (dangerousSets.Count > 0)
            {
                foreach (var element in axaml.Split('<'))
                {
                    if (!Regex.IsMatch(element, $"(?<![\\w.])(?:Foreground|Fill)={white}")) continue;

                    // AN INLINE Background BEATS THE STYLE SETTER in Avalonia, so an element that sets
                    // its own is not on the class's surface at all. Eighteen elements in Views already
                    // do this. Skipping them loses nothing: if the inline value IS a filled resource,
                    // the first pass above already has it, on the same element.
                    if (Regex.IsMatch(element, "(?<![\\w.])Background=\"")) continue;

                    // BOTH SPELLINGS. Classes="a b" is the plain form; Classes.danger="{Binding ...}"
                    // is the conditional one, and it is not exotic — twenty-seven elements across
                    // eleven view files use it. A pass that saw only the first would be blind to every
                    // class applied by a binding, which is the same shape of miss it exists to close.
                    // ONE COPY OF THE RULES, shared with the ancestry walk. Two copies of a
                    // class-spelling regex is how one of them goes stale and its pass quietly stops
                    // seeing classes — exactly the drift this file's remarks warn about elsewhere.
                    var worn = ClassesOn(element);
                    if (worn.Length == 0) continue;

                    foreach (var set in dangerousSets.Where(set => set.All(worn.Contains)))
                    {
                        offences.Add($"{label}: class-filled on {brush} via {string.Join(".", set)}");
                    }
                }

                // AND THE CHILD-GLYPH SHAPE (RemEx-ugvcr), which is the last one that was invisible.
                // A Path inside a class-filled Button carries its own Fill, and Foreground never
                // reaches it — so the literal and the class sit on DIFFERENT elements and none of the
                // passes above can pair them. ShellView's gear FAB is exactly this, fixed by hand in
                // RemEx-o9gd, and reverting that fix used to leave this file green.
                foreach (var offence in ScanAncestry(axaml, dangerousSets, white))
                {
                    offences.Add($"{label}: child glyph on {brush} inside {offence}");
                }
            }
        }

        return offences;
    }

    /// <summary>
    /// Finds a white literal on an element whose FILLED ancestor is filled by a class (RemEx-ugvcr).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A BOUNDED WALK RATHER THAN A XAML PARSE, and the bound is the point. The three passes beside
    /// this one deliberately compare attributes that sit together, which is why they are regexes and
    /// why they are trustworthy. This one needs the tree, so it tracks open elements on a stack and
    /// nothing more: no resource resolution, no templates, no cross-file classes. That covers the
    /// shape this repo actually writes — a glyph directly inside a styled container — without
    /// pretending to understand markup it does not.
    /// </para>
    /// <para>
    /// STRICT ANCESTORS ONLY. An element carrying both the class and the literal is the same-element
    /// case and is already reported by the pass above; including self here would double-count it.
    /// </para>
    /// <para>
    /// COMMENTS ARE STRIPPED FIRST because a commented-out element would push a frame that never pops
    /// and would then attribute its class to everything below it — the failure mode of a hand-rolled
    /// walk, and silent, since it produces MORE findings rather than fewer.
    /// </para>
    /// </remarks>
    private static List<string> ScanAncestry(string markup, List<string[]> dangerousSets, string white)
    {
        var offences = new List<string>();

        // Attribute values are matched as quoted runs, in BOTH quote styles, so a '>' inside one — a
        // binding, a selector, a StringFormat — does not end the tag early. Single quotes are legal
        // and five attributes in Views already use them; without the alternative, everything written
        // after such a value in the same tag would be invisible, which is the silent direction.
        var tags = Regex.Matches(markup, "<(/?)([A-Za-z_][\\w:.\\-]*)((?:\"[^\"]*\"|'[^']*'|[^>\"'])*?)(/?)>");
        var stack = new List<string[]>();

        foreach (Match tag in tags)
        {
            var closing = tag.Groups[1].Value == "/";
            var attributes = tag.Groups[3].Value;
            var selfClosing = tag.Groups[4].Value == "/";

            if (closing)
            {
                if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                continue;
            }

            if (Regex.IsMatch(attributes, $"(?<![\\w.])(?:Foreground|Fill)={white}"))
            {
                // ONE FRAME MUST CARRY THE WHOLE SET, not the ancestor chain between them. Flattening
                // the stack would re-admit the exact OR-for-AND bug the same-element pass takes care
                // to avoid: Border.accent-stripe.problem is a live compound selector here, and a
                // .accent-stripe wrapping a .problem fills NEITHER element, because the style needs
                // both names on one.
                foreach (var set in dangerousSets.Where(set => stack.Any(frame => set.All(frame.Contains))))
                {
                    offences.Add($"<{tag.Groups[2].Value}> under {string.Join(".", set)}");
                }
            }

            if (selfClosing) continue;

            // AN INLINE Background ON AN ANCESTOR STOPS ITS CLASS MATTERING TO DESCENDANTS, for the
            // same reason it stops mattering to the element itself one pass above: the inline value
            // beats the style setter, so nothing below is on the class's surface. It pushes an empty
            // frame rather than being skipped, because it still has to be popped by its closing tag.
            stack.Add(Regex.IsMatch(attributes, "(?<![\\w.])Background=\"")
                ? Array.Empty<string>()
                : ClassesOn(attributes));
        }

        return offences;
    }

    /// <summary>Both spellings of a class attribute, on one element.</summary>
    private static string[] ClassesOn(string attributes)
    {
        var worn = new List<string>();

        var plain = Regex.Match(attributes, "(?<![\\w.])Classes=\"([^\"]*)\"");
        if (plain.Success)
        {
            worn.AddRange(plain.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        worn.AddRange(Regex.Matches(attributes, "(?<![\\w.])Classes\\.([A-Za-z0-9_-]+)=")
            .Select(m => m.Groups[1].Value));

        return worn.ToArray();
    }

    [Fact]
    public void NoViewPutsAWhiteLITERALOnAFilledSurface()
    {
        // THE GUARD THAT WOULD HAVE CAUGHT THIS, and its absence is why the first sweep looked
        // complete. The theme tests above read Themes/ only, so they proved every theme OFFERS a
        // readable foreground while seventeen SITES went on ignoring it - ten inline accent, two
        // inline success, five style blocks - the fix and its guard
        // were measuring different things.
        var viewsDirectory = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "remex.desktop", "Views");
        Assert.True(Directory.Exists(viewsDirectory), $"Views moved: {viewsDirectory}");

        var files = Directory.EnumerateFiles(viewsDirectory, "*.axaml", SearchOption.AllDirectories).ToList();
        Assert.NotEmpty(files);

        var offenders = files
            .SelectMany(file => ScanForWhiteOnFilled(File.ReadAllText(file), Path.GetFileName(file)))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "A white literal sits on a filled surface, unreadable in at least one theme: "
                + string.Join(", ", offenders)
                + ". Use the per-theme token for that surface: AccentForegroundBrush, "
                + "SuccessForegroundBrush, ErrorForegroundBrush for the solid error fill, or "
                + "ErrorTintForegroundBrush for the translucent one.");
    }

    [Fact]
    public void TheViewScanFindsBOTHSpellings_AndLeavesTheTRANSLUCENTSurfaceAlone()
    {
        // Feeds the REAL function the shapes it hunts. The style case is the one that matters: the
        // scan reported zero against seven live style-driven offenders before this existed.
        Assert.Single(ScanForWhiteOnFilled(
            "<Button Background=\"{DynamicResource AccentPrimaryBrush}\" Foreground=\"White\"/>", "inline"));

        Assert.Single(ScanForWhiteOnFilled(
            "<Style Selector=\"Button.x\">"
            + "<Setter Property=\"Background\" Value=\"{DynamicResource AccentPrimaryBrush}\"/>"
            + "<Setter Property=\"Foreground\" Value=\"White\"/></Style>", "style"));

        // A glyph, where Foreground does not reach and Fill does.
        Assert.Single(ScanForWhiteOnFilled(
            "<Style Selector=\"Button.x\">"
            + "<Setter Property=\"Background\" Value=\"{DynamicResource SystemSuccessBrush}\"/>"
            + "<Setter Property=\"Fill\" Value=\"White\"/></Style>", "glyph"));

        // The Color-key spelling, which this repo actually uses.
        Assert.Single(ScanForWhiteOnFilled(
            "<Button Background=\"{DynamicResource AccentPrimary}\" Foreground=\"White\"/>", "colorkey"));

        // ERROR RED IS COVERED NOW, in both spellings, because RemEx-xb3c gave it a measured token of
        // its own. It was excluded until then for a real reason - no correct foreground existed to
        // point people at - and this assertion flipped with the fix rather than being left behind.
        Assert.Single(ScanForWhiteOnFilled(
            "<Button Background=\"{DynamicResource SystemErrorBrush}\" Foreground=\"White\"/>", "red"));
        Assert.Single(ScanForWhiteOnFilled(
            "<Button Background=\"{DynamicResource SystemError}\" Foreground=\"White\"/>", "redcolor"));

        // AND THE TRANSLUCENT VARIANT NOW TOO (RemEx-1elh), where this assertion used to read Empty.
        // The exclusion was correct while it stood: white IS right on three themes over 15% red on
        // glass (15.96, 16.74, 11.85) and the solid error token measures 1.18-1.67 there, so a guard
        // banning white would have demanded exactly the wrong fix. What changed is that
        // ErrorTintForegroundBrush now exists and is measured, so there is somewhere correct to send
        // people — and a literal is wrong even where it happens to be readable, because it is right by
        // luck on three themes and 1.38:1 on the fourth.
        Assert.Single(ScanForWhiteOnFilled(
            "<Button Background=\"{DynamicResource SystemErrorBackgroundBrush}\" Foreground=\"White\"/>",
            "translucent"));

        // THE CLASS FORM, which is the one this file could not see until RemEx-1elh. The background is
        // three elements away in a style block and only a class name connects them. Injection is what
        // proved it was needed: with the fix in place, putting CanvasView's literal back scored zero.
        Assert.Single(ScanForWhiteOnFilled(
            "<Style Selector=\"Button.danger\">"
            + "<Setter Property=\"Background\" Value=\"{DynamicResource SystemErrorBackgroundBrush}\"/></Style>"
            + "<Button Classes=\"card-action danger\" Foreground=\"White\"/>", "classform"));

        // AND IT MUST NOT FIRE ON A CLASS THAT IS MERELY PRESENT. Without this, the pass could match
        // any element carrying any class in a file that happens to contain a filled style anywhere,
        // which would be a guard that reports every button and therefore none.
        Assert.Empty(ScanForWhiteOnFilled(
            "<Style Selector=\"Button.danger\">"
            + "<Setter Property=\"Background\" Value=\"{DynamicResource SystemErrorBackgroundBrush}\"/></Style>"
            + "<Button Classes=\"card-action\" Foreground=\"White\"/>", "innocent"));

        // THE CONDITIONAL SPELLING, Classes.<name>="{Binding}", which twenty-seven live elements use
        // and which the first version of this pass could not see at all.
        Assert.Single(ScanForWhiteOnFilled(
            "<Style Selector=\"Button.danger\">"
            + "<Setter Property=\"Background\" Value=\"{DynamicResource SystemErrorBackgroundBrush}\"/></Style>"
            + "<Button Classes.danger=\"{Binding HasError}\" Foreground=\"White\"/>", "bound"));

        // A COMPOUND SELECTOR IS AND. TrayBalloonWindow really does declare Border.accent-stripe.problem
        // over an error background, so reading it as OR would report every element wearing either name.
        Assert.Empty(ScanForWhiteOnFilled(
            "<Style Selector=\"Border.accent-stripe.problem\">"
            + "<Setter Property=\"Background\" Value=\"{DynamicResource SystemErrorBackgroundBrush}\"/></Style>"
            + "<Border Classes=\"accent-stripe\" Foreground=\"White\"/>", "half"));

        Assert.Single(ScanForWhiteOnFilled(
            "<Style Selector=\"Border.accent-stripe.problem\">"
            + "<Setter Property=\"Background\" Value=\"{DynamicResource SystemErrorBackgroundBrush}\"/></Style>"
            + "<Border Classes=\"accent-stripe problem\" Foreground=\"White\"/>", "both"));

        // THE CHILD-GLYPH SHAPE (RemEx-ugvcr). Foreground does not reach a Path, so it carries its own
        // Fill — on a different element from the class that fills it. ShellView's gear FAB is this
        // shape, and reverting its hand fix used to leave every test here green.
        Assert.Single(ScanForWhiteOnFilled(
            "<Style Selector=\"Button.fab\">"
            + "<Setter Property=\"Background\" Value=\"{DynamicResource AccentPrimaryBrush}\"/></Style>"
            + "<Button Classes=\"fab\"><Path Fill=\"White\"/></Button>", "glyphchild"));

        // IT IS A WALK, NOT A PARENT CHECK, AND THIS IS THE ONLY THING THAT SAYS SO. Review caught
        // that every other probe here — and ShellView's gear FAB, the injection that supposedly proves
        // this pass — nests the glyph exactly ONE level deep. Replacing the ancestor walk with "look at
        // the immediate parent" left the entire suite and the injection green while silently losing
        // <Button Classes="fab"><StackPanel><Path Fill="White"/></StackPanel></Button>, which is the
        // ordinary icon-and-label button and commoner than the depth-1 shape that was covered.
        Assert.Single(ScanForWhiteOnFilled(
            "<Style Selector=\"Button.fab\">"
            + "<Setter Property=\"Background\" Value=\"{DynamicResource AccentPrimaryBrush}\"/></Style>"
            + "<Button Classes=\"fab\"><Grid><StackPanel><Path Fill=\"White\"/></StackPanel></Grid></Button>",
            "deep"));

        // A COMPOUND SET MUST SIT ON ONE ANCESTOR, not be assembled from several. Neither Border below
        // is filled - the style needs both names on the same element - so reporting this would send
        // someone to "fix" correct markup.
        Assert.Empty(ScanForWhiteOnFilled(
            "<Style Selector=\"Border.accent-stripe.problem\">"
            + "<Setter Property=\"Background\" Value=\"{DynamicResource SystemErrorBackgroundBrush}\"/></Style>"
            + "<Border Classes=\"accent-stripe\"><Border Classes=\"problem\"><Path Fill=\"White\"/></Border></Border>",
            "split"));

        // AN ANCESTOR THAT SETS Background INLINE IS NOT ON THE CLASS'S SURFACE, and neither is
        // anything inside it. Five live elements already wear a dangerous class and override their
        // background this way.
        Assert.Empty(ScanForWhiteOnFilled(
            "<Style Selector=\"Button.fab\">"
            + "<Setter Property=\"Background\" Value=\"{DynamicResource AccentPrimaryBrush}\"/></Style>"
            + "<Button Classes=\"fab\" Background=\"{DynamicResource CardBackgroundBrush}\">"
            + "<Path Fill=\"White\"/></Button>", "overriddenparent"));

        // ONCE THE CONTAINER IS CLOSED, ITS CLASS STOPS APPLYING. Without popping the stack this would
        // report every white literal in the rest of the file, which is the failure mode of a
        // hand-rolled walk: it produces MORE findings, so it looks like a working guard.
        Assert.Empty(ScanForWhiteOnFilled(
            "<Style Selector=\"Button.fab\">"
            + "<Setter Property=\"Background\" Value=\"{DynamicResource AccentPrimaryBrush}\"/></Style>"
            + "<Button Classes=\"fab\"><Path Data=\"x\"/></Button><Path Fill=\"White\"/>", "sibling"));

        // A SELF-CLOSING CONTAINER OPENS NOTHING. If it pushed a frame, the frame would never pop and
        // its class would leak onto everything after it.
        Assert.Empty(ScanForWhiteOnFilled(
            "<Style Selector=\"Button.fab\">"
            + "<Setter Property=\"Background\" Value=\"{DynamicResource AccentPrimaryBrush}\"/></Style>"
            + "<Button Classes=\"fab\"/><Path Fill=\"White\"/>", "selfclosing"));

        // A COMMENTED-OUT CONTAINER IS NOT A CONTAINER. Its opening tag has no closing tag to pop it.
        Assert.Empty(ScanForWhiteOnFilled(
            "<Style Selector=\"Button.fab\">"
            + "<Setter Property=\"Background\" Value=\"{DynamicResource AccentPrimaryBrush}\"/></Style>"
            + "<!-- <Button Classes=\"fab\"> --><Path Fill=\"White\"/>", "commented"));

        // AN INLINE Background WINS OVER THE STYLE, so the element is not on the class's surface.
        Assert.Empty(ScanForWhiteOnFilled(
            "<Style Selector=\"Button.danger\">"
            + "<Setter Property=\"Background\" Value=\"{DynamicResource SystemErrorBackgroundBrush}\"/></Style>"
            + "<Button Classes=\"danger\" Background=\"{DynamicResource CardBackgroundBrush}\" Foreground=\"White\"/>",
            "overridden"));

        // The HOVER key stays out, and not by accident: the pattern anchors the key's end, so a
        // longer name sharing the prefix does not match. Without this, a rename could quietly widen
        // the rule and nothing would say so.
        Assert.Empty(ScanForWhiteOnFilled(
            "<Button Background=\"{DynamicResource SystemErrorBackgroundHoverBrush}\" Foreground=\"White\"/>",
            "hover"));
    }

    [Fact]
    public void PlainWhiteWouldFailOnEVERYThemesSuccessFill()
    {
        // The control for the success token, and a stronger claim than the accent one: white fails on
        // green in all FOUR themes, not three. Without this the comment's numbers are asserted
        // nowhere and could drift from the themes they describe.
        var failures = Themes.Count(theme =>
            Contrast(Extract(File.ReadAllText(ThemePath(theme)), "<Color x:Key=\"SystemSuccess\">"), "#FFFFFFFF") < 4.5);

        Assert.Equal(Themes.Length, failures);
    }

    [Fact]
    public void TheErrorForegroundMeetsWcagAAAgainstItsOwnError()
    {
        // THE THIRD SURFACE, AND THE ONE THAT PROVES THE TOKENS MUST BE PER-SURFACE, not just
        // per-theme. Red does not follow the accent answer: on BaseDarkGlass the accent wants white
        // and red wants dark, on SolarFlare the accent wants dark and red wants white, and the two
        // tokens are identical in the other two themes - a coincidence, not a licence. Borrowing the
        // accent token fails AA on both of the themes where they differ, which is why RemEx-tq2e's
        // sweep had to be reverted.
        foreach (var theme in Themes)
        {
            var text = File.ReadAllText(ThemePath(theme));
            var error = Extract(text, "<Color x:Key=\"SystemError\">");
            var foreground = ExtractBrush(text, "ErrorForegroundBrush");

            var ratio = Contrast(error, foreground);
            Assert.True(ratio >= 4.5, $"{theme}: {foreground} on {error} is {ratio:F2}:1, below AA.");
        }
    }

    [Fact]
    public void EveryThemeDeclaresAnErrorForeground()
    {
        foreach (var theme in Themes)
        {
            Assert.Contains("ErrorForegroundBrush", File.ReadAllText(ThemePath(theme)));
        }
    }

    [Fact]
    public void BorrowingTheAccentTokenForREDWouldStillFailToday()
    {
        // THE CONTROL THAT WOULD HAVE CAUGHT THE ORIGINAL MISTAKE, rewritten after review showed the
        // first version was decorative. It asserted only that the two tokens DIFFER somewhere, which
        // is true today by coincidence and which no mutation could trip without tripping the AA test
        // first - and its comment claimed to check SolarFlare specifically while checking no theme in
        // particular.
        //
        // This asserts the thing that actually justifies a separate token: borrowing the accent one
        // is not merely a different value on red, it is an UNREADABLE one - 3.67:1 on BaseDarkGlass,
        // 3.81:1 on SolarFlare. If that ever stops being true, the split needs re-arguing rather than
        // silently keeping.
        var borrowWouldFail = Themes.Count(theme =>
        {
            var text = File.ReadAllText(ThemePath(theme));
            return Contrast(Extract(text, "<Color x:Key=\"SystemError\">"),
                            ExtractBrush(text, "AccentForegroundBrush")) < 4.5;
        });

        Assert.True(borrowWouldFail > 0,
            "AccentForegroundBrush now clears AA on every error fill, so a separate ErrorForegroundBrush "
                + "is no longer justified by measurement - re-measure before deleting or keeping it.");
    }

    // ── The TRANSLUCENT error surface (RemEx-1elh) ─────────────────────────────────────────────

    /// <summary>What 15% red over glass actually renders as, which is the only thing worth measuring.</summary>
    /// <remarks>
    /// EVERY OTHER TEST IN THIS FILE COMPARES A FOREGROUND TO A COLOUR KEY DIRECTLY, and doing that
    /// here would measure a surface nobody sees: <c>SystemErrorBackgroundBrush</c> is
    /// <c>SystemError</c> at <c>Opacity 0.15</c>, so the pixel behind the text is a blend, not the red.
    /// Comparing against the red is what makes white look wrong on the dark themes when it is right.
    /// </remarks>
    private static string Composite(string over, double alpha, string onto)
    {
        static int Channel(string hex, int i)
        {
            var h = hex.TrimStart('#');
            if (h.Length == 8) h = h[2..];
            return Convert.ToInt32(h.Substring(i * 2, 2), 16);
        }

        var blended = Enumerable.Range(0, 3)
            .Select(i => (int)Math.Round((Channel(over, i) * alpha) + (Channel(onto, i) * (1 - alpha))))
            .ToArray();

        return $"#{blended[0]:X2}{blended[1]:X2}{blended[2]:X2}";
    }

    /// <summary>
    /// Reads the tint strength out of the theme rather than hardcoding 0.15.
    /// </summary>
    /// <remarks>
    /// A LITERAL HERE WOULD GO STALE SILENTLY. Someone deepening the tint to make the button read
    /// better darkens the surface under the text, which is exactly when this guard should speak up —
    /// and a hardcoded 0.15 would keep measuring the old surface and stay green through it.
    /// </remarks>
    private static double TintOpacity(string text, string key)
    {
        var match = Regex.Match(text, $"x:Key=\"{key}\"[^/]*?Opacity=\"([0-9.]+)\"");
        Assert.True(match.Success, $"missing Opacity on {key}");
        return double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    [Fact]
    public void EveryThemeDeclaresAnErrorTintForeground()
    {
        foreach (var theme in Themes)
        {
            Assert.Contains("ErrorTintForegroundBrush", File.ReadAllText(ThemePath(theme)));
        }
    }

    [Fact]
    public void TheErrorTintForegroundMeetsWcagAAOverTheCompositeAtRestAndOnHover()
    {
        // BOTH STATES, AND THE HOVER ONE IS NOT CEREMONIAL. The bead's own analysis measured only the
        // resting 0.15 tint, and red-700 #B91C1C clears AA there at 4.70:1 — then falls to 4.19:1 once
        // the pointer deepens the tint to 0.22. A value chosen against the rest state alone would have
        // shipped a button that fails the moment you reach for it.
        foreach (var theme in Themes)
        {
            var text = File.ReadAllText(ThemePath(theme));
            var error = Extract(text, "<Color x:Key=\"SystemError\">");
            var glass = Extract(text, "<Color x:Key=\"GlassBaseMedium\">");
            var foreground = ExtractBrush(text, "ErrorTintForegroundBrush");

            foreach (var key in new[] { "SystemErrorBackgroundBrush", "SystemErrorBackgroundHoverBrush" })
            {
                var surface = Composite(error, TintOpacity(text, key), glass);
                var ratio = Contrast(surface, foreground);
                Assert.True(ratio >= 4.5,
                    $"{theme}/{key}: {foreground} on composite {surface} is {ratio:F2}:1, below AA.");
            }
        }
    }

    [Fact]
    public void TheTintFilledButtonActuallyUsesTheTintToken()
    {
        // FOUND BY INJECTION, AND NOTHING ELSE HERE COVERS IT. Every other test in this group proves
        // the token EXISTS and is MEASURED; the scan proves no white LITERAL sits on the surface. None
        // of that stops the view from pointing at a different DynamicResource — swapping this style
        // back to SystemErrorBrush, which the test above proves fails AA on all four themes, left the
        // whole file green. A guard that cannot see the regression it was written for is decoration.
        var canvas = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "remex.desktop", "Views", "CanvasView.axaml"));

        var style = Regex.Match(
            canvas,
            "<Style Selector=\"Button\\.card-action-danger\">.*?</Style>",
            RegexOptions.Singleline);
        Assert.True(style.Success, "the card-action-danger style moved or was renamed");

        Assert.Matches(
            "Property=\"Foreground\" Value=\"\\{DynamicResource ErrorTintForegroundBrush\\}\"",
            style.Value);
    }

    [Fact]
    public void TheSelectedCommandPaletteRowTakesTheAccentForeground()
    {
        // THE SCAN CANNOT SEE THIS ONE AND IS NOT BEING ASKED TO (RemEx-o9gd). ScanForWhiteOnFilled
        // hunts a white LITERAL; this defect was a light TOKEN — TextPrimaryBrush and
        // TextSecondaryBrush — on a descendant of a row filled with the accent. Measured against each
        // theme's own accent that is 4.88 / 1.31 / 3.65 / 10.29 and 1.99 / 1.37 / 1.12 / 5.97, and
        // CyberNOC's 1.31 is worse than the 1.38 headline RemEx-iegl was raised to fix. Generalising
        // the scan to "any token that might be light on any surface that might be filled" is a much
        // larger and much guessier rule; pinning the one place it went wrong is honest and cheap.
        var palette = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "remex.desktop", "Views", "CommandPaletteWindow.axaml"));

        foreach (var target in new[] { "palette-label", "palette-category" })
        {
            var style = Regex.Match(
                palette,
                $"<Style Selector=\"ListBoxItem:selected TextBlock\\.{target}\">.*?</Style>",
                RegexOptions.Singleline);

            Assert.True(style.Success, $"the selected-row style for {target} moved or was removed");
            Assert.Matches(
                "Property=\"Foreground\" Value=\"\\{DynamicResource AccentForegroundBrush\\}\"",
                style.Value);
        }

        // AND THE LABELS MUST NOT SET IT INLINE, because a local value beats a style setter in
        // Avalonia — leaving the Foreground on the TextBlock would make both rules above inert while
        // every assertion here still passed.
        var template = Regex.Match(palette, "<DataTemplate.*?</DataTemplate>", RegexOptions.Singleline);
        Assert.True(template.Success, "the palette item template moved");
        Assert.DoesNotContain("Foreground=", template.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void PlainWhiteWouldFailOnExactlyOneThemesErrorTint()
    {
        // THE CONTROL, AND IT INVERTS THE SOLID FILL'S — which is the entire reason a fourth token
        // exists rather than a reuse of the third. On solid red, white fails everywhere but SolarFlare.
        // On the tint, white fails ONLY on SolarFlare, because its GlassBaseMedium is opaque and light
        // while the other three are dark. Same colour, opposite answer, one theme apart.
        var failures = Themes.Count(theme =>
        {
            var text = File.ReadAllText(ThemePath(theme));
            var surface = Composite(
                Extract(text, "<Color x:Key=\"SystemError\">"),
                TintOpacity(text, "SystemErrorBackgroundBrush"),
                Extract(text, "<Color x:Key=\"GlassBaseMedium\">"));
            return Contrast(surface, "#FFFFFFFF") < 4.5;
        });

        // AGAINST THE LIST RATHER THAN A LITERAL 1, which is the convention this file argues for twice
        // above and which the first version of this test broke. A fifth theme — a second light one,
        // added correctly with its own measured token — would take this to 2 and read as a contrast
        // regression when it is only a longer list. What is worth pinning is that white fails
        // SOMEWHERE and not everywhere: that is the inversion against the solid fill, where white
        // fails on every theme but SolarFlare.
        Assert.InRange(failures, 1, Themes.Length - 1);
    }

    [Fact]
    public void NeitherExistingErrorTokenWouldWorkOnTheTint()
    {
        // WHY A FOURTH TOKEN IS JUSTIFIED, asserted rather than claimed in a comment. If either of
        // these ever clears AA on every theme's tint, the extra token stops being earned and the split
        // should be re-argued — not kept because it is already there.
        // SystemErrorBrush IS IN THIS LIST BECAUSE REVIEW CAUGHT ITS ABSENCE. The style comment in
        // CanvasView rejects it with four measured numbers, and those were the only figures this change
        // introduced that nothing asserted — which is exactly how that comment came to claim it "fails
        // on two themes" when all four of 4.35 / 4.31 / 3.48 / 3.51 are below 4.5.
        //
        // IT IS READ FROM THE COLOUR KEY, NOT THE BRUSH. SystemErrorBrush is declared as
        // Color="{DynamicResource SystemError}" with no literal on the line, so ExtractBrush's
        // "first # after the key" would walk on to whatever colour is declared next and measure it
        // instead — silently, and against a value from an unrelated token.
        var borrowedTokens = new (string Name, Func<string, string> Read)[]
        {
            ("ErrorForegroundBrush", text => ExtractBrush(text, "ErrorForegroundBrush")),
            ("AccentForegroundBrush", text => ExtractBrush(text, "AccentForegroundBrush")),
            ("SystemErrorBrush", text => Extract(text, "<Color x:Key=\"SystemError\">")),
        };

        foreach (var (name, read) in borrowedTokens)
        {
            var wouldFail = Themes.Count(theme =>
            {
                var text = File.ReadAllText(ThemePath(theme));
                var surface = Composite(
                    Extract(text, "<Color x:Key=\"SystemError\">"),
                    TintOpacity(text, "SystemErrorBackgroundBrush"),
                    Extract(text, "<Color x:Key=\"GlassBaseMedium\">"));
                return Contrast(surface, read(text)) < 4.5;
            });

            Assert.True(wouldFail > 0,
                $"{name} now clears AA on every error tint, so ErrorTintForegroundBrush is no "
                    + "longer justified by measurement — re-measure before keeping it.");
        }
    }

    [Fact]
    public void PlainWhiteWouldFailOnEveryErrorFillButOne()
    {
        // The control this surface was missing, and the file's own pattern: without it, the 3.41 /
        // 3.67 / 3.88 / 4.83 quoted in four theme comments and the changelog are asserted NOWHERE and
        // can drift from the themes they describe.
        var failures = Themes.Count(theme =>
            Contrast(Extract(File.ReadAllText(ThemePath(theme)), "<Color x:Key=\"SystemError\">"), "#FFFFFFFF") < 4.5);

        // Against the list, not 3. White passes on exactly one theme, SolarFlare, at 4.83:1 - which
        // is why that theme alone keeps white.
        Assert.Equal(Themes.Length - 1, failures);
    }

}
