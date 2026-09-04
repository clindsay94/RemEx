using Remex.Desktop.Views;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Table-driven coverage for <see cref="MaterialDialogs.MapConsent"/>, the one pure function
/// extracted from RemEx-x6a70.3's collapse of ConfirmationDialog/FileConsentDialog/RestorePromptWindow
/// onto Material.Avalonia.Dialogs. Everything else in <c>MaterialDialogs.cs</c> either builds a
/// <see cref="Avalonia.Controls.Window"/> (untestable without a headless Avalonia harness, which this
/// repo does not have - see <c>DialogsDismissOnEscapeTests</c>'s source-scan approach) or is a
/// one-line equality check already covered there.
/// </summary>
public class MaterialDialogsTests
{
    public static TheoryData<string?, bool, bool, bool> ConsentResults => new()
    {
        // result,   remember, expectedGranted, expectedRemember
        { "allow", true, true, true },
        { "allow", false, true, false },
        { "deny", true, false, false },
        { "deny", false, false, false },
        { null, true, false, false },
        { "none", true, false, false },
        { "cancel", true, false, false },
        { "ALLOW", true, false, false }, // case-sensitive on purpose - no silent widening of the allow set
        { "", true, false, false },
    };

    [Theory]
    [MemberData(nameof(ConsentResults))]
    public void MapConsent_OnlyGrantsOnTheExactAllowResult(
        string? result, bool remember, bool expectedGranted, bool expectedRemember)
    {
        var decision = MaterialDialogs.MapConsent(result, remember);

        Assert.Equal(expectedGranted, decision.Granted);
        Assert.Equal(expectedRemember, decision.Remember);
    }

    [Fact]
    public void MapConsent_DenyIgnoresRememberEvenWhenTrue()
    {
        // Remember only ever applies to a grant (RemEx-2m7fr's original Deny() command hard-codes
        // Remember: false regardless of the checkbox) - a denied request must never be remembered.
        var decision = MaterialDialogs.MapConsent("deny", remember: true);

        Assert.False(decision.Granted);
        Assert.False(decision.Remember);
    }
}
