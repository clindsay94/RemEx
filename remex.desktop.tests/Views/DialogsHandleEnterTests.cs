using System.Text.RegularExpressions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Enter triggers the affirmative action on the five dialogs RemEx-df08 covers, matching
/// <c>DialogsDismissOnEscapeTests</c>'s asymmetry now that Enter has been decided per dialog.
/// </summary>
/// <remarks>
/// <para>
/// A source scan, for the same reason <c>DialogsDismissOnEscapeTests</c> is one: no headless
/// Avalonia harness exists in this suite, so a Window/UserControl cannot be instantiated and
/// key-pressed here. Each assertion is anchored to the specific command/handler a dialog's own
/// primary button already uses, not just "an Enter binding exists somewhere" - the failure mode
/// this bead calls out is getting the target backwards (Enter reaching Cancel/Clear instead of the
/// real confirm action), and a loose match would not catch that.
/// </para>
/// <para>
/// CopyAlertDialog and DialogContent both got their Enter wiring already (CopyAlertDialog predates
/// this pass; DialogContent's <c>IsDefault</c> is the only mechanism available since it is hosted
/// inside a Window Material.Avalonia.Dialogs builds, not one this app's own XAML owns) - both are
/// pinned here anyway so a revert of either fails this file, not just the one that was mid-flight.
/// </para>
/// <para>
/// FIX ROUND 1 (Opus review): <c>DialogContent_ActionButtonIsDefaultIsSetPerCallSite</c> replaces
/// <c>DialogContent_ActionButtonIsDefault_CancelButtonIsNot</c>, which pinned a hardcoded
/// <c>IsDefault="True"</c> in the XAML - that was itself the bug the review caught (a single
/// <c>IsDefault</c> on shared content cannot express ConfirmAsync's destructive "primary danger"
/// action needing Enter OFF while RestoreAsync's non-destructive "primary" action can safely have it
/// ON). The flag now lives per call site in <c>MaterialDialogs.cs</c>, so the source scan moved
/// there. <c>SecondMetricDialog_OnKeyDownAppliesTheSelectionOnEnter</c> below was also re-pinned:
/// the previous body literal (<c>Apply(SensorList.SelectedItem as string)</c> called unconditionally)
/// was itself the review's HIGH finding - Enter fired Clear's outcome whenever a search filtered out
/// the current selection - so asserting that exact literal again would keep the regression green.
/// </para>
/// </remarks>
public class DialogsHandleEnterTests
{
    private static string ViewsDirectory() =>
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "remex.desktop", "Views");

    private static string ReadView(string fileName) =>
        File.ReadAllText(Path.Combine(ViewsDirectory(), fileName));

    [Fact]
    public void PairingDialog_EnterRoutesToSubmitNotCancel()
    {
        var xaml = ReadView("PairingDialog.axaml");

        Assert.Matches(
            new Regex(@"<KeyBinding\s+Gesture=""Enter""\s+Command=""\{Binding SubmitCommand\}""\s*/>"),
            xaml);
    }

    [Fact]
    public void SetAlertDialog_EnterRoutesToConfirmNotClearOrCancel()
    {
        var xaml = ReadView("SetAlertDialog.axaml");

        Assert.Matches(
            new Regex(@"<KeyBinding\s+Gesture=""Enter""\s+Command=""\{Binding ConfirmCommand\}""\s*/>"),
            xaml);

        // Anti-backwards: Enter must not have landed on the destructive Clear action or on Cancel.
        Assert.DoesNotMatch(
            new Regex(@"<KeyBinding\s+Gesture=""Enter""\s+Command=""\{Binding (ClearAlertCommand|CancelCommand)\}""\s*/>"),
            xaml);
    }

    [Fact]
    public void CopyAlertDialog_EnterRoutesToApplyNotCancel()
    {
        var xaml = ReadView("CopyAlertDialog.axaml");

        Assert.Matches(
            new Regex(@"<KeyBinding\s+Gesture=""Enter""\s+Command=""\{Binding ApplyCommand\}""\s*/>"),
            xaml);
        Assert.DoesNotMatch(
            new Regex(@"<KeyBinding\s+Gesture=""Enter""\s+Command=""\{Binding CancelCommand\}""\s*/>"),
            xaml);
    }

    /// <summary>
    /// DialogContent is hosted as <c>CustomDialogBuilderParams.Content</c> inside a Window
    /// Material.Avalonia.Dialogs builds (see MaterialDialogs.cs), so there is no app-owned Window
    /// XAML here to add a KeyBinding to - IsDefault/IsCancel are the only mechanism that reaches a
    /// Window this control's own file never declares.
    /// </summary>
    [Fact]
    public void DialogContent_XamlNeverHardcodesActionButtonIsDefault_CancelButtonNeverGetsIt()
    {
        var xaml = ReadView("DialogContent.axaml");

        var actionButton = Regex.Match(xaml, @"<Button\s+x:Name=""ActionButton""[^/]*/>").Value;
        // Anti-backwards for the fix itself: IsDefault must NOT be hardcoded here any more - a single
        // value shared by every call site is exactly the bug (see class remarks above).
        Assert.DoesNotContain("IsDefault", actionButton, StringComparison.Ordinal);

        var cancelButton = Regex.Match(xaml, @"<Button\s+x:Name=""CancelButton""[^/]*/>").Value;
        Assert.DoesNotContain("IsDefault", cancelButton, StringComparison.Ordinal);
        Assert.DoesNotContain("IsCancel", cancelButton,
            StringComparison.Ordinal); // Escape already dismisses via MaterialDialogs.AttachEscapeDismiss.
    }

    /// <summary>
    /// The constructor sets <c>ActionButton.IsDefault</c> from a caller-supplied flag instead - pinned
    /// on the constructor's signature and body, since there is still no headless Avalonia harness to
    /// instantiate the control and read the property back.
    /// </summary>
    [Fact]
    public void DialogContent_ConstructorSetsActionButtonIsDefaultFromItsParameter()
    {
        var cs = ReadView("DialogContent.axaml.cs");

        Assert.Matches(
            new Regex(@"DialogContent\(\s*string header,\s*string message,\s*string cancelText,\s*string actionText,\s*string actionClasses,\s*bool actionIsDefault\)"),
            cs);
        Assert.Contains("ActionButton.IsDefault = actionIsDefault;", cs, StringComparison.Ordinal);
    }

    /// <summary>
    /// The two real call sites, pinned to opposite values: ConfirmAsync's action is destructive
    /// ("primary danger") and must NOT default to Enter; RestoreAsync's is not and safely can. This is
    /// the actual Opus-review fix - a wrong value here reintroduces "Enter fires a destructive confirm".
    /// </summary>
    [Fact]
    public void MaterialDialogs_ConfirmPassesActionIsDefaultFalse_RestorePassesTrue()
    {
        var source = ReadView("MaterialDialogs.cs");

        Assert.Matches(
            new Regex(
                @"new DialogContent\(\s*title,\s*message,\s*loc\[""Btn_Cancel""\],\s*confirmText,\s*""primary danger"",\s*actionIsDefault:\s*false\)",
                RegexOptions.Singleline),
            source);

        Assert.Matches(
            new Regex(
                @"new DialogContent\(\s*loc\[""Restore_PromptTitle""\].*?""primary"",\s*actionIsDefault:\s*true\)",
                RegexOptions.Singleline),
            source);
    }

    /// <summary>
    /// SecondMetricDialog has no view model - Cancel/Set/Clear are code-behind handlers - so its
    /// Escape handling already overrides <c>OnKeyDown</c> rather than using a KeyBinding
    /// (RemEx-xxifk). Enter extends the same override, but ONLY when a real sensor is actually
    /// selected.
    /// </summary>
    /// <remarks>
    /// Opus review HIGH finding, fix round 1: the previous body called
    /// <c>Apply(SensorList.SelectedItem as string)</c> unconditionally, so typing a search query that
    /// filtered the current selection out of <c>_filtered</c> made <c>SelectedItem</c> go null and
    /// Enter silently fired Clear's outcome (closing the dialog, clearing the overlay). This asserts
    /// the actual fix - the Enter branch's condition includes a pattern match on
    /// <c>SensorList.SelectedItem</c> - not just that some "Apply" call exists anywhere in the method,
    /// which is exactly the kind of loose match that let the original bug pass review once already.
    /// </remarks>
    [Fact]
    public void SecondMetricDialog_OnKeyDownOnlyAppliesOnEnterWhenAStringIsActuallySelected()
    {
        var cs = ReadView("SecondMetricDialog.axaml.cs");

        var handlerBody = Regex.Match(
            cs,
            @"protected override void OnKeyDown\(Avalonia\.Input\.KeyEventArgs e\)\s*\{(?<body>.*?)\n    \}",
            RegexOptions.Singleline);

        Assert.True(handlerBody.Success, "OnKeyDown was renamed or restructured on SecondMetricDialog");
        var body = handlerBody.Groups["body"].Value;

        Assert.Contains("Key.Escape", body, StringComparison.Ordinal);

        // The Enter check itself must be GUARDED on the selection being a string - not a bare
        // `if (e.Key == Key.Enter)` followed by an unconditional Apply call.
        Assert.Matches(
            new Regex(
                @"if\s*\(e\.Key == Avalonia\.Input\.Key\.Enter\s*&&\s*SensorList\.SelectedItem is string (?<var>\w+)\)"),
            body);

        var enterGuard = Regex.Match(
            body,
            @"if\s*\(e\.Key == Avalonia\.Input\.Key\.Enter\s*&&\s*SensorList\.SelectedItem is string (?<var>\w+)\)\s*\{(?<inner>.*?)\}",
            RegexOptions.Singleline);
        Assert.True(enterGuard.Success, "Enter branch was not found in the guarded shape expected");
        Assert.Contains($"Apply({enterGuard.Groups["var"].Value})", enterGuard.Groups["inner"].Value,
            StringComparison.Ordinal);

        // Anti-backwards: the unconditional call that caused the bug must not still be reachable.
        Assert.DoesNotContain("Apply(SensorList.SelectedItem as string)", body, StringComparison.Ordinal);
    }
}
