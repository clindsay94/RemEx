using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Remex.Desktop.Services;

namespace Remex.Desktop.Render.Tests;

/// <summary>
/// RemEx-flmfi. Material 3.19.0's clear button on a <c>TextBox.clearButton</c> ships an English
/// "Click to clear field" tip and no accessible name. App.axaml overrides both with a localized
/// string; this renders the real template so a renamed part or a lost priority contest goes red.
/// </summary>
public sealed class SearchClearButtonRenderTests
{
    [AvaloniaFact]
    public void TheClearButtonCarriesTheLocalizedTipAndAccessibleName()
    {
        var box = new TextBox { Text = "report", Width = 220 };
        box.Classes.Add("clearButton");
        var window = new Window { Width = 320, Height = 120, Content = box };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();

            var button = box.GetVisualDescendants().OfType<Button>()
                .SingleOrDefault(b => b.Name == "PART_ClearButton");
            button.Should().NotBeNull("Material must still render its clear button as Button#PART_ClearButton for a non-empty clearButton box");

            var expected = LocalizationService.Instance["A11y_ClearSearch"];
            expected.Should().NotBeNullOrWhiteSpace().And.NotBe("A11y_ClearSearch");

            ToolTip.GetTip(button!).Should().Be(expected, "Material's own tip is English-only");
            AutomationProperties.GetName(button!).Should().Be(expected, "a screen reader must not announce an unlabelled button");
        }
        finally
        {
            window.Close();
        }
    }
}
