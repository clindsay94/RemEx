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

    /// <summary>
    /// The brushes that paint the .primary look (App.axaml ":is(Button).primary"). A button that
    /// sets Background or Foreground to either one, on its own account, reads as a visual primary
    /// no matter what its Classes attribute says — used by
    /// <see cref="AtMostOnePrimaryButtonPerViewSurface"/> and
    /// <see cref="NoButtonOverridesAClassOwnedPaintPropertyUnlessListed"/> (RemEx-cgrv3).
    /// </summary>
    private static readonly string[] AccentPaintValues =
    {
        "{DynamicResource AccentPrimaryBrush}",
        "{DynamicResource AccentForegroundBrush}",
    };

    /// <summary>The four properties App.axaml's emphasis/tint/size classes own. An inline value on
    /// any of these, on a button wearing an emphasis class, makes that class inert on the property
    /// — see <see cref="NoButtonOverridesAClassOwnedPaintPropertyUnlessListed"/> (RemEx-cgrv3).</summary>
    private static readonly string[] PaintOwningProperties = { "Background", "Foreground", "CornerRadius", "Padding" };

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
        // The nine nav DESTINATIONS moved to ListBoxItem under RemEx-zi3ua, so nav-item-active
        // (their old Classes.nav-item-active toggle) is gone entirely — retired here rather than
        // widened, which is the whole point of an exception list that gets checked. "nav-item"
        // itself stays: the drawer's round brand-mark toggle Button still wears it.
        ["nav-item"] = "RemEx-zi3ua — retained for the drawer-toggle Button after nav destinations became a Material list",
        // "gear-fab" (RemEx-bado6) is RETIRED, not widened: the gear is a material:FloatingButton
        // now, not a Button, so the regexes below — scoped to Button/ToggleButton/RepeatButton/
        // DropDownButton/SplitButton tags — can no longer even see a Classes="gear-fab" if one
        // existed. Keeping the entry would have been an exception with nothing left to except.
        ["alert-bell"] = "RemEx-8wpvr.4 — CanvasView's per-card alert bell; scopes the Kind/Foreground "
            + "styles (Button.alert-bell mi|MaterialIcon / .tripped) to that one button rather than "
            + "any icon-button in the app",
    };

    /// <summary>
    /// Buttons exempt from carrying an emphasis class because they hand-roll their own paint
    /// inline, listed by name rather than by class since the exemption is that one button's inline
    /// paint, not something reusable. Bound to docs/BUTTON-VOCABULARY.md the same way
    /// <see cref="Exceptions"/> is, by <c>TheExceptionListMatchesTheDocumentation</c>.
    /// </summary>
    private static readonly Dictionary<string, string> IndividualEmphasisExceptions = new()
    {
        ["Name=\"DrawerToggle\""] = "RemEx-z7pnx.1 — ShellView's app-bar drawer-toggle hand-rolls "
            + "its own paint (inline Background=\"Transparent\" BorderThickness=\"0\") alongside "
            + "icon-button compact instead of an emphasis class; per the vocabulary, icon-button is "
            + "meant to sit alongside tertiary, so the real fix is `tertiary icon-button compact` "
            + "with the inline Background/BorderThickness dropped. Deferred: that restyles the "
            + "app's most-visible chrome control, which is out of scope for the fix that added this "
            + "exception",
    };

    [Fact]
    public void EveryButtonClassInUseIsInTheVocabularyOrIsADocumentedException()
    {
        // THE ANTI-SPRAWL RULE. A new screen-named class is not an error — it compiles, it
        // renders, and it looks fine on the one screen its author was looking at. This is the
        // only place that notices.
        //
        // KNOWN GAP (noted, not fixed, during RemEx-zi3ua's review): the tag list below only
        // matches Button and its WPF-style siblings, so a screen-named class on a ListBoxItem —
        // the nav rail's own ".nav-item" among them — is invisible to this rule. Not introduced by
        // this bead and not a loosening of anything this rule used to catch; recorded here so a
        // future pass knows the vocabulary's coverage stops at Button-shaped controls.
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
    public void EveryButtonDeclaresAClass()
    {
        // THE ACCEPTANCE CRITERION RemEx-z7pnx.1 WAS FILED FOR. A Button with no Classes
        // attribute at all falls through to Material's default ControlTheme, which is
        // registered on {x:Type Button} and renders as a RAISED ACCENT-FILLED button with a
        // Depth1 shadow - "no class" was never "no style", it was "primary" by accident.
        // Every button in the app now picks one on purpose; WindowChrome.axaml's
        // minimise/maximise/close are template parts of the window chrome, not app buttons,
        // and are the only exemption (docs/BUTTON-VOCABULARY.md).
        var offenders = new List<string>();

        foreach (var (file, text) in XamlFiles())
        {
            // Matched by repo-relative PATH, not bare filename (Opus review round 2, LOW): a
            // same-named WindowChrome.axaml dropped anywhere else in the tree would otherwise be
            // silently exempted too.
            if (RepoRelativeViewPath(file) == "Themes/Chrome/WindowChrome.axaml")
            {
                continue;
            }

            // The tag name must be followed by whitespace, '>' or '/' - never '.', or this
            // would match a property-element tag like <Button.Flyout> instead of a real Button.
            foreach (Match match in Regex.Matches(
                         text, @"<(?:Button|ToggleButton|RepeatButton|DropDownButton|SplitButton)(?=[\s>/])([^>]*?)/?>"))
            {
                if (Regex.IsMatch(match.Groups[1].Value, @"\bClasses\s*="))
                {
                    continue;
                }

                offenders.Add(Path.GetFileName(file));
            }
        }

        offenders.Should().BeEmpty(
            "every Button (and its WPF-style siblings) needs a Classes attribute or it renders "
            + "as Material's default raised primary by accident; WindowChrome.axaml's template "
            + "parts are the only exemption (RemEx-z7pnx.1)");
    }

    [Fact]
    public void EveryButtonDeclaresAnEmphasisOrIsAListedException()
    {
        // THE LOWER BOUND. EveryButtonDeclaresAClass only checks that SOME Classes attribute is
        // present; it never checks that the attribute carries an EMPHASIS. A Button wearing only
        // geometry/modifier classes like "compact" still falls through to Material's default
        // {x:Type Button} ControlTheme and renders raised and accent-filled - an accidental
        // primary that "has a class" and still looks exactly like the bug EveryButtonDeclaresAClass
        // exists to catch. PersonalizationPanelView had exactly this: nine buttons, every one
        // Classes="compact", none of the three emphases - unnoticed because nothing asserted a
        // *lower* bound the way AtMostOnePrimaryButtonPerViewSurface asserts an upper one.
        //
        // Three groups legitimately never reach the default ControlTheme, so they are exempt:
        //   - THEMED: tile/card/swatch/nav-item are painted by something other than the default
        //     ControlTheme (App.axaml styles, ShellView's listed .nav-item exception, or inline
        //     per-usage for swatch) rather than by one bespoke ControlTheme shared across all four.
        //   - INDIVIDUAL: a button that hand-rolls its own paint inline (listed by name, not class,
        //     because the exemption is the inline paint, not something reusable).
        //   - CHROME: WindowChrome.axaml's template parts, skipped the same way
        //     EveryButtonDeclaresAClass skips them.
        var themedExemptClasses = new[] { "tile", "card", "swatch", "nav-item" };
        var individualExceptions = IndividualEmphasisExceptions;

        var offenders = new List<string>();
        var themedSeen = new HashSet<string>();
        var individualSeen = new HashSet<string>();

        foreach (var (file, text) in XamlFiles())
        {
            if (RepoRelativeViewPath(file) == "Themes/Chrome/WindowChrome.axaml")
            {
                continue;
            }

            foreach (Match match in Regex.Matches(
                         text, @"<(?:Button|ToggleButton|RepeatButton|DropDownButton|SplitButton)\b[^>]*?\bClasses=""([^""]*)""[^>]*>"))
            {
                var classes = match.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (classes.Any(Emphasis.Contains))
                {
                    continue;
                }

                var themedClass = themedExemptClasses.FirstOrDefault(classes.Contains);
                if (themedClass is not null)
                {
                    themedSeen.Add(themedClass);
                    continue;
                }

                var exemption = individualExceptions.Keys.FirstOrDefault(
                    key => match.Value.Contains(key, StringComparison.Ordinal));
                if (exemption is not null)
                {
                    individualSeen.Add(exemption);
                    continue;
                }

                offenders.Add($"{Path.GetFileName(file)}: Classes=\"{match.Groups[1].Value}\"");
            }
        }

        // ANTI-VACUITY: every listed exception - themed class or individual button - has to
        // actually match a real no-emphasis button, or the allow-list is silently protecting
        // nothing while looking exhaustive.
        var unmatchedThemed = themedExemptClasses.Except(themedSeen).ToList();
        unmatchedThemed.Should().BeEmpty(
            "every themed exemption has to match a real button with no emphasis, or the allow-list "
            + "is stale: " + string.Join(", ", unmatchedThemed));

        var unmatchedIndividual = individualExceptions.Keys.Except(individualSeen).ToList();
        unmatchedIndividual.Should().BeEmpty(
            "every EveryButtonDeclaresAnEmphasisOrIsAListedException exception has to match a real "
            + "button, or the allow-list is stale: " + string.Join(", ", unmatchedIndividual));

        offenders.Should().BeEmpty(
            "every button needs primary, secondary or tertiary - or a listed reason it never "
            + "reaches Material's default ControlTheme - or it renders as an accidental raised "
            + "primary; this is exactly how PersonalizationPanelView's nine compact-only buttons "
            + "went unnoticed");
    }

    [Fact]
    public void AtMostOnePrimaryButtonPerViewSurface()
    {
        // Opus review round 2 (HIGH), RemEx-z7pnx.1: SettingsView had FOUR buttons wearing
        // "primary" on one scrolling page - Save, Save & Reconnect, Export Settings, Add Shared
        // Folder - the exact "screen with six primaries" outcome the vocabulary forbids. Three
        // are "secondary" now; this guards against it coming back. The unit here is the FILE,
        // the coarsest thing a regex scan can reason about - finer-grained cards and mutually
        // exclusive states need documented help below, same anti-sprawl discipline as Exceptions.
        var perFilePrimaryExceptions = new Dictionary<string, string>
        {
            ["Presence.IsHostDown"] = "ShellView - HostDown/HasNoPhone/HasPhone are mutually exclusive Presence states, never two visible together",
            ["Presence.HasNoPhone"] = "ShellView - see the Presence.IsHostDown entry",
            ["Presence.HasPhone"] = "ShellView - see the Presence.IsHostDown entry",
            ["IsVisible=\"{Binding !IsTutorialLastPage}\""] = "ShellView - Tutorial_Next/Tutorial_Finish are mutually exclusive by IsTutorialLastPage. Was keyed off TutorialPageIndex + IntNotEqual/IntEqualConverter until RemEx-qgql; those compared a RAW page index against a hardcoded 16, which is the defect that bead fixed. Do not restore the converter keys - a raw index is not the filtered position, and they disagree exactly on the platforms that hide a page",
            ["IsVisible=\"{Binding IsTutorialLastPage}\""] = "ShellView - see the !IsTutorialLastPage entry",
            ["x:Name=\"ActionButton\""] = "DialogContent - Classes=\"primary\" is only the XAML default; the constructor clears it and applies the caller's own classes, and Cancel is always \"secondary\" (RemEx-z7pnx.1)",

            ["Dashboard_Connect"] = "CanvasView Actions card - Connect/CancelConnect/Disconnect are mutually exclusive by connection state",
            ["Canvas_CancelConnect"] = "CanvasView Actions card - see the Dashboard_Connect entry",
            ["Canvas_Disconnect"] = "CanvasView Actions card - see the Dashboard_Connect entry",
            ["Canvas_ActionDone"] = "CanvasView - the floating selection toolbar is a different surface from the dashboard cards",
            ["Canvas_CoachGotIt"] = "CanvasView - the coach-mark overlay is a different surface from the dashboard cards",
            ["Canvas_AddCard"] = "CanvasView - a per-staging-item template button, a different surface per card",

            ["FileTransfer_CreateFolderConfirmBtn"] = "FileTransferView - mutually exclusive by IsCreatingFolder",
            ["FileTransfer_RenameConfirmBtn"] = "FileTransferView - mutually exclusive by IsRenaming",

            ["About_ViewGitHub"] = "AboutView - the GitHub card is a different surface from the Software Update card",

            ["Home_InitializeSensors"] = "HomeView - the Sensors HUD empty-state card is a different surface from the phone-link card",
            ["Home_InitializeLink"] = "HomeView - the phone-link card is a different surface from the Sensors HUD card",
        };

        var byFile = new Dictionary<string, List<string>>();
        var exemptedSeen = new HashSet<string>();

        foreach (var (file, text) in XamlFiles())
        {
            var primaries = new List<string>();

            foreach (Match match in Regex.Matches(
                         text, @"<(?:Button|ToggleButton|RepeatButton|DropDownButton|SplitButton)\b[^>]*?\bClasses=""([^""]+)""[^>]*>"))
            {
                var classes = match.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                // COUNTS PAINT, NOT JUST THE CLASS (RemEx-cgrv3). ShellView's Grid.Column="3"
                // banner button wore Classes="secondary" and shipped for real: LocalValue
                // Background/Foreground set to the exact primary-accent brushes, a second visual
                // primary sitting beside a real .primary.success button, invisible to a check that
                // only reads classes.Contains("primary"). A button that paints itself with either
                // accent-primary brush counts here whether or not it says "primary".
                var isPrimary = classes.Contains("primary") || AccentPaintValues.Any(v =>
                    Regex.IsMatch(match.Value, $@"\b(?:Background|Foreground)=""{Regex.Escape(v)}"""));
                if (!isPrimary)
                {
                    continue;
                }

                var exemption = perFilePrimaryExceptions.Keys.FirstOrDefault(
                    key => match.Value.Contains(key, StringComparison.Ordinal));
                if (exemption is not null)
                {
                    exemptedSeen.Add(exemption);
                    continue;
                }

                primaries.Add(match.Value);
            }

            if (primaries.Count > 0)
            {
                byFile[file] = primaries;
            }
        }

        // ANTI-VACUITY: every exception has to actually match a real button, or the list is
        // quietly protecting nothing while still looking exhaustive.
        var unmatchedExceptions = perFilePrimaryExceptions.Keys.Except(exemptedSeen).ToList();
        unmatchedExceptions.Should().BeEmpty(
            "every AtMostOnePrimaryButtonPerViewSurface exception has to match a real button, or "
            + "the allow-list is stale: " + string.Join(", ", unmatchedExceptions));

        var offenders = byFile
            .Where(kv => kv.Value.Count > 1)
            .Select(kv => $"{Path.GetFileName(kv.Key)}: {kv.Value.Count} unlisted primaries")
            .ToList();

        offenders.Should().BeEmpty(
            "at most one .primary belongs on a surface at a time; an extra one needs a listed "
            + "reason above, not just presence - that is how SettingsView ended up with four");
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
    public void NoButtonOverridesAClassOwnedPaintPropertyUnlessListed()
    {
        // THE GUARD THIS WHOLE SWEEP EXISTS FOR (RemEx-cgrv3, review of PR #55). Every other test
        // in this file checks CLASSES. That is exactly why ShellView's Grid.Column="3" button
        // shipped for real: it wore Classes="secondary" — correct, and every test above was happy
        // — while LocalValue Background/Foreground painted it with the primary-accent brushes
        // beside a real .primary.success button in the same banner. Avalonia LocalValue outranks
        // Style and StyleTrigger regardless of declaration order, so an inline Background,
        // Foreground, CornerRadius or Padding on a button wearing an emphasis class makes that
        // class inert on the property it sets — whether or not the inline value happens to match
        // what the class would have supplied. 86 buttons across 11 views had done this by the time
        // PR #55 was reviewed. This test fails on the OVERRIDE, not on whether it currently paints
        // something wrong, because "currently harmless" is exactly how the 86 accumulated: each one
        // looked fine in isolation on the screen its author was looking at.
        //
        // A call site that genuinely needs to differ is listed below, by a substring unique enough
        // to identify that one button, with the reason it differs — the same discipline as
        // <see cref="Exceptions"/> and <see cref="IndividualEmphasisExceptions"/> above, just not
        // wired to docs/BUTTON-VOCABULARY.md (that file is out of this bead's owned paths).
        var inlinePaintOverrideExceptions = new Dictionary<string, string>
        {
            // ── Geometry tied to the control's own dimensions, not a copy of a class default ──
            ["Command=\"{Binding ReplayCoachMarkCommand}\""] = "CanvasView coach-mark replay button — CornerRadius=17/Padding=0 make it a circle exactly matching its own Width/Height=34, not a CornerRadiusSmall copy",

            // ── Non-interactive control abusing Button as a label / hand-rolled chrome ──
            ["Classes=\"secondary\" IsHitTestVisible=\"False\""] = "CanvasView toolbar title — IsHitTestVisible=\"False\", not a real secondary surface",
            ["Name=\"ConnectionStatusButton\""] = "ShellView collapsed-drawer status row — deliberately wider geometry than a card button, not drift",

            // ── Deliberate off-default fill/text for context (flyout chrome, toolbar tone) ──
            ["Command=\"{Binding NavigateToDiagnosticLogsCommand}\""] = "ShellView status flyout — reads as flyout chrome (matches the flyout's own GlassBaseDarkBrush), not a card surface",
            ["Command=\"{Binding Connection.SendPingCommand}\""] = "CanvasView dashboard actions — lighter-than-resting-secondary tone against the canvas backdrop",
            ["Command=\"{Binding PinSelectedCommand}\""] = "CanvasView selection toolbar — same lighter tone as the Ping button above it",
            ["Command=\"{Binding ResetToDefaultCommand}\""] = "PersonalizationPanelView reset link — quieter than .tertiary's default TextSecondaryBrush on purpose",
            ["Command=\"{Binding Connection.GenerateQrCodeCommand}\""] = "SettingsView pair-phone button — muted TextSecondaryBrush instead of the default TextPrimaryBrush",

            // ── The compact flyout/banner action-row size (CornerRadius=6), documented once here
            //    rather than per button — every one below is the same intentional exception ──
            ["Command=\"{Binding Connection.ConnectCommand}\" IsVisible=\"{Binding Presence.IsHostDown}\""] = "ShellView flyout Connect — compact action-row CornerRadius=6 (RemEx-cgrv3)",
            ["Command=\"{Binding NavigateToSettingsCommand}\" IsVisible=\"{Binding Presence.HasNoPhone}\""] = "ShellView flyout Pair — same compact action-row exception",
            ["Command=\"{Binding NavigateToSettingsCommand}\" IsVisible=\"{Binding Presence.HasPhone}\""] = "ShellView flyout Settings — same compact action-row exception",
            ["Grid.Column=\"2\" Classes=\"primary success compact\""] = "ShellView connection banner Connect — same compact action-row exception",
            ["Grid.Column=\"3\" Classes=\"secondary compact\""] = "ShellView connection banner Settings — same compact action-row exception",
            ["Command=\"{Binding DismissConnectionBannerCommand}\""] = "ShellView connection banner dismiss — icon-button.compact's zero Padding doesn't fit this text-free chip; CornerRadius=6 matches the row",
            ["Command=\"{Binding DismissLayoutLoadWarningCommand}\""] = "ShellView layout-warning dismiss — same icon-button exception as the connection banner dismiss",
            ["Command=\"{Binding ConfirmCustomAccentCommand}\""] = "PersonalizationPanelView Apply — same compact action-row CornerRadius=6 exception",
            ["Command=\"{Binding $parent[UserControl].((vm:SettingsViewModel)DataContext).ApplyPairedDeviceRenameCommand}\""] = "SettingsView paired-device rename — same compact-row CornerRadius=6 exception, smaller row-level Padding",
            ["Command=\"{Binding $parent[UserControl].((vm:SettingsViewModel)DataContext).UnpairDeviceCommand}\""] = "SettingsView paired-device unpair — same compact-row CornerRadius=6 exception",

            // ── Close/dismiss buttons whose Padding intentionally isn't icon-button's zero ──
            ["Name=\"SettingsSheetCloseButton\""] = "ShellView settings sheet close — Padding=8 gives the close icon room; icon-button's default is 0",
            ["Command=\"{Binding CloseQrCodeCommand}\""] = "PairingQrPanelView close — same roomier-than-icon-button-default Padding",
            ["Command=\"{Binding ClosePairingPinCommand}\""] = "PairingPinPanelView close — same roomier-than-icon-button-default Padding",

            // ── Context-specific CTA/chip sizing that never matched a class default (each is its
            //    own documented call, not a shared pattern) ──
            ["Command=\"{Binding Shell.OpenCommandPaletteCommand}\""] = "HomeView command-palette launcher — wide search-bar-styled CTA",
            ["Command=\"{Binding NavigateToCanvasCommand}\" Padding=\"16,8\""] = "HomeView open-workspace chip",
            ["Command=\"{Binding NavigateToCanvasCommand}\" HorizontalAlignment=\"Center\" Padding=\"24,12\""] = "HomeView empty-state hero CTA",
            ["Command=\"{Binding ClearActivityCommand}\""] = "HomeView clear-activity chip",
            ["Command=\"{Binding Connection.ConnectCommand}\" IsVisible=\"{Binding !Connection.IsConnected}\""] = "HomeView initialize-link CTA",
            ["Classes=\"secondary pill danger\""] = "HomeView terminate-link CTA — matches the InitializeLink CTA's Padding",
            ["Command=\"{Binding StartStreamCommand}\""] = "RemoteDesktopView start-stream toolbar button",
            ["Command=\"{Binding StopStreamCommand}\""] = "RemoteDesktopView stop-stream toolbar button",
            ["Command=\"{Binding ApplySettingsCommand}\""] = "RemoteDesktopView apply-settings toolbar button",
            ["Command=\"{Binding DiscoverHostsCommand}\""] = "ConnectionView discover-hosts button",
            ["Command=\"{Binding ConnectCommand}\" IsVisible=\"{Binding !IsConnected}\""] = "ConnectionView connect CTA",
            ["Command=\"{Binding DisconnectCommand}\" IsVisible=\"{Binding IsConnected}\""] = "ConnectionView disconnect CTA",
            ["Command=\"{Binding GenerateQrCodeCommand}\" IsVisible=\"{Binding CanRevealPairingPin}\""] = "ConnectionView pair-phone button",
            ["Command=\"{Binding GenerateQrCodeCommand}\" HorizontalAlignment=\"Center\""] = "PairingPinPanelView expired-PIN get-new button",
            ["Click=\"OnCopySnapshot\""] = "CanvasView copy-snapshot toolbar button",
            ["Command=\"{Binding SaveCommand}\""] = "SettingsView header Save CTA",
            ["Command=\"{Binding DiscoverHostCommand}\""] = "SettingsView discover-host button",
            ["Command=\"{Binding SaveAndReconnectCommand}\""] = "SettingsView save-and-reconnect button",
            ["Content=\"{local:Localize Btn_Disconnect}\""] = "SettingsView connection-card disconnect button",
            ["Command=\"{Binding ExitApplicationCommand}\""] = "SettingsView exit-app button",
            ["Command=\"{Binding ExportSettingsCommand}\""] = "SettingsView export-settings button",
            ["Command=\"{Binding ImportSettingsCommand}\""] = "SettingsView import-settings button",
            ["Command=\"{Binding AddSharedFolderCommand}\""] = "SettingsView add-shared-folder button",
            ["Command=\"{Binding RestoreDefaultSharedFoldersCommand}\""] = "SettingsView restore-default-folders button",
            ["Command=\"{Binding RemoveCommand}\""] = "SettingsView shared-folder-row remove button",
            ["Command=\"{Binding RefreshPairedDeviceListCommand}\""] = "SettingsView paired-devices refresh button",
            ["Command=\"{Binding RefreshTrustedDevicesCommand}\""] = "SettingsView trusted-devices refresh button",
            ["Command=\"{Binding RevokeCommand}\""] = "SettingsView trust-row revoke button",
            ["Command=\"{Binding ReplayTutorialCommand}\""] = "SettingsView replay-tutorial button",
            ["Command=\"{Binding $parent[UserControl].((vm:AppLauncherViewModel)DataContext).LaunchAppCommand}\""] = "AppLauncherView tile launch button — Padding=12 gives the per-app art room; the tile's own Card supplies the colour",
        };

        var offenders = new List<string>();
        var exemptedSeen = new HashSet<string>();

        foreach (var (file, text) in XamlFiles())
        {
            var fileName = Path.GetFileName(file);

            foreach (Match match in Regex.Matches(
                         text, @"<(?:Button|ToggleButton|RepeatButton|DropDownButton|SplitButton)\b[^>]*?\bClasses=""([^""]+)""[^>]*>"))
            {
                var classes = match.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (!classes.Any(Emphasis.Contains))
                {
                    continue;
                }

                var overriddenProps = PaintOwningProperties
                    .Where(p => Regex.IsMatch(match.Value, $@"\b{p}="""))
                    .ToList();
                if (overriddenProps.Count == 0)
                {
                    continue;
                }

                var exemption = inlinePaintOverrideExceptions.Keys.FirstOrDefault(
                    key => match.Value.Contains(key, StringComparison.Ordinal));
                if (exemption is not null)
                {
                    exemptedSeen.Add(exemption);
                    continue;
                }

                offenders.Add($"{fileName}: {string.Join(",", overriddenProps)} inline on "
                    + $"Classes=\"{match.Groups[1].Value}\"");
            }
        }

        // ANTI-VACUITY: every listed exception has to match a real offending button, or the
        // allow-list is stale and silently protecting nothing (same discipline as the exception
        // lists above).
        var unmatched = inlinePaintOverrideExceptions.Keys.Except(exemptedSeen).ToList();
        unmatched.Should().BeEmpty(
            "every NoButtonOverridesAClassOwnedPaintPropertyUnlessListed exception has to match a "
            + "real button, or the allow-list is stale: " + string.Join(", ", unmatched));

        offenders.Should().BeEmpty(
            "Background/Foreground/CornerRadius/Padding are owned by the emphasis/tint/size "
            + "classes once a button wears one; an inline value on any of them makes the class "
            + "inert on that property regardless of whether the value matches, and unnoticed "
            + "overrides are exactly how RemEx-cgrv3's 86-button sweep happened. A new one needs "
            + "a class, or a listed, bead-owned reason.");
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

        // Same discipline for the individual (by-name) exceptions from
        // EveryButtonDeclaresAnEmphasisOrIsAListedException: an unbound allow-list drifts from the
        // doc exactly the way the class-keyed one used to.
        foreach (var (buttonKey, reason) in IndividualEmphasisExceptions)
        {
            doc.Should().Contain(buttonKey,
                $"docs/BUTTON-VOCABULARY.md has to list the {buttonKey} exception");

            var bead = Regex.Match(reason, @"RemEx-[a-z0-9.]+").Value;
            bead.Should().NotBeEmpty($"the {buttonKey} exception has to name the bead that owns it");
            doc.Should().Contain(bead,
                $"the doc has to name {bead} as the owner of the {buttonKey} exception");
        }
    }

    // ─────────────────────────── plumbing ───────────────────────────

    private static IEnumerable<(string File, string Text)> XamlFiles()
        => Directory
            .EnumerateFiles(Path.Combine(RepoRoot(), "remex.desktop"), "*.axaml", SearchOption.AllDirectories)
            .Select(file => (file, StripXmlComments(File.ReadAllText(file))));

    /// <summary>
    /// Removes <c>&lt;!-- ... --&gt;</c> blocks before any regex scan (Opus review round 2, LOW):
    /// without this, a commented-out Button could either satisfy a guard that should have failed
    /// on the real markup, or trip one that should have ignored dead prose. Every fact in this
    /// file reads through XamlFiles(), so the fix is centralised here rather than per-test.
    /// </summary>
    private static string StripXmlComments(string xaml)
        => Regex.Replace(xaml, "<!--.*?-->", string.Empty, RegexOptions.Singleline);

    /// <summary>The view file's path relative to remex.desktop, with forward slashes, for exact
    /// path comparisons instead of a bare filename that a same-named file elsewhere would also
    /// satisfy.</summary>
    private static string RepoRelativeViewPath(string file)
        => Path.GetRelativePath(Path.Combine(RepoRoot(), "remex.desktop"), file).Replace('\\', '/');

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
