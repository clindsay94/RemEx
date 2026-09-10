using System;
using System.IO;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Guards PairingDialog after RemEx-leatt moved it off <c>Border</c> onto Material.Styles'
/// <c>Card</c>, matching HomeView/SettingsView/ConfirmationDialog.
/// </summary>
/// <remarks>
/// <para>
/// Source scan, not a rendering test — there is no headless Avalonia harness in this suite
/// (<see cref="CardSurfaceTests"/>, <see cref="ShellSettingsSideSheetTests"/>). A regression to
/// <c>Border</c> or a literal Background/CornerRadius still compiles and still renders
/// something, so the only place to catch it is the markup that declares it.
/// </para>
/// <para>
/// RemEx-leatt is presentation-only: PairingDialogViewModel and ConnectionViewModel are untouched,
/// and the pinned bindings/classes below (TextFieldAssist.Hints, Classes.invalid, the
/// IsIndeterminate ProgressBar, the primary/secondary buttons, the Escape KeyBinding) are the
/// contract TextInputTests, DialogsDismissOnEscapeTests and ProgressIndicatorTests already pin —
/// repeated here so a revert to the pre-leatt markup fails locally, not only in those other files.
/// </para>
/// </remarks>
public class PairingDialogSurfaceTests
{
    [Fact]
    public void TheRootContentIsACardWithoutBackgroundOrCornerRadius()
    {
        var dialog = Markup();

        dialog.Should().Contain("<material:Card ",
            "the pairing dialog's content sits on the shared Material Card surface, not a raw Border");

        dialog.Should().NotContain("<Border Padding=\"24\" Background=",
            "the old Border-with-literal-background root was replaced by material:Card, whose "
            + "App.axaml theme already supplies CardBackgroundBrush and CardCornerRadius");

        var cardOpenTag = ExtractTag(dialog, "<material:Card ");
        cardOpenTag.Should().NotContain("Background=",
            "the Card theme in App.axaml owns Background; a literal here would shadow the "
            + "personalization slider's only path in (REGRESSION-GUARDS, Material.Avalonia template parts)");
        cardOpenTag.Should().NotContain("CornerRadius=",
            "the Card theme in App.axaml owns CornerRadius via CardCornerRadius; a literal here "
            + "would shadow the personalization corner-radius slider");
    }

    [Fact]
    public void TheTitleUsesTheHeadline6TypeScale()
    {
        var dialog = Markup();

        dialog.Should().Contain("Theme=\"{StaticResource Headline6TextBlock}\"",
            "the dialog title moved onto the shared Material type scale instead of a literal FontSize");

        dialog.Should().NotContain("FontSize=\"20\"",
            "the literal 20pt title size is retired now that Headline6TextBlock supplies it");
    }

    [Fact]
    public void OnlyTheTextBoxKeepsAnInlineFontSize()
    {
        // Typography exception 4: the PIN entry TextBox has no matching ControlTheme, so its
        // FontSize stays inline (see PairingDialog.axaml's ownership comment and the
        // TYPOGRAPHY-VOCABULARY exceptions list). Every other inline size in this file was the
        // title, which is now gone.
        var matches = System.Text.RegularExpressions.Regex.Matches(Markup(), "FontSize=\"[0-9]");
        matches.Count.Should().Be(1,
            "the pairing dialog should carry exactly one inline FontSize left — the PIN TextBox, "
            + "which has no matching Material type ControlTheme");
    }

    [Fact]
    public void TheWindowExtendsUnderTheDrawnTitleBar()
    {
        Markup().Should().Contain("ExtendClientAreaTitleBarHeightHint=\"32\"",
            "matching ConfirmationDialog's inset so the Card's content clears the drawn title bar "
            + "instead of sitting under it");
    }

    [Fact]
    public void ThePinnedBindingsAndClassesSurvivedTheMoveToCard()
    {
        var dialog = Markup();

        dialog.Should().Contain("assists:TextFieldAssist.Hints=\"{Binding ErrorText}\"",
            "the PIN error stays attached to the field, not floating below it (TextInputTests)");
        dialog.Should().Contain("Classes.invalid=\"{Binding ErrorText, Converter={x:Static StringConverters.IsNotNullOrEmpty}}\"",
            "the field's invalid state binding is unchanged by the surface move");
        dialog.Should().Contain("IsIndeterminate=\"True\"",
            "the handshake progress bar stays honestly indeterminate");
        dialog.Should().Contain("AutomationProperties.Name=\"{local:Localize Status_Connecting}\"",
            "the indeterminate bar keeps its accessible name (ProgressIndicatorTests)");
        dialog.Should().Contain("Classes=\"secondary\"",
            "Cancel stays a secondary button");
        dialog.Should().Contain("Classes=\"primary\"",
            "Pair stays a primary button");
        dialog.Should().Contain("<KeyBinding Gesture=\"Escape\" Command=\"{Binding CancelCommand}\"/>",
            "Escape still dismisses the dialog (DialogsDismissOnEscapeTests)");
    }

    // ─────────────────────────── plumbing ───────────────────────────

    private static string Markup()
    {
        var text = File.ReadAllText(
            Path.Combine(RepoRoot(), "remex.desktop", "Views", "PairingDialog.axaml"));

        text.Should().NotBeNullOrEmpty("PairingDialog.axaml must exist and have content to guard");
        return text;
    }

    private static string ExtractTag(string text, string openingToken)
    {
        var start = text.IndexOf(openingToken, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"{openingToken} should appear in PairingDialog.axaml");

        var end = text.IndexOf('>', start);
        end.Should().BeGreaterThan(start);

        return text.Substring(start, end - start);
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
