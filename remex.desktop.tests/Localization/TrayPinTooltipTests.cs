using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.Localization;

/// <summary>
/// Tray_Pin / Tray_Unpin were translated into all nine locales in 689b61b but never bound to
/// anything, so the tray flyout's pin toggle had no tooltip in any language. This guards the fix:
/// the key selection in <see cref="TrayFlyoutViewModel.PinTooltipKey"/>, and that the view actually
/// wires the resulting property to the toggle button.
/// </summary>
public class TrayPinTooltipTests
{
    [Theory]
    [InlineData(false, "Tray_Pin")]
    [InlineData(true, "Tray_Unpin")]
    public void PinTooltipKey_SwitchesWithIsPinned(bool isPinned, string expectedKey)
    {
        TrayFlyoutViewModel.PinTooltipKey(isPinned).Should().Be(expectedKey);
    }

    [Fact]
    public void PinToggleButton_BindsPinTooltip()
    {
        var path = Path.Combine(RepoRoot(), "remex.desktop", "Views", "TrayFlyoutWindow.axaml");
        File.Exists(path).Should().BeTrue($"the tray flyout view must exist at {path}");

        var source = File.ReadAllText(path);

        // Anti-vacuity: the ToggleButton element itself must be findable before asserting anything
        // about its attributes, or a rename of the element would make this test pass for the wrong
        // reason (AGENTS.md).
        var toggleButton = Regex.Match(
            source,
            @"<ToggleButton\b[^>]*AutomationProperties\.Name=""\{local:Localize A11y_TrayFlyout_Pin\}""[^>]*/?>",
            RegexOptions.Singleline);

        toggleButton.Success.Should().BeTrue(
            "the pin ToggleButton (identified by its A11y_TrayFlyout_Pin automation name) must be " +
            "present in TrayFlyoutWindow.axaml, or this test passes vacuously");

        toggleButton.Value.Should().Contain(
            "{Binding PinTooltip}",
            "the pin toggle's tooltip must be bound to PinTooltip so it switches with IsPinned and " +
            "the current language");
    }

    [Theory]
    [InlineData("Tray_Pin")]
    [InlineData("Tray_Unpin")]
    public void BaseResx_DefinesBothTooltipKeys(string key)
    {
        var path = Path.Combine(RepoRoot(), "remex.desktop", "Localization", "Strings.resx");
        File.Exists(path).Should().BeTrue($"the base resx must exist at {path}");

        var keys = XDocument.Load(path)
            .Root!
            .Elements("data")
            .Select(d => (string?)d.Attribute("name"))
            .Where(name => !string.IsNullOrEmpty(name))
            .ToHashSet(StringComparer.Ordinal);

        keys.Should().NotBeEmpty("the resx parse must actually find keys, or this test is vacuous");
        keys.Should().Contain(key);
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
    {
        var directory = Path.GetDirectoryName(thisSourceFile)!;
        return Path.GetFullPath(Path.Combine(directory, "..", ".."));
    }
}
