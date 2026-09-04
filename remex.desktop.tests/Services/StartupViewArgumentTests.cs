using System.Collections.Generic;
using FluentAssertions;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// <c>--view &lt;Name&gt;</c> (RemEx-8q7de) is the palette sweep's only navigation channel — it
/// replaced sending <c>Ctrl+D1..D7</c> via <c>SendKeys</c>, which is banned in this repo. These
/// tests cover the pure parsing and the completeness of the name-to-navigation mapping; they do not
/// construct a real <see cref="Remex.Desktop.ViewModels.ShellViewModel"/> (that needs a full DI
/// graph) so they never actually invoke a <c>Navigators</c> delegate.
/// </summary>
public class StartupViewArgumentTests
{
    [Theory]
    [InlineData(new[] { "--view", "Home" }, "Home")]
    [InlineData(new[] { "--view", "Settings" }, "Settings")]
    [InlineData(new[] { "--minimized", "--view", "Logs" }, "Logs")]
    [InlineData(new[] { "--VIEW", "Sensors" }, "Sensors")] // flag itself is case-insensitive
    [InlineData(new[] { "--view", "Nonsense" }, "Nonsense")] // returned as-is; caller validates
    public void ExtractRequestedViewName_ReadsTheValueAfterTheFlag(string[] args, string expected)
    {
        StartupViewArgument.ExtractRequestedViewName(args).Should().Be(expected);
    }

    [Fact]
    public void ExtractRequestedViewName_ReturnsNullForEmptyArgs()
    {
        StartupViewArgument.ExtractRequestedViewName(new string[0]).Should().BeNull();
    }

    [Fact]
    public void ExtractRequestedViewName_ReturnsNullWhenTheFlagIsAbsent()
    {
        StartupViewArgument.ExtractRequestedViewName(new[] { "--minimized" }).Should().BeNull();
    }

    [Fact]
    public void ExtractRequestedViewName_ReturnsNullWhenTheFlagHasNoValue()
    {
        StartupViewArgument.ExtractRequestedViewName(new[] { "--view" }).Should().BeNull();
    }

    [Fact]
    public void ExtractRequestedViewName_ReturnsNullForNullArgs()
    {
        StartupViewArgument.ExtractRequestedViewName(null).Should().BeNull();
    }

    /// <summary>
    /// Ctrl+D1..D7 / Ctrl+OemComma order (MainWindow.axaml:34-53), plus About which has no
    /// keybinding at all. RemoteDesktop is deliberately absent — it stays a manual sweep cell.
    /// </summary>
    [Fact]
    public void Navigators_CoversExactlyTheNineScriptableViews()
    {
        var expected = new[]
        {
            "Home", "Sensors", "Commands", "Launcher", "Processes", "Files", "Logs", "Settings", "About",
        };

        StartupViewArgument.Navigators.Keys.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public void Navigators_IsCaseInsensitive()
    {
        StartupViewArgument.Navigators.Should().ContainKey("home");
        StartupViewArgument.Navigators.Should().ContainKey("HOME");
    }

    [Fact]
    public void Navigators_DoesNotIncludeRemoteDesktop()
    {
        StartupViewArgument.Navigators.Should().NotContainKey("RemoteDesktop");
    }

    [Fact]
    public void TryApply_ReturnsFalseWhenNoViewFlagIsPresent()
    {
        StartupViewArgument.TryApply(new[] { "--minimized" }, viewModel: null!).Should().BeFalse();
    }

    [Fact]
    public void TryApply_ReturnsFalseForAnUnrecognisedName()
    {
        StartupViewArgument.TryApply(new[] { "--view", "Nonsense" }, viewModel: null!).Should().BeFalse();
    }
}
