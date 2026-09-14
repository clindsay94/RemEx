using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// RemEx-4kv0g.4.3: <c>SelectedTabIndex</c> is remembered for the run (closing and reopening the
/// sheet lands on the last tab) but never persisted, and never feeds <c>BuildCurrentSettings</c>.
/// Source-text, like its siblings (<see cref="CustomizationSettingsRoundTripTests"/>) —
/// <c>CustomizationViewModel</c> cannot be constructed here (it takes a shell view model, a
/// dashboard layout service and a theme service, none of which stand up outside a running app).
/// </summary>
public class CustomizationTabIndexTests
{
    [Fact]
    public void SelectedTabIndexDefaultsToZero()
    {
        // [ObservableProperty] backs SelectedTabIndex with this field; an uninitialized `int` field
        // defaults to 0 (Colour, the first tab) - a regression here would be an explicit
        // initializer overriding that, which this pins against.
        var source = ViewModelSource();

        source.Should().Contain("private int _selectedTabIndex;",
            "SelectedTabIndex must default to 0 (Colour) - the bare declaration has no explicit initializer");
    }

    [Fact]
    public void BuildCurrentSettingsNeverReadsSelectedTabIndex()
    {
        // Deliberately excluded (spec 2026-09-13-personalize-tabs §1): which tab is open is a
        // run-only UI convenience, not a persisted customization field.
        ApplyAndSaveInitializer().Should().NotContain("SelectedTabIndex",
            "SelectedTabIndex is remembered for the run only - it must never reach the persisted record");
    }

    private static string ViewModelSource() => File.ReadAllText(Path.Combine(RepoRoot(),
        "remex.desktop", "ViewModels", "CustomizationViewModel.cs"));

    /// <summary>The object-initializer body of <c>BuildCurrentSettings</c> (same anchor as
    /// <see cref="CustomizationSettingsRoundTripTests"/>'s helper of the same name).</summary>
    private static string ApplyAndSaveInitializer()
    {
        var match = Regex.Match(ViewModelSource(),
            @"private CustomizationSettings BuildCurrentSettings\(\)\s*\{.*?return new CustomizationSettings\s*\{(.*?)\n        \};",
            RegexOptions.Singleline);

        match.Success.Should().BeTrue(
            "BuildCurrentSettings's initializer moved or was reshaped — re-point this test rather than deleting it");
        return match.Groups[1].Value;
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
