using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Localization;

/// <summary>
/// Guards the Diagnostics "system event logs" branch localized in RemEx-rg9in
/// (<see cref="Remex.Desktop.ViewModels.DiagnosticLogsViewModel.FetchServiceLogsAsync"/> and
/// <see cref="Remex.Desktop.ViewModels.DiagnosticLogsViewModel.DescribeLinuxJournal"/>).
/// </summary>
/// <remarks>
/// The tab HEADERS were already localized while every string the branch actually SHOWS —
/// the reading placeholder, both empty states, the two error lines — was a raw English literal,
/// so a translated header sat over untranslated content. This file has two independent guards:
/// one that the branch's user-visible assignments no longer contain a bare English literal, and
/// one that the six keys the fix introduces exist in every locale file, not only the neutral one.
/// </remarks>
public class ServiceLogsLocalizationTests
{
    /// <summary>The keys this bead added, so the parity check below is exact rather than fuzzy.</summary>
    private static readonly string[] NewKeys =
    [
        "Logs_Service_Reading",
        "Logs_Service_WindowsEmpty",
        "Logs_Service_JournalFailed",
        "Logs_Service_Unsupported",
        "Logs_Service_ReadError",
        "Logs_Service_LinuxEmpty",
    ];

    /// <summary>
    /// <c>ServiceLogsText = "..."</c> / <c>ServiceLogsText = $"..."</c> is exactly the shape every
    /// literal in this branch used to take. If either ever reappears it means a hardcoded English
    /// sentence replaced a resource lookup — the injection proof for this test flips this very
    /// assertion by reinstating one such literal.
    /// </summary>
    [Fact]
    public void ServiceLogsText_IsNeverAssignedARawStringLiteral()
    {
        var path = ViewModelPath();
        File.Exists(path).Should().BeTrue($"the view model must be readable at {path}");

        var text = File.ReadAllText(path);
        var offenders = Regex.Matches(text, @"ServiceLogsText\s*=\s*\$?""[^""]*[A-Za-z]{3,}[^""]*""")
            .Select(m => m.Value)
            .ToList();

        offenders.Should().BeEmpty(
            "every ServiceLogsText assignment in the ServiceLogs branch must come from " +
            "LocalizationService.Instance[...], not a hardcoded English sentence (RemEx-rg9in)");
    }

    /// <summary>Every key this bead added must be defined in the neutral resx and all 8 locales.</summary>
    [Fact]
    public void EveryNewServiceLogsKey_IsDefinedInAllNineResxFiles()
    {
        var localeDirectory = Path.Combine(RepoRoot(), "remex.desktop", "Localization");
        var resxFiles = Directory.GetFiles(localeDirectory, "Strings*.resx");
        resxFiles.Should().HaveCountGreaterOrEqualTo(9, "the base resx plus 8 locale variants must all be on disk");

        var missing = new List<string>();
        foreach (var path in resxFiles)
        {
            var defined = XDocument.Load(path)
                .Root!
                .Elements("data")
                .Select(d => (string?)d.Attribute("name"))
                .Where(name => !string.IsNullOrEmpty(name))
                .ToHashSet(StringComparer.Ordinal);

            foreach (var key in NewKeys)
            {
                if (!defined.Contains(key))
                    missing.Add($"{key} missing from {Path.GetFileName(path)}");
            }
        }

        missing.Should().BeEmpty(
            "a key missing from even one locale file renders as its own name in that language " +
            "(LocalizationService's indexer ends in '?? key')");
    }

    private static string ViewModelPath([CallerFilePath] string thisSourceFile = "")
        => Path.Combine(RepoRoot(thisSourceFile), "remex.desktop", "ViewModels", "DiagnosticLogsViewModel.cs");

    /// <summary>
    /// The repository root, resolved from THIS source file rather than from the test assembly.
    /// </summary>
    /// <remarks>
    /// Walking up from the assembly couples the test to build output living inside the repo, so
    /// building with <c>--artifacts-path</c> elsewhere breaks it with an error that says nothing
    /// about the change that caused it — see RemEx-6i1l, where exactly that happened.
    /// </remarks>
    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
    {
        // <repo>/remex.desktop.tests/Localization/ThisFile.cs -> <repo>
        var directory = Path.GetDirectoryName(thisSourceFile)!;
        return Path.GetFullPath(Path.Combine(directory, "..", ".."));
    }
}
