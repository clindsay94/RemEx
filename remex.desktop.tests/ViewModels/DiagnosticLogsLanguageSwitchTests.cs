using System.Linq;
using FluentAssertions;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>The Logs Preset and Export-scope menus follow a live language switch (PC-3.5).</summary>
public class DiagnosticLogsLanguageSwitchTests
{
    [Fact]
    public void PresetAndScopeNamesChangeLanguage_AndKeepTheirSelections()
    {
        var original = LocalizationService.Instance.CultureTag;
        try
        {
            LocalizationService.Instance.SetCulture("en");
            var vm = new DiagnosticLogsViewModel(null!);
            vm.SelectedPreset = vm.Presets[2];
            vm.SelectedExportScope = vm.ExportScopes[4];
            var english = vm.Presets[2].Name;
            var visibleBefore = vm.VisibleEntries.Count;

            LocalizationService.Instance.SetCulture("es");

            var spanish = LocalizationService.Instance["Logs_PresetConnection"];
            spanish.Should().NotBe(english, "anti-vacuity: the two languages must differ");
            vm.Presets[2].Name.Should().Be(spanish);
            vm.SelectedPreset.Should().BeSameAs(vm.Presets[2]);
            vm.SelectedExportScope.Should().BeSameAs(vm.ExportScopes[4]);
            vm.ExportScopes.Select(s => s.Name).Should().Contain(spanish);
            vm.VisibleEntries.Count.Should().Be(visibleBefore);
        }
        finally
        {
            LocalizationService.Instance.SetCulture(original);
        }
    }
}
