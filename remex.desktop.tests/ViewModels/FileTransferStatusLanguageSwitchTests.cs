using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// The File Transfer status line follows a live language switch (sweep D7).
/// </summary>
/// <remarks>
/// Every status message used to be formatted once into a frozen string, so a user who switched to
/// Español mid-session kept reading "Renaming report.pdf to notes.pdf…" in English until the next
/// operation overwrote it. The repo rule is that UI-bound properties never hold a pre-formatted
/// string; the view model now keeps the recipe and replays it when the language changes.
/// </remarks>
public class FileTransferStatusLanguageSwitchTests
{
    [Fact]
    public void ALocalizedStatusIsReWordedWhenTheLanguageChanges()
    {
        using var connection = new ConnectionViewModel(null);
        using var vm = new FileTransferViewModel(connection);
        var original = LocalizationService.Instance.CultureTag;
        try
        {
            LocalizationService.Instance.SetCulture("en");
            var name = "report.pdf";
            vm.SetStatus(() => string.Format(
                LocalizationService.Instance["FileTransfer_RenamingFormat"], name, "notes.pdf"));
            var english = vm.StatusText;

            LocalizationService.Instance.SetCulture("es");
            // The live path posts this to the UI thread; a unit test has none, so run it inline.
            vm.ApplyLocaleChange();
            var spanish = vm.StatusText;

            spanish.Should().Be(string.Format(
                LocalizationService.Instance["FileTransfer_RenamingFormat"], "report.pdf", "notes.pdf"));
            spanish.Should().NotBe(english, "anti-vacuity: the two languages must actually differ here");
            spanish.Should().Contain("report.pdf").And.Contain("notes.pdf");
        }
        finally
        {
            LocalizationService.Instance.SetCulture(original);
        }
    }

    [Fact]
    public void AHostMessageIsNotReplacedByAnOlderLocalizedOne()
    {
        // A direct assignment (here, the host's own words) is not ours to translate, and it must
        // also stop a language switch from bringing back the message it replaced.
        using var connection = new ConnectionViewModel(null);
        using var vm = new FileTransferViewModel(connection);
        var original = LocalizationService.Instance.CultureTag;
        try
        {
            LocalizationService.Instance.SetCulture("en");
            vm.SetStatus(() => LocalizationService.Instance["FileTransfer_RenameComplete"]);
            vm.StatusText = "The phone says: storage is full.";

            LocalizationService.Instance.SetCulture("fr");
            vm.ApplyLocaleChange();

            vm.StatusText.Should().Be("The phone says: storage is full.");
        }
        finally
        {
            LocalizationService.Instance.SetCulture(original);
        }
    }

    [Fact]
    public void NoStatusAssignmentFreezesALocalizedString()
    {
        // The source-level half: every localized status goes through SetStatus. A direct
        // `StatusText = string.Format(...)` or `StatusText = LocalizationService...` is the defect.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Remex.sln")))
            dir = dir.Parent;
        dir.Should().NotBeNull();
        var source = File.ReadAllText(Path.Combine(
            dir!.FullName, "remex.desktop", "ViewModels", "FileTransferViewModel.cs"));

        Regex.Matches(source, @"SetStatus\(\(\) =>").Count.Should().BeGreaterThan(30,
            "anti-vacuity: the scan must be looking at the file that sets these messages");

        var frozen = Regex.Matches(source, @"StatusText\s*=\s*(?!string\.Empty|ex\.HostMessage|render\(\))[^;]*?(string\.Format|LocalizationService|\$"")")
            .Select(m => m.Value)
            .ToArray();
        frozen.Should().BeEmpty("a frozen localized string stays in the old language after a switch");
    }
}
