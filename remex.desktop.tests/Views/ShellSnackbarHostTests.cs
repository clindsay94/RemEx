using System.Linq;
using System.Text.RegularExpressions;
using Material.Icons;
using Remex.Desktop.Services;
using Remex.Desktop.Views;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// The shell's in-app toast moved off Avalonia's <c>WindowNotificationManager</c> /
/// <c>NotificationCard</c> onto a Material <c>SnackbarHost</c> declared in <c>ShellView.axaml</c>
/// (RemEx-uedna).
/// </summary>
/// <remarks>
/// A source scan, matching <see cref="ShellDrawerOverlayTests"/>: there is no headless render here,
/// and a misplaced or mis-sized <c>SnackbarHost</c> throws nothing - it just quietly sits under the
/// gear FAB or swallows clicks over the content area, which is exactly what the bead's acceptance
/// criterion ("snackbars appear above the FAB rather than under it") is about.
/// </remarks>
public class ShellSnackbarHostTests
{
    private static string ShellMarkup() => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "remex.desktop", "Views", "ShellView.axaml"));

    private static string ShellCodeBehind() => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "remex.desktop", "Views", "ShellView.axaml.cs"));

    [Fact]
    public void TheShellDeclaresASnackbarHost()
    {
        Assert.Contains("material:SnackbarHost", ShellMarkup(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheNotificationCardStyleIsGoneNowNothingUsesIt()
    {
        // The old toast surface. Leaving its Style override in App.axaml would style a control the
        // app no longer instantiates anywhere.
        var appMarkup = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
                "remex.desktop", "App.axaml"));

        Assert.DoesNotContain("Selector=\"NotificationCard\"", appMarkup, StringComparison.Ordinal);
        // Not a blanket string search: the migration is explained in prose comments (deliberately, so
        // the reason a Style override disappeared from App.axaml is not lost) and those legitimately
        // still name the old type. Only an actual instantiation would mean the toast surface reverted.
        Assert.DoesNotContain("new WindowNotificationManager", ShellCodeBehind(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheSnackbarHostIsAnchoredClearOfTheGearFab()
    {
        // The FAB is Width/Height=52 at Margin 0,0,20,20 (bottom 20 to bottom 72). The snackbar's own
        // bottom margin must clear that footprint entirely, not merely differ from it - 88 = 20 (FAB
        // margin) + 52 (FAB height) + 16 (gap).
        Assert.Matches(
            new Regex(@"material:SnackbarHost[^>]*Margin=""0,0,20,88"""),
            ShellMarkup());
    }

    [Fact]
    public void TheSnackbarHostSizesToItsContentInsteadOfStretching()
    {
        // A ContentControl stretched over the row would hit-test the whole area even with nothing
        // showing, because Material's own control theme sets Background="Transparent" and Avalonia
        // hit-tests a transparent brush the same as an opaque one. Right/Bottom alignment collapses
        // the control to its content's size - zero, when no toast is up.
        // Negative lookbehind so this doesn't accidentally pass on SnackbarHorizontalAlignment /
        // SnackbarVerticalAlignment alone - those control WHERE inside the host a toast anchors, not
        // whether the host itself stretches over the row.
        var xaml = ShellMarkup();
        Assert.Matches(new Regex(@"material:SnackbarHost[^>]*(?<!Snackbar)HorizontalAlignment=""Right"""), xaml);
        Assert.Matches(new Regex(@"material:SnackbarHost[^>]*(?<!Snackbar)VerticalAlignment=""Bottom"""), xaml);
    }

    [Fact]
    public void MultipleToastsCanStackInsteadOfReplacingEachOther()
    {
        // SnackbarMaxCounts defaults to 1 in Material.Avalonia. Matches the old
        // WindowNotificationManager.MaxItems so the stacking behaviour is not a regression.
        Assert.Matches(
            new Regex(@"material:SnackbarHost[^>]*SnackbarMaxCounts=""3"""),
            ShellMarkup());
    }

    [Fact]
    public void TheHostNameInMarkupMatchesTheConstTheSinkPostsTo()
    {
        // Plain literal in markup, not x:Static - XamlIl cannot resolve a static member of the very
        // class its own code-behind partial is compiling (AVLN2000, measured while building this
        // bead). This test is the guard against the two drifting apart instead.
        Assert.Contains(
            $"HostName=\"{ShellView.ShellSnackbarHostName}\"",
            ShellMarkup(),
            StringComparison.Ordinal);
        Assert.Equal("ShellSnackbar", ShellView.ShellSnackbarHostName);
    }

    [Theory]
    [InlineData(NotificationImportance.Problem, MaterialIconKind.AlertCircleOutline, "SystemErrorBrush")]
    [InlineData(NotificationImportance.Outcome, MaterialIconKind.CheckCircleOutline, "SystemSuccessBrush")]
    [InlineData(NotificationImportance.Informational, MaterialIconKind.InformationOutline, "TextSecondaryBrush")]
    public void EachImportanceGetsItsOwnIconAndBrushKey(
        NotificationImportance importance, MaterialIconKind expectedIcon, string expectedBrushKey)
    {
        // A snackbar's default content template is a bare string with no notion of severity - unlike
        // the old NotificationCard, which is why this mapping exists at all. Distinct per importance,
        // or the distinction the retired style used to draw is gone.
        var (icon, brushKey) = SnackbarSeverityMapping.For(importance);

        Assert.Equal(expectedIcon, icon);
        Assert.Equal(expectedBrushKey, brushKey);
    }

    [Fact]
    public void NoTwoImportancesShareABrushKey()
    {
        var keys = new[]
        {
            SnackbarSeverityMapping.For(NotificationImportance.Problem).BrushKey,
            SnackbarSeverityMapping.For(NotificationImportance.Outcome).BrushKey,
            SnackbarSeverityMapping.For(NotificationImportance.Informational).BrushKey,
        };

        Assert.Equal(keys.Length, keys.Distinct().Count());
    }
}
