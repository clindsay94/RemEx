using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Guards the Material sweep of the three input dialogs (RemEx-ckuox): SetAlertDialog,
/// SecondMetricDialog, AddProgramWindow. Source-scan only — there is no headless render.
/// </summary>
public class InputDialogsMaterialTests
{
    private const string RepoRoot = "../../../../";

    private static string ReadView(string relativePath)
    {
        var path = Path.Combine(RepoRoot, relativePath);
        File.Exists(path).Should().BeTrue($"expected {relativePath} to exist");
        return File.ReadAllText(path);
    }

    [Fact]
    public void OnlySetAlertDialogsNumericUpDownCarriesFontSize()
    {
        var setAlert = ReadView("remex.desktop/Views/SetAlertDialog.axaml");
        var secondMetric = ReadView("remex.desktop/Views/SecondMetricDialog.axaml");
        var addProgram = ReadView("remex.desktop/Views/AddProgramWindow.axaml");

        Regex.Matches(secondMetric, @"FontSize=""\d").Should().BeEmpty(
            "SecondMetricDialog has no exempt control left after the sweep");
        Regex.Matches(addProgram, @"FontSize=""\d").Should().BeEmpty(
            "AddProgramWindow has no exempt control left after the sweep");

        var setAlertMatches = Regex.Matches(setAlert, @"<([A-Za-z]+)[^>]*\bFontSize=""\d[^>]*");
        foreach (Match match in setAlertMatches)
        {
            match.Groups[1].Value.Should().Be("NumericUpDown",
                "NumericUpDown is the only exemption (App.axaml TextBox exception 4) — everything else must use a Theme key");
        }
        setAlertMatches.Count.Should().Be(1, "exactly one inline FontSize should remain, on the NumericUpDown");
    }

    [Fact]
    public void EachDialogHasExactlyOnePrimaryButtonAndNoPaintAttributesOnAnyButton()
    {
        foreach (var relativePath in new[]
                 {
                     "remex.desktop/Views/SetAlertDialog.axaml",
                     "remex.desktop/Views/SecondMetricDialog.axaml",
                     "remex.desktop/Views/AddProgramWindow.axaml",
                 })
        {
            var text = ReadView(relativePath);

            Regex.Matches(text, @"Classes=""[^""]*\bprimary\b[^""]*""").Count.Should().Be(1,
                $"{relativePath} should have exactly one Classes=\"primary\" button");

            foreach (Match match in Regex.Matches(text, @"<Button\b[^>]*"))
            {
                var tag = match.Value;
                tag.Should().NotMatch("*Background=*", $"{relativePath}: no inline Background on Button");
                tag.Should().NotMatch("*CornerRadius=*", $"{relativePath}: no inline CornerRadius on Button");
                tag.Should().NotMatch("*FontSize=*", $"{relativePath}: no inline FontSize on Button");
                tag.Should().NotMatch("*Foreground=*", $"{relativePath}: no inline Foreground on Button - the danger/success tints come from the vocabulary");
            }
        }
    }

    [Fact]
    public void DialogsDismissOnEscapeGuardsStillHold()
    {
        var setAlert = ReadView("remex.desktop/Views/SetAlertDialog.axaml");
        setAlert.Should().Contain(@"Gesture=""Escape""");

        var secondMetricCodeBehind = ReadView("remex.desktop/Views/SecondMetricDialog.axaml.cs");
        secondMetricCodeBehind.Should().Contain("Key.Escape");
    }

    [Fact]
    public void AddProgramWindowBindsHexColorInvalidState()
    {
        var addProgram = ReadView("remex.desktop/Views/AddProgramWindow.axaml");
        addProgram.Should().Contain(@"Classes.invalid=""{Binding !IsHexColorValid}""");
    }

    [Theory]
    [InlineData("zzz", false)]
    [InlineData("#FF8800", true)]
    public void IsHexColorValidTracksHexColor(string hex, bool expectedValid)
    {
        var vm = new AddProgramViewModel(new FakeIconExtractionService());

        vm.HexColor = hex;

        vm.IsHexColorValid.Should().Be(expectedValid);
    }

    private sealed class FakeIconExtractionService : Remex.Core.Services.IIconExtractionService
    {
        public string ExtractIconAsBase64(string filePath) => string.Empty;
    }
}
