using System.IO;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Guards the nav rail's move from nine hand-rolled <c>Button</c>s to Material
/// <c>ListBoxItem</c>s (RemEx-zi3ua).
/// </summary>
/// <remarks>
/// Everything here is a markup scan — there is no headless Avalonia harness in this repo (see
/// AGENTS.md and <c>ButtonVocabularyTests</c>/<c>MaterialIconAdoptionTests</c>, which do the same).
/// These tests prove the XAML SHAPE the bead's acceptance criteria depend on — that the
/// class-toggling code is gone, that selection state is real <c>IsSelected</c> rather than a
/// <c>Classes</c> hack, and that every destination kept its accessible name — not the runtime
/// click/keyboard behaviour itself. Named for what they actually check, not for the acceptance
/// criterion they support: a markup scan named e.g. "ArrowKeysMoveThroughDestinations" would be
/// exactly the "test whose name makes a runtime claim its body cannot back up" shape this bead's
/// handoff warned against.
/// </remarks>
public class ShellNavListTests
{
    private static string ShellViewXaml()
        => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "ShellView.axaml"));

    /// <summary>
    /// The hand-rolled selection state the bead names explicitly: nine
    /// <c>Classes.nav-item-active</c> bindings, each re-deriving "am I the active one" from
    /// <c>ActiveNavIndex</c> by hand. If this string is back in the file, the ListBox migration was
    /// reverted or bypassed.
    /// </summary>
    [Fact]
    public void ShellView_NoLongerTogglesAClassForTheActiveDestination()
    {
        // The attribute usage, not the bare name — the surrounding XAML's own comments now discuss
        // the retired pattern by name (including in this file's own doc comments), and a substring
        // match on the name alone would fail on prose that mentions history rather than code that
        // revived it.
        ShellViewXaml().Should().NotContain("Classes.nav-item-active=\"",
            "selection state belongs to the ListBoxItem now (IsSelected), not a hand-toggled class");
    }

    /// <summary>
    /// Each of the nine destinations has to survive as a <c>ListBoxItem</c> carrying: the
    /// <c>Tag</c> that <c>ShellView.axaml.cs</c>'s <c>OnNavSelectionChanged</c> reads to find the
    /// matching <c>NavigateToX</c> command, the accessible name the bead's acceptance criteria
    /// required kept, and a one-way <c>IsSelected</c> binding against the same
    /// <c>ActiveNavIndex</c> the old <c>Classes.nav-item-active</c> bindings compared against.
    /// </summary>
    [Theory]
    [InlineData("Nav_Home", 0)]
    [InlineData("Nav_Sensors", 1)]
    [InlineData("Nav_Commands", 2)]
    [InlineData("Nav_Launcher", 3)]
    [InlineData("Nav_Processes", 4)]
    [InlineData("Nav_Files", 7)]
    [InlineData("Shell_LogsDiagnostics", 8)]
    [InlineData("Nav_Settings", 9)]
    [InlineData("Shell_About", 6)]
    public void EveryDestination_IsAListBoxItemWithItsAccessibleNameAndSelectionBinding(string localizationKey, int navIndex)
    {
        var shell = ShellViewXaml();

        shell.Should().Contain($"Tag=\"{navIndex}\"",
            $"OnNavSelectionChanged reads each item's Tag to find the NavigateTo command for index {navIndex}");
        shell.Should().Contain($"AutomationProperties.Name=\"{{conv:Localize {localizationKey}}}\"",
            $"{localizationKey}'s accessible name must survive the ListBox migration");
        shell.Should().Contain(
            $"IsSelected=\"{{Binding ActiveNavIndex, Converter={{x:Static ObjectConverters.Equal}}, ConverterParameter={navIndex}, Mode=OneWay}}\"",
            $"index {navIndex}'s active state has to be real ListBoxItem selection, one-way from ActiveNavIndex");
    }

    /// <summary>
    /// Ripple and the hover state layer are Material's <c>ListBoxItem</c> control theme's own
    /// (confirmed against Material.Avalonia 3.19.0's own template source, not merely the DLL — see
    /// the comment above these styles in ShellView.axaml). What this app still has to supply itself
    /// is the accent-tinted selected fill/foreground and the per-state icon recolour, since the
    /// base theme has no idea what this app's accent colour is.
    /// </summary>
    [Fact]
    public void TheListBoxItemStyles_CoverBaseHoverAndSelectedStates()
    {
        var shell = ShellViewXaml();

        foreach (var selector in new[]
                 {
                     "ListBoxItem.nav-item",
                     "ListBoxItem.nav-item:pointerover",
                     "ListBoxItem.nav-item:selected",
                     "ListBoxItem.nav-item mi|MaterialIcon",
                     "ListBoxItem.nav-item:pointerover mi|MaterialIcon",
                     "ListBoxItem.nav-item:selected mi|MaterialIcon",
                 })
        {
            shell.Should().Contain($"Selector=\"{selector}\"",
                "the nav list's ripple and base hover state layer come from the ListBoxItem control " +
                "theme itself, but the accent fill/foreground per state is this app's own style and " +
                "has to name the selector that theme actually exposes");
        }
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
