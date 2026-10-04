using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using FluentAssertions;
using Remex.Core.Models;
using Remex.Desktop.Services;

namespace Remex.Desktop.Render.Tests;

/// <summary>
/// A label written as its own TextBlock inside a filled (.primary) button must use the button's
/// on-colour, not the surface ink (RemEx-pp4cm.23: Home's "Pair a phone" read faintly on its fill in
/// several palette-sweep cells). App.axaml's global "TextBlock" rule paints every TextBlock with
/// TextPrimaryBrush, and an explicit TextBlock does not inherit the button's Foreground past it.
/// </summary>
public sealed class FilledButtonLabelRenderTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void APrimaryButtonsOwnTextBlock_UsesTheButtonsOnColour(bool light)
    {
        new ThemeService().ApplyCustomization(new CustomizationSettings
        {
            ThemeMode = light ? "Light" : "Dark",
        });
        Dispatcher.UIThread.RunJobs();

        var label = new TextBlock { Text = "Pair a phone" };
        var button = new Button
        {
            Classes = { "primary", "pill" },
            Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { label } },
        };
        var window = new Window { Width = 400, Height = 200, Content = button };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var on = ((ISolidColorBrush)button.Foreground!).Color;
        var fill = ((ISolidColorBrush)button.Background!).Color;
        ((ISolidColorBrush)label.Foreground!).Color.Should().Be(on,
            "the label sits on the accent fill, so it takes the fill's on-colour");
        on.Should().NotBe(fill);
        window.Close();
    }
}
