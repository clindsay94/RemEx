using System.Text.RegularExpressions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// DialogContent.axaml and FileConsentContent.axaml had no <c>AutomationProperties</c> at all before
/// RemEx-df08 - every other dialog view in this app sets at least one (CopyAlertDialog's filter
/// TextBox, PairingDialog's PIN TextBox/ProgressBar, SetAlertDialog's NumericUpDown/ComboBoxes,
/// SecondMetricDialog's search TextBox). A screen reader landing on either control's content had
/// nothing to announce beyond whatever its individual TextBlocks/Buttons already expose through
/// their own visible text.
/// </summary>
/// <remarks>
/// <para>
/// FIX ROUND 1 (Opus review MEDIUM finding): the first pass set <c>AutomationProperties.Name</c> on
/// each content's root <c>StackPanel</c> in XAML. That announced nothing - Avalonia's
/// <c>Control.OnCreateAutomationPeer</c> defaults to <c>NoneAutomationPeer</c> for any element with
/// no dedicated peer type, there is no <c>PanelAutomationPeer</c> in Avalonia's peer list, and
/// <c>NoneAutomationPeer</c> is documented as not contributing to the logical structure of the
/// application at all - the TextBox/ProgressBar analogy the first pass leaned on does not hold,
/// because those controls have real dedicated peers and a StackPanel does not.
/// </para>
/// <para>
/// The real fix moved the name onto the host WINDOW instead - <c>WindowAutomationPeer</c> is a real,
/// announced peer - via <c>AutomationProperties.SetName(window, ...)</c> calls in
/// <c>MaterialDialogs.cs</c>, beside each builder's existing <c>AttachEscapeDismiss</c> call. That is
/// what these tests now pin: a source scan over <c>MaterialDialogs.cs</c>, for the same reason every
/// other dialog test in this suite is a source scan (no headless Avalonia render exists here to
/// actually query a peer's name). The old axaml-scan assertions are gone entirely, not just changed -
/// the axaml no longer carries an AutomationProperties.Name at all, so a test still looking for one
/// there would pass for a reason that has stopped being true.
/// </para>
/// </remarks>
public class DialogContentAccessibleNamesTests
{
    private static string ViewsDirectory() =>
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "remex.desktop", "Views");

    private static string ReadView(string fileName) =>
        File.ReadAllText(Path.Combine(ViewsDirectory(), fileName));

    /// <summary>
    /// The root element's own opening tag, which is the only place this guard applies. Scoped rather
    /// than searched file-wide on purpose: <c>ActionButton</c> and <c>CancelButton</c> have real
    /// <c>ButtonAutomationPeer</c>s, so a name on THEM is announced and is exactly what a later
    /// accessibility pass would add. A file-wide DoesNotContain would forbid that improvement while
    /// claiming to guard against a different mistake entirely.
    /// </summary>
    private static string RootElementTag(string fileName)
    {
        var xaml = ReadView(fileName);
        var start = xaml.IndexOf("<StackPanel", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{fileName} no longer opens with a StackPanel root; re-scope this guard.");
        var end = xaml.IndexOf('>', start);
        Assert.True(end > start, $"{fileName}'s root StackPanel tag is unterminated.");
        return xaml[start..end];
    }

    [Fact]
    public void DialogContentAxamlNoLongerSetsAutomationPropertiesOnItsOwnRoot()
    {
        // Anti-regression for the fix itself: a name on the root Panel resolves to a NoneAutomationPeer
        // and is never announced, so re-adding it here would look like accessibility and deliver none.
        // The real name goes on the host window, in MaterialDialogs — pinned by the tests below.
        Assert.DoesNotContain("AutomationProperties.Name", RootElementTag("DialogContent.axaml"), StringComparison.Ordinal);
    }

    [Fact]
    public void FileConsentContentAxamlNoLongerSetsAutomationPropertiesOnItsOwnRoot()
    {
        Assert.DoesNotContain("AutomationProperties.Name", RootElementTag("FileConsentContent.axaml"), StringComparison.Ordinal);
    }

    [Fact]
    public void ConfirmAsyncNamesItsHostWindowAfterTheDialogsTitle()
    {
        var source = ReadView("MaterialDialogs.cs");

        var confirmAsync = ExtractMethodBody(source, "ConfirmAsync");
        Assert.Contains("AutomationProperties.SetName(window, title);", confirmAsync, StringComparison.Ordinal);
    }

    [Fact]
    public void RestoreAsyncNamesItsHostWindowAfterTheRestorePromptTitle()
    {
        var source = ReadView("MaterialDialogs.cs");

        var restoreAsync = ExtractMethodBody(source, "RestoreAsync");
        Assert.Contains(
            "AutomationProperties.SetName(window, loc[\"Restore_PromptTitle\"]);",
            restoreAsync,
            StringComparison.Ordinal);
    }

    [Fact]
    public void FileConsentAsyncNamesItsHostWindowAfterTheViewModelsTitle()
    {
        var source = ReadView("MaterialDialogs.cs");

        var fileConsentAsync = ExtractMethodBody(source, "FileConsentAsync");
        Assert.Contains("AutomationProperties.SetName(window, vm.Title);", fileConsentAsync, StringComparison.Ordinal);
    }

    /// <summary>
    /// Anti-vacuity for all three tests above: the type actually needs the import, or the calls above
    /// would not compile in the first place - but a future edit could delete the import while leaving
    /// a stray reference some other way, so pin it directly too.
    /// </summary>
    [Fact]
    public void MaterialDialogsImportsAvaloniaAutomation()
    {
        var source = ReadView("MaterialDialogs.cs");

        Assert.Contains("using Avalonia.Automation;", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// Extracts one <c>internal static async Task...</c> method's body by name, loosely enough to
    /// survive signature reflow but anchored enough that it cannot accidentally match a different
    /// method - stops at the next method-level brace closing at 4-space indent, same shape the other
    /// source-scan tests in this suite already rely on for `OnKeyDown`/`AttachEscapeDismiss`.
    /// </summary>
    private static string ExtractMethodBody(string source, string methodName)
    {
        var match = Regex.Match(
            source,
            $@"{methodName}\([^)]*\)\s*\{{(?<body>.*?)\n    \}}",
            RegexOptions.Singleline);

        Assert.True(match.Success, $"{methodName} was renamed or restructured in MaterialDialogs.cs");
        return match.Groups["body"].Value;
    }
}
