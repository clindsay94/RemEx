using System;
using System.IO;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Remex.Desktop.Views;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// The second-metric picker's Set button must never turn "nothing highlighted" into "clear the overlay".
/// </summary>
public class SecondMetricDialogTests
{
    private static readonly string[] Sensors = ["CPU Load", "CPU Temp", "GPU Load"];

    [Fact]
    public void AfterFilterKeepsASurvivingPick() =>
        SecondMetricSelection.AfterFilter("CPU Temp", new[] { "CPU Load", "CPU Temp" }).Should().Be("CPU Temp");

    [Fact]
    public void AfterFilterFallsBackToTheFirstMatchWhenThePickIsFilteredOut() =>
        SecondMetricSelection.AfterFilter("GPU Load", new[] { "CPU Load", "CPU Temp" }).Should().Be("CPU Load");

    [Fact]
    public void AfterFilterFallsBackToTheFirstMatchWithNoPreviousPick() =>
        SecondMetricSelection.AfterFilter(null, Sensors).Should().Be("CPU Load");

    [Fact]
    public void AfterFilterIsNullWhenNothingMatches() =>
        SecondMetricSelection.AfterFilter("CPU Temp", Array.Empty<string>()).Should().BeNull();

    [Fact]
    public void SetOnlyAppliesARealSelectionAndIsDisabledWithoutOne()
    {
        var code = Source("SecondMetricDialog.axaml.cs");
        code.Should().Contain("if (SensorList.SelectedItem is string name)");
        code.Should().NotContain("=> Apply(SensorList.SelectedItem as string)",
            "no path forwards a null selection to the caller as a Clear");
        code.Should().Contain("SetButton.IsEnabled = SensorList.SelectedItem is string");
        code.Should().Contain("SensorList.SelectionChanged +=");
        code.Should().Contain("SecondMetricSelection.AfterFilter(previous, _filtered)");

        var xaml = Source("SecondMetricDialog.axaml");
        xaml.Should().Contain("x:Name=\"SetButton\"");
        xaml.Should().Contain("x:Name=\"ClearButton\"");
    }

    [Fact]
    public void ClearIsDisabledWhenTheCardHasNoSecondMetricAndTheListScrollsToTheCurrentOne()
    {
        var code = Source("SecondMetricDialog.axaml.cs");
        code.Should().Contain("ClearButton.IsEnabled = current is not null;");
        code.Should().Contain("SearchBox.Focus();");
        code.Should().Contain("SensorList.ScrollIntoView(selected);");
        code.Should().Contain("Avalonia.Input.Key.Down");
    }

    private static string Source(string file, [CallerFilePath] string thisFile = "") =>
        File.ReadAllText(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "remex.desktop", "Views", file));
}
