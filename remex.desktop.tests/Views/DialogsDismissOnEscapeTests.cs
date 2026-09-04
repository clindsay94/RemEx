using System.Text.RegularExpressions;
using System.Xml.Linq;
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

    private static string ReadView(string fileName) =>
        File.ReadAllText(Path.Combine(ViewsDirectory(), fileName));

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
    /// RemEx-x6a70.3 fix round 2: <c>ConfirmAsync</c>/<c>RestoreAsync</c> no longer read
    /// <c>ShowDialog</c>'s return value for their outcome at all (it resolves through a library
    /// <c>DialogResult</c> property external code cannot set - see the type remarks) - both now return
    /// <c>MaterialDialogs.Resolve(content.ResultTask)</c>, the pure helper unit-tested directly in
    /// <c>MaterialDialogsTests</c>. THE MUTATION THIS GUARDS: reverting either builder to read
    /// <c>result?.GetResult</c> would silently reintroduce reading a value nothing can ever write.
    /// </summary>
    [Fact]
    public void ConfirmAndRestoreResolveThroughTheContentsResultTask()
    {
        var source = MaterialDialogsSource();

        var resolveCalls = Regex.Matches(source, @"return Resolve\(content\.ResultTask\);").Count;
        Assert.True(
            resolveCalls >= 2,
            "ConfirmAsync and RestoreAsync should both `return Resolve(content.ResultTask);`; found "
                + "fewer than two such returns.");

        Assert.DoesNotContain("result?.GetResult", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE ONE DIALOG WHERE THE TARGET MATTERS AS MUCH AS THE BINDING. Deny is fail-closed; a
    /// keyboard dismissal that granted an incoming file would be a security regression wearing a
    /// convenience feature. FileConsentDialog.axaml is gone (RemEx-x6a70.3 moved it onto
    /// MaterialDialogs.FileConsentAsync), so this scans that.
    /// </summary>
    /// <remarks>
    /// Fix round 2 dropped <c>MapConsent</c> - Deny/Allow are real buttons in
    /// <c>FileConsentContent</c> now, bound straight to <c>FileConsentDialogViewModel.DenyCommand</c>/
    /// <c>AllowCommand</c>, so there is no library result string left to translate. The fail-closed
    /// contract MapConsent used to express now lives in
    /// <see cref="Remex.Desktop.ViewModels.FileConsentDialogViewModel.ResolveAsDeny"/>
    /// (unit-tested directly in <c>FileConsentDialogViewModelTests</c>) - this guards that
    /// <c>FileConsentAsync</c> actually calls it for the window-closed-without-a-decision path.
    /// </remarks>
    [Fact]
    public void TheFileConsentDialogsEscapeDeniesRatherThanAccepts()
    {
        var source = MaterialDialogsSource();

        // The consent dialog's NegativeResult must still be the deny value, not the allow one.
        Assert.Contains(
            "NegativeResult = new DialogResult(ConsentDenyResult),",
            source,
            StringComparison.Ordinal);

        // And the window closing without a button click (Escape included) must fail closed through
        // the view model's own deny resolution, not a fresh decision built from a result string.
        Assert.Contains("vm.ResolveAsDeny();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MapConsent", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// RemEx-x6a70.3 fix round 1: <c>ConfirmAsync</c> and <c>RestoreAsync</c> clipped their message
    /// instead of wrapping it, because <c>CreateAlertDialog</c>'s <c>SupportingText</c> TextBlock has
    /// no <c>TextWrapping</c> set. Both now build through <c>CreateCustomDialog</c> with
    /// <c>DialogContent</c> instead - this guards that they stay off <c>CreateAlertDialog</c> and off
    /// setting <c>ContentHeader</c>/<c>SupportingText</c> (the non-wrapping path), so nobody can revert
    /// the fix by switching the builder back without the wrap guard below also failing.
    /// </summary>
    [Fact]
    public void ConfirmAndRestoreBuildThroughCreateCustomDialogNotCreateAlertDialog()
    {
        var source = MaterialDialogsSource();

        // Substring checks against the raw source rather than a stripped-comments version would also
        // trip on this very doc comment naming the builder it moved away from, so match the actual
        // invocation/assignment shapes instead of the bare words.
        Assert.DoesNotContain("DialogHelper.CreateAlertDialog(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ContentHeader =", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SupportingText =", source, StringComparison.Ordinal);

        var createCustomDialogCalls = Regex.Matches(source, @"DialogHelper\.CreateCustomDialog\(").Count;
        Assert.True(
            createCustomDialogCalls >= 3,
            "ConfirmAsync, FileConsentAsync and RestoreAsync should all build through CreateCustomDialog; "
                + "found fewer CreateCustomDialog( calls than that.");

        var dialogContentUses = Regex.Matches(source, @"new DialogContent\(").Count;
        Assert.True(
            dialogContentUses >= 2,
            "Both ConfirmAsync and RestoreAsync should build their content as `new DialogContent(...)`; "
                + "found fewer than that.");
    }

    /// <summary>
    /// RemEx-x6a70.3 fix round 2's actual button-vocabulary fix, pinned at the exact call sites so a
    /// mutation cannot pass by matching only the loose per-file checks below.
    /// INJECTION B THIS CATCHES: swapping ConfirmAsync's action button from "primary danger" to
    /// "secondary" (or any other class) breaks the first literal below; swapping RestoreAsync's
    /// "primary" the same way breaks the regex (anchored so "primary danger" cannot satisfy it either).
    /// </summary>
    [Fact]
    public void ConfirmPassesPrimaryDangerAndRestorePassesPrimaryToDialogContent()
    {
        var source = MaterialDialogsSource();

        Assert.Contains(
            "new DialogContent(title, message, loc[\"Btn_Cancel\"], confirmText, \"primary danger\")",
            source,
            StringComparison.Ordinal);

        Assert.Matches(
            new Regex(
                @"new DialogContent\(\s*loc\[""Restore_PromptTitle""\].*?loc\[""Restore_Skip""\],\s*loc\[""Restore_Accept""\],\s*""primary""\);",
                RegexOptions.Singleline),
            source);
    }

    /// <summary>
    /// The actual wrap fix, guarded at the source that renders the message: <c>DialogContent</c> (the
    /// content <c>ConfirmAsync</c>/<c>RestoreAsync</c> build) and <c>FileConsentContent</c> (the content
    /// <c>FileConsentAsync</c> builds) must both set <c>TextWrapping="Wrap"</c> on their message
    /// TextBlock, or a long message clips at the window edge again - the exact regression this fix
    /// round exists for. Anti-vacuity: also assert each markup file actually contains a message
    /// TextBlock at all, so a rewrite that deletes the TextBlock instead of un-wrapping it does not
    /// pass by having nothing left to check.
    /// </summary>
    [Theory]
    [InlineData("DialogContent.axaml")]
    [InlineData("FileConsentContent.axaml")]
    public void DialogContentMessageWraps(string markupFile)
    {
        var path = Path.Combine(ViewsDirectory(), markupFile);
        Assert.True(File.Exists(path), $"{markupFile} moved or was renamed");

        var xaml = File.ReadAllText(path);

        Assert.Contains("<TextBlock", xaml, StringComparison.Ordinal);
        Assert.Contains("TextWrapping=\"Wrap\"", xaml, StringComparison.Ordinal);
    }

    /// <summary>
    /// RemEx-x6a70.3 fix round 2's route: the library gets no <c>DialogButtons</c> of its own for any
    /// of the three prompts (it has no builder-level way to hand a rendered button this app's Classes
    /// vocabulary - see the type remarks) - every visible button is a real RemEx <c>Button</c> living
    /// in the content control instead. Anti-regression: also asserts no <c>new DialogButton {</c>
    /// construction remains, so nobody can quietly go back to the library-button fallback fix round 1
    /// used without this test noticing.
    /// </summary>
    [Fact]
    public void NoBuiltDialogHandsTheLibraryAnyDialogButtons()
    {
        var source = MaterialDialogsSource();

        var emptyDialogButtons = Regex.Matches(source, @"DialogButtons\s*=\s*Array\.Empty<DialogButton>\(\),").Count;
        Assert.True(
            emptyDialogButtons >= 3,
            "ConfirmAsync, FileConsentAsync and RestoreAsync should each pass an empty DialogButtons "
                + "array; found fewer than three such assignments.");

        Assert.DoesNotContain("new DialogButton {", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// Guards the wiring inside <c>DialogContent</c>'s own code-behind: its Cancel button must resolve
    /// its outcome to <c>false</c> and its action button to <c>true</c>. INJECTION A THIS CATCHES:
    /// making Cancel complete the outcome with <c>true</c> would make Escape's sibling - a literal
    /// click on Cancel - read as if the user had confirmed/accepted.
    /// </summary>
    [Fact]
    public void DialogContentResolvesCancelFalseAndActionTrue()
    {
        var cs = ReadView("DialogContent.axaml.cs");

        Assert.Matches(
            new Regex(@"OnCancelClick\([^)]*\)\s*=>\s*Resolve\(false\);"),
            cs);
        Assert.Matches(
            new Regex(@"OnActionClick\([^)]*\)\s*=>\s*Resolve\(true\);"),
            cs);
    }

    /// <summary>
    /// The button-vocabulary requirement itself, parsed from the actual XAML attribute values (not a
    /// substring match, which a stray comment could also satisfy) - see docs/BUTTON-VOCABULARY.md.
    /// <c>DialogContent</c>'s Cancel is always "secondary" in markup; its action button's classes vary
    /// by caller and are covered instead by
    /// <see cref="ConfirmPassesPrimaryDangerAndRestorePassesPrimaryToDialogContent"/>.
    /// <c>FileConsentContent</c>'s Deny/Allow are fixed, so both are asserted here, each still bound to
    /// the view model's own command.
    /// </summary>
    [Fact]
    public void ContentControlsCarryTheVocabularyClassesOnTheRightButtons()
    {
        var dialogContentButtons = ParseButtons(ReadView("DialogContent.axaml"));
        var cancelButton = Assert.Single(dialogContentButtons, b => b.Name == "CancelButton");
        Assert.Equal("secondary", cancelButton.Classes);

        var consentButtons = ParseButtons(ReadView("FileConsentContent.axaml"));
        var deny = Assert.Single(consentButtons, b => b.Command == "DenyCommand");
        var allow = Assert.Single(consentButtons, b => b.Command == "AllowCommand");
        Assert.Equal("secondary", deny.Classes);
        Assert.Equal("primary", allow.Classes);
    }

    private static List<(string? Name, string? Classes, string? Command)> ParseButtons(string xaml)
    {
        var doc = XDocument.Parse(xaml);
        return doc.Descendants()
            .Where(e => e.Name.LocalName == "Button")
            .Select(e => (
                Name: e.Attributes().FirstOrDefault(a => a.Name.LocalName == "Name")?.Value,
                Classes: e.Attributes().FirstOrDefault(a => a.Name.LocalName == "Classes")?.Value,
                Command: ExtractBindingPath(e.Attributes().FirstOrDefault(a => a.Name.LocalName == "Command")?.Value)))
            .ToList();
    }

    private static string? ExtractBindingPath(string? bindingMarkup)
    {
        if (bindingMarkup is null)
            return null;

        var match = Regex.Match(bindingMarkup, @"\{Binding\s+([A-Za-z0-9_]+)\s*\}");
        return match.Success ? match.Groups[1].Value : null;
    }
}
