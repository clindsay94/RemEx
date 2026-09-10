using System.Threading.Tasks;
using Remex.Desktop.Views;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Unit coverage for <see cref="MaterialDialogs.Resolve"/>, the pure function
/// <see cref="MaterialDialogs.ConfirmAsync"/>/<see cref="MaterialDialogs.RestoreAsync"/> use to turn
/// <see cref="Remex.Desktop.Views.DialogContent.ResultTask"/> into their boolean outcome - extracted so
/// the "closes without a button defaults to declined" contract can be tested directly, without an
/// Avalonia window (this repo has no headless render harness - see
/// <c>DialogsDismissOnEscapeTests</c>'s source-scan approach for everything else in
/// <c>MaterialDialogs.cs</c>).
/// </summary>
/// <remarks>
/// RemEx-x6a70.3 fix round 2 replaced this file's previous subject, <c>MaterialDialogs.MapConsent</c>:
/// that function translated a Material.Avalonia.Dialogs library button's result string into a
/// <c>FileConsentDecision</c>, and it has no callers left once Deny/Allow became real buttons bound
/// straight to <see cref="Remex.Desktop.ViewModels.FileConsentDialogViewModel.DenyCommand"/>/
/// <c>AllowCommand</c> - there is no library result string left to translate. Its fail-closed guarantee
/// is still covered, just by a different, more direct test:
/// <c>FileConsentDialogViewModelTests.ResolveAsDeny_WhenDismissed_ResolvesDenied</c>.
/// </remarks>
public class MaterialDialogsTests
{
    [Fact]
    public void Resolve_TaskCompletedTrue_ReturnsTrue()
    {
        var tcs = new TaskCompletionSource<bool>();
        tcs.SetResult(true);

        Assert.True(MaterialDialogs.Resolve(tcs.Task));
    }

    [Fact]
    public void Resolve_TaskCompletedFalse_ReturnsFalse()
    {
        var tcs = new TaskCompletionSource<bool>();
        tcs.SetResult(false);

        Assert.False(MaterialDialogs.Resolve(tcs.Task));
    }

    [Fact]
    public void Resolve_TaskNeverCompleted_DefaultsToFalse()
    {
        // THE CASE THIS EXISTS FOR: the window closed some other way (Escape, Alt+F4, the title-bar
        // close button) without either DialogContent button ever being clicked, so ResultTask is left
        // incomplete rather than faulted or cancelled. Resolve must treat that as a decline, not throw
        // or block - it never awaits the task, only inspects whether it already finished.
        var tcs = new TaskCompletionSource<bool>();

        Assert.False(MaterialDialogs.Resolve(tcs.Task));
    }
}
