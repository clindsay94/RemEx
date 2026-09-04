using System.Text.RegularExpressions;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Overlay focus management for the drawer and the Personalize side sheet (RemEx-ddk6b).
/// </summary>
/// <remarks>
/// <para>
/// A source scan, matching <see cref="ShellDrawerOverlayTests"/>: there is no headless render here,
/// and focus that lands behind a scrim throws nothing - it just silently strands a keyboard user.
/// </para>
/// <para>
/// Comments are stripped from every extracted body before assertions run
/// (<see cref="WithoutCsComments"/>/<see cref="WithoutXmlComments"/>), the same reason
/// <see cref="ShellDrawerOverlayTests"/> strips them: the wiring is discussed in prose right next to
/// it, and a guard a comment can satisfy is not a guard.
/// </para>
/// </remarks>
public class ShellOverlayFocusTests
{
    private static string ShellMarkup() => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "remex.desktop", "Views", "ShellView.axaml"));

    private static string ShellCodeBehind() => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "remex.desktop", "Views", "ShellView.axaml.cs"));

    [Fact]
    public void TheDrawerContentRootIsNamedAndCyclesTab()
    {
        var xaml = WithoutXmlComments(ShellMarkup());

        Assert.Matches(
            new Regex(@"<Border\s+Name=""DrawerContentRoot""\s+KeyboardNavigation\.TabNavigation=""Cycle"""),
            xaml);
    }

    [Fact]
    public void TheSettingsSideSheetCyclesTabAndStaysTransparent()
    {
        // Background="Transparent" is a REGRESSION-GUARDS invariant (Desktop shell — "An unset
        // property is not a neutral property"): PART_RootBorder must stay hit-testable for the
        // scrim's click-to-dismiss, so this asserts it survives alongside the new attribute rather
        // than trusting a diff not to have touched it.
        var xaml = ShellMarkup();

        var tagStart = xaml.IndexOf("<material:SideSheet", StringComparison.Ordinal);
        Assert.True(tagStart >= 0, "ShellView.axaml no longer declares a material:SideSheet");

        var tagEnd = xaml.IndexOf('>', tagStart);
        Assert.True(tagEnd > tagStart, "the SettingsSideSheet opening tag never closes");

        var openTag = xaml[tagStart..tagEnd];

        Assert.Contains(@"Name=""SettingsSideSheet""", openTag, StringComparison.Ordinal);
        Assert.Contains(@"Background=""Transparent""", openTag, StringComparison.Ordinal);
        Assert.Contains(@"KeyboardNavigation.TabNavigation=""Cycle""", openTag, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSettingsSheetCloseButtonIsNamedAndKeepsItsAutomationName()
    {
        // A11y_Close already exists (used by other close affordances) - reused rather than adding a
        // new resx key, per the bead's constraints.
        Assert.Matches(
            new Regex(@"<Button\s+Name=""SettingsSheetCloseButton""\s+AutomationProperties\.Name=""\{conv:Localize A11y_Close\}"""),
            ShellMarkup());
    }

    [Fact]
    public void TheDrawerBranchStillResyncsAndArmsBeforeTogglingFocus()
    {
        var body = OnViewModelPropertyChangedBody();

        var drawerBranch = body.IndexOf("nameof(ShellViewModel.IsDrawerOpen)", StringComparison.Ordinal);
        Assert.True(drawerBranch >= 0, "OnViewModelPropertyChanged no longer branches on IsDrawerOpen");

        var sheetBranch = body.IndexOf("nameof(ShellViewModel.IsSettingsPanelOpen)", StringComparison.Ordinal);
        Assert.True(sheetBranch > drawerBranch,
            "OnViewModelPropertyChanged no longer branches on IsSettingsPanelOpen after the drawer branch");

        var drawerSection = body[drawerBranch..sheetBranch];

        Assert.Contains("ResyncNavListSelection()", drawerSection, StringComparison.Ordinal);
        Assert.Contains("ArmNavEntranceOnFirstOpen(", drawerSection, StringComparison.Ordinal);
        Assert.Contains("OnOverlayToggled(", drawerSection, StringComparison.Ordinal);
        Assert.Contains("_drawerInvoker", drawerSection, StringComparison.Ordinal);
        Assert.Contains("_drawerToggle", drawerSection, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSettingsBranchTogglesFocusWithTheGearFabAsFallback()
    {
        var body = OnViewModelPropertyChangedBody();

        var sheetBranch = body.IndexOf("nameof(ShellViewModel.IsSettingsPanelOpen)", StringComparison.Ordinal);
        Assert.True(sheetBranch >= 0, "OnViewModelPropertyChanged no longer branches on IsSettingsPanelOpen");

        var sheetSection = body[sheetBranch..];

        Assert.Contains("OnOverlayToggled(", sheetSection, StringComparison.Ordinal);
        Assert.Contains("_sheetInvoker", sheetSection, StringComparison.Ordinal);
        Assert.Contains("_gearFab", sheetSection, StringComparison.Ordinal);
        Assert.Contains("_settingsSideSheet", sheetSection, StringComparison.Ordinal);
    }

    [Fact]
    public void OverlayToggledPostsTheMoveInAndReadsFocusFromTheFocusManager()
    {
        var body = OnOverlayToggledBody();

        Assert.Contains("Dispatcher.UIThread.Post(", body, StringComparison.Ordinal);
        Assert.Contains("FocusManager", body, StringComparison.Ordinal);
        Assert.Contains("GetFocusedElement()", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRestorePathIsGatedBehindAncestryAndVisibility()
    {
        var body = OnOverlayToggledBody();

        Assert.Contains("IsVisualAncestorOf(", body, StringComparison.Ordinal);
        Assert.Contains("IsEffectivelyVisible", body, StringComparison.Ordinal);

        // The Focus() call that can steal focus back onto the shell must be textually AFTER the
        // null-or-inside-the-closing-overlay check, not merely present somewhere in the method -
        // an unconditional restore is exactly what would rip focus out of RemoteDesktopView or any
        // other page that focused itself while some OTHER overlay's PropertyChanged still fires.
        var guardIndex = body.IndexOf("focused == null || focusIsInsideClosingOverlay", StringComparison.Ordinal);
        Assert.True(guardIndex >= 0, "OnOverlayToggled no longer gates the restore on focus-null-or-inside-overlay");

        var restoreFocusCall = body.IndexOf("restoreTarget as InputElement", StringComparison.Ordinal);
        Assert.True(restoreFocusCall > guardIndex,
            "the restore Focus() call must come after (and therefore be inside) the null-or-inside-overlay guard");
    }

    /// <summary>
    /// The braces-matched body of <c>ShellView.OnViewModelPropertyChanged</c>, comments stripped.
    /// </summary>
    private static string OnViewModelPropertyChangedBody() =>
        ExtractMethodBody("private void OnViewModelPropertyChanged(");

    /// <summary>
    /// The braces-matched body of <c>ShellView.OnOverlayToggled</c>, comments stripped.
    /// </summary>
    private static string OnOverlayToggledBody() =>
        ExtractMethodBody("private void OnOverlayToggled(");

    /// <summary>
    /// Same brace-counting approach as <c>ShellDrawerOverlayTests.OnKeyDownBody</c>, generalised to
    /// any method signature substring rather than one hardcoded method.
    /// </summary>
    private static string ExtractMethodBody(string signatureNeedle)
    {
        var cs = ShellCodeBehind();

        var signature = cs.IndexOf(signatureNeedle, StringComparison.Ordinal);
        Assert.True(signature >= 0, $"ShellView.axaml.cs no longer contains \"{signatureNeedle}\"");

        var open = cs.IndexOf('{', signature);
        Assert.True(open > signature, $"\"{signatureNeedle}\" has no body");

        var depth = 0;
        var close = -1;

        for (var i = open; i < cs.Length; i++)
        {
            if (cs[i] == '{')
            {
                depth++;
            }
            else if (cs[i] == '}' && --depth == 0)
            {
                close = i;
                break;
            }
        }

        Assert.True(close > open, $"\"{signatureNeedle}\"'s braces do not balance");

        return WithoutCsComments(cs[open..(close + 1)]);
    }

    private static string WithoutCsComments(string source) =>
        Regex.Replace(
            Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline),
            @"//[^\r\n]*",
            string.Empty);

    private static string WithoutXmlComments(string xaml) =>
        Regex.Replace(xaml, "<!--.*?-->", string.Empty, RegexOptions.Singleline);
}
