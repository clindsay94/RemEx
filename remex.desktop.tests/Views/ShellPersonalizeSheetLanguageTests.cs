using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Personalize is for looks only (RemEx-172ba): the language picker lives in Settings › General and
/// nowhere else, so the side sheet in ShellView must not carry a second one.
/// </summary>
public class ShellPersonalizeSheetLanguageTests
{
    [Fact]
    public void ThePersonalizeSheetHasNoLanguagePicker()
    {
        var shell = Read("remex.desktop", "Views", "ShellView.axaml");
        var sheet = Regex.Match(shell, @"<material:SideSheet\b[^>]*Name=""SettingsSideSheet""[\s\S]*?</material:SideSheet>");
        sheet.Success.Should().BeTrue("the Personalize side sheet has to exist for this check to mean anything");

        sheet.Value.Should().NotContain("AvailableLanguages");
        sheet.Value.Should().NotContain("Settings_Language");
    }

    [Fact]
    public void SettingsStillHasTheLanguagePicker()
    {
        Read("remex.desktop", "Views", "SettingsView.axaml")
            .Should().Contain("ItemsSource=\"{Binding AvailableLanguages}\"");
    }

    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine(RepoRoot(), Path.Combine(parts)));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
