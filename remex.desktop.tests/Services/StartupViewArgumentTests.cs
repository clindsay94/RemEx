using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// <c>--view &lt;Name&gt;</c> (RemEx-8q7de) is the palette sweep's only navigation channel — it
/// replaced sending <c>Ctrl+D1..D7</c> via <c>SendKeys</c>, which is banned in this repo.
/// </summary>
/// <remarks>
/// NO <c>new ShellViewModel(...)</c> HERE, DELIBERATELY — same reasoning as
/// <c>ShellPresencePulseTests</c>' own remark: the constructor needs a full DI graph
/// (<c>DashboardLayoutService</c>, <c>ThemeService</c>, <c>HardwareThemeService</c>,
/// <c>ConnectionViewModel</c>, <c>IServiceProvider</c>, several lazily-<c>GetRequiredService</c>'d
/// view models) that nothing in this test project builds, and there is no
/// <c>new ShellViewModel(...)</c> anywhere in the suite to follow. So instead of exercising the
/// lambdas in <c>Navigators</c> against a real instance, <see cref="Navigators_BindsEachNameToItsDocumentedShellViewModelMethod"/>
/// does what <c>ShellPresencePulseTests</c> does for the same class: a source scan proving each
/// name is wired to the exact <c>ShellViewModel</c> method it claims, plus a reflection check that
/// the method still exists — together closing the gap a keys-only equality check leaves open (two
/// names silently bound to the same method, or one bound to nothing).
/// </remarks>
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

    /// <summary>
    /// Ctrl+D1..D7 / Ctrl+OemComma order (MainWindow.axaml:34-53), the same navigation calls
    /// <c>ShellViewModel</c>'s own <c>[RelayCommand]</c> methods bind to. Each pair is checked two
    /// ways: the exact source text (a name silently re-pointed at the wrong lambda) and reflection
    /// (a method renamed elsewhere without updating this table).
    /// </summary>
    [Theory]
    [InlineData("Home", "NavigateToHome")]
    [InlineData("Sensors", "NavigateToCanvas")]
    [InlineData("Commands", "NavigateToRemote")]
    [InlineData("Launcher", "NavigateToAppLauncher")]
    [InlineData("Processes", "NavigateToTaskManager")]
    [InlineData("Files", "NavigateToFileTransfer")]
    [InlineData("Logs", "NavigateToDiagnosticLogs")]
    [InlineData("Settings", "NavigateToSettings")]
    [InlineData("About", "NavigateToAbout")]
    public void Navigators_BindsEachNameToItsDocumentedShellViewModelMethod(string viewName, string methodName)
    {
        var pattern = $@"\[\s*""{Regex.Escape(viewName)}""\s*\]\s*=\s*vm\s*=>\s*vm\.{Regex.Escape(methodName)}\(\)";
        Regex.IsMatch(StartupViewArgumentSource(), pattern).Should().BeTrue(
            $"'{viewName}' must navigate via ShellViewModel.{methodName}() exactly — found no " +
            $"'[\"{viewName}\"] = vm => vm.{methodName}()' entry in StartupViewArgument.Navigators");

        typeof(ShellViewModel).GetMethod(methodName, Type.EmptyTypes).Should().NotBeNull(
            $"ShellViewModel.{methodName}() must exist and take no parameters — it may have been renamed without updating this mapping");
    }

    private static string StartupViewArgumentSource([CallerFilePath] string f = "")
        => File.ReadAllText(Path.Combine(RepoRoot(f), "remex.desktop", "Services", "StartupViewArgument.cs"));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
