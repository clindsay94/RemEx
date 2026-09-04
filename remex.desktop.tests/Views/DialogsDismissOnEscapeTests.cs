using System.Text.RegularExpressions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Every modal dialog can be dismissed from the keyboard (RemEx-xxifk).
/// </summary>
/// <remarks>
/// <para>
/// None of the six bound Escape. A modal you cannot dismiss from the keyboard is a real gap rather
/// than a nicety — it is the one interaction every user already knows, and <c>PairingDialog</c> made
/// someone type six digits and then reach for the mouse to back out.
/// </para>
/// <para>
/// **ESCAPE ONLY, AND THE ASYMMETRY IS THE POINT.** RemEx-df08 asks for Enter as well and is right
/// that Enter needs deciding per dialog: binding it to a destructive default turns a deliberate
/// confirmation into a reflex, and <c>FileConsentDialog</c>'s Enter must never become a way to accept
/// an incoming file by holding a key. Escape carries none of that, because cancelling is never the
/// destructive option — the same reasoning that makes Enter risky is what makes Escape safe. So this
/// asserts Escape is bound and says nothing about Enter.
/// </para>
/// <para>
/// A source scan, for the usual reason: the alternative is driving six modal windows through a
/// headless Avalonia harness, which is far heavier than what it guards. It is deliberately loose
/// about HOW each dialog binds it — three route a command from XAML and three override
/// <c>OnKeyDown</c>, because a KeyBinding cannot reach a code-behind Click handler — and strict about
/// the one thing that matters, which is that Escape reaches something.
/// </para>
/// </remarks>
public class DialogsDismissOnEscapeTests
{
    private static string ViewsDirectory() =>
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "remex.desktop", "Views");

    private static string MaterialDialogsSource() =>
        File.ReadAllText(Path.Combine(ViewsDirectory(), "MaterialDialogs.cs"));

    public static TheoryData<string> Dialogs =>
    [
        "PairingDialog", "SetAlertDialog", "SecondMetricDialog",
    ];

    [Theory]
    [MemberData(nameof(Dialogs))]
    public void TheDialogDismissesOnEscape(string dialog)
    {
        var markup = Path.Combine(ViewsDirectory(), dialog + ".axaml");
        var codeBehind = markup + ".cs";

        Assert.True(File.Exists(markup), $"{dialog}.axaml moved or was renamed");

        var xaml = File.ReadAllText(markup);
        var cs = File.Exists(codeBehind) ? File.ReadAllText(codeBehind) : string.Empty;

        var declarative = Regex.IsMatch(xaml, "<KeyBinding[^>]*Gesture=\"Escape\"");
        var handled = cs.Contains("Key.Escape", StringComparison.Ordinal);

        Assert.True(
            declarative || handled,
            $"{dialog} binds nothing to Escape, so it cannot be dismissed from the keyboard. Route it "
                + "to whatever this dialog already treats as cancel — a KeyBinding when that is a "
                + "command, an OnKeyDown override when it is a Click handler.");
    }

    /// <summary>
    /// ConfirmationDialog, FileConsentDialog and RestorePromptWindow stopped being their own Window
    /// subclasses in RemEx-x6a70.3 - they are now built through <c>MaterialDialogs.cs</c> via
    /// Material.Avalonia.Dialogs' builders, so the source scan above no longer has files to point at
    /// for them. This is the replacement: it scans the one file all three now share.
    /// </summary>
    [Fact]
    public void MaterialDialogsAttachesEscapeDismissToEveryBuiltDialog()
    {
        var source = MaterialDialogsSource();

        // Three builders (ConfirmAsync, FileConsentAsync, RestoreAsync), three calls - the same
        // guarantee the per-file scan above gave, just counted instead of located by filename.
        var escapeAttachments = Regex.Matches(source, @"AttachEscapeDismiss\(").Count;
        Assert.True(
            escapeAttachments >= 3,
            "MaterialDialogs.cs should attach Escape-dismisses-the-window to each of ConfirmAsync, "
                + "FileConsentAsync and RestoreAsync; found fewer AttachEscapeDismiss( calls than that.");

        // AND THE HANDLER ITSELF MUST ACTUALLY CLOSE THE WINDOW ON ESCAPE, or the count above is
        // satisfied by calls into a body that does nothing (anti-vacuity).
        var handlerBody = Regex.Match(
            source,
            @"private static void AttachEscapeDismiss\(Window window\)\s*\{(?<body>.*?)\n    \}",
            RegexOptions.Singleline);
        Assert.True(handlerBody.Success, "AttachEscapeDismiss was renamed or restructured");
        Assert.Contains("Key.Escape", handlerBody.Groups["body"].Value, StringComparison.Ordinal);
        Assert.Contains("window.Close()", handlerBody.Groups["body"].Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// Each of the three builders must hand Material.Avalonia.Dialogs a <c>NegativeResult</c> - the
    /// value <c>DialogHelper</c> applies to the dialog's result BEFORE it is shown, so every dismissal
    /// that is not a button click (Escape via the handler above, Alt+F4, the title-bar close button)
    /// resolves to it. Without this, those paths would resolve to <c>DialogResult.NoResult</c>
    /// ("none"), which none of the three return-mapping checks below treat as an affirmative answer -
    /// but asserting the NegativeResult is set is what makes that not an accident.
    /// </summary>
    [Fact]
    public void EveryBuiltDialogSetsANegativeResult()
    {
        var source = MaterialDialogsSource();
        var negativeResultAssignments = Regex.Matches(source, @"NegativeResult\s*=\s*new DialogResult\(").Count;

        Assert.True(
            negativeResultAssignments >= 3,
            "MaterialDialogs.cs should set NegativeResult when building each of the confirm, consent "
                + "and restore dialogs; found fewer than three NegativeResult assignments.");
    }

    [Fact]
    public void ConfirmAsyncOnlyReturnsTrueOnTheConfirmResult()
    {
        // THE MUTATION THIS GUARDS: returning true on CancelResult (or on the raw result string being
        // non-null) would make Escape/Alt+F4/the close button - all of which resolve to CancelResult
        // - read as a confirmed destructive action instead of a declined one.
        var source = MaterialDialogsSource();

        Assert.Contains(
            "return result?.GetResult == ConfirmResult;",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RestoreAsyncOnlyReturnsTrueOnItsPositiveResult()
    {
        var source = MaterialDialogsSource();

        Assert.Contains(
            "return result?.GetResult == RestoreResult;",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheFileConsentDialogsEscapeDeniesRatherThanAccepts()
    {
        // THE ONE DIALOG WHERE THE TARGET MATTERS AS MUCH AS THE BINDING. Deny is fail-closed; a
        // keyboard dismissal that granted an incoming file would be a security regression wearing a
        // convenience feature. FileConsentDialog.axaml is gone (RemEx-x6a70.3 moved it onto
        // MaterialDialogs.FileConsentAsync / MaterialDialogs.MapConsent), so this now scans that.
        var source = MaterialDialogsSource();

        // The consent dialog's NegativeResult must be the deny value, not the allow one.
        Assert.Contains(
            "NegativeResult = new DialogResult(ConsentDenyResult),",
            source,
            StringComparison.Ordinal);

        // And MapConsent must grant on that exact allow value and nothing else - see
        // MaterialDialogsTests for the table-driven version of this (null/none/cancel/deny all deny).
        var mapConsent = Regex.Match(
            source,
            @"internal static FileConsentDecision MapConsent\(string\? result, bool remember\) =>(?<body>.*?);",
            RegexOptions.Singleline);
        Assert.True(mapConsent.Success, "MapConsent was renamed or restructured");
        Assert.Contains("result == ConsentAllowResult", mapConsent.Groups["body"].Value, StringComparison.Ordinal);
        Assert.DoesNotContain("ConsentDenyResult ==", mapConsent.Groups["body"].Value, StringComparison.Ordinal);
    }
}
