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

    public static TheoryData<string> Dialogs =>
    [
        "PairingDialog", "FileConsentDialog", "SetAlertDialog",
        "ConfirmationDialog", "RestorePromptWindow", "SecondMetricDialog",
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

    [Fact]
    public void TheFileConsentDialogsEscapeDeniesRatherThanAccepts()
    {
        // THE ONE DIALOG WHERE THE TARGET MATTERS AS MUCH AS THE BINDING. Deny is fail-closed; a
        // keyboard dismissal that granted an incoming file would be a security regression wearing a
        // convenience feature.
        var xaml = File.ReadAllText(Path.Combine(ViewsDirectory(), "FileConsentDialog.axaml"));

        var escape = Regex.Match(xaml, "<KeyBinding[^>]*Gesture=\"Escape\"[^>]*>");
        Assert.True(escape.Success, "FileConsentDialog no longer binds Escape declaratively");
        Assert.Contains("DenyCommand", escape.Value, StringComparison.Ordinal);

        // AllowCommand IS THE REAL NAME OF THE GRANTING ACTION, and getting that right matters more
        // than it looks. An earlier version of this line forbade "AcceptCommand", which no view model
        // here has — so it asserted that a binding nobody could write was not written. Avalonia's XAML
        // compiler resolves command bindings against the view model type, so a made-up name fails the
        // BUILD (AVLN2000) rather than this test; the only mutation this can catch is one to a command
        // that genuinely exists, which means it has to name that command.
        Assert.DoesNotContain("AllowCommand", escape.Value, StringComparison.Ordinal);
    }
}
