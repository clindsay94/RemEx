using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Remex.Core.Models;
using Remex.Desktop.Controls;
using Remex.Desktop.Services;

namespace Remex.Desktop.Render.Tests;

/// <summary>
/// The busy placeholder's states, with its real template and styles applied (RemEx-pp4cm.21/.22).
/// </summary>
/// <remarks>
/// The control's styles used to target <c>ContentControl /template/ ...</c>. A bare type in an
/// Avalonia selector matches that exact style key only, and a <see cref="BusyPlaceholder"/>'s style
/// key is its own type, so not one rule ever matched: the content, the spinner AND the four skeleton
/// bars all drew at once, forever. That read as "the Processes list never stops loading" and as
/// "outlined bars around every spinner". The XAML-text tests in remex.desktop.tests could not see it.
/// </remarks>
public sealed class BusyPlaceholderRenderTests
{
    private static (Window Window, BusyPlaceholder Placeholder, TextBlock Content) Open(BusyPlaceholderMode mode, bool busy)
    {
        var content = new TextBlock { Text = "row" };
        var placeholder = new BusyPlaceholder { Mode = mode, IsBusy = busy, Content = content };
        var window = new Window { Width = 400, Height = 300, Content = placeholder };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, placeholder, content);
    }

    private static Control Part(BusyPlaceholder placeholder, string name) =>
        placeholder.GetVisualDescendants().OfType<Control>().Single(c => c.Name == name);

    [AvaloniaTheory]
    [InlineData(BusyPlaceholderMode.Skeleton)]
    [InlineData(BusyPlaceholderMode.Spinner)]
    public void NotBusy_ShowsOnlyTheContent(BusyPlaceholderMode mode)
    {
        var (window, placeholder, content) = Open(mode, busy: false);

        Part(placeholder, "PART_Spinner").IsVisible.Should().BeFalse("nothing is loading");
        Part(placeholder, "PART_Skeleton").IsVisible.Should().BeFalse("nothing is loading");
        content.IsEffectivelyVisible.Should().BeTrue();
        Part(placeholder, "PART_ContentPresenter").Opacity.Should().Be(1);
        window.Close();
    }

    [AvaloniaFact]
    public void BusySkeleton_ShowsTheBarsOnly_AndClearsWhenTheDataArrives()
    {
        var (window, placeholder, content) = Open(BusyPlaceholderMode.Skeleton, busy: true);

        Part(placeholder, "PART_Skeleton").IsVisible.Should().BeTrue();
        Part(placeholder, "PART_Spinner").IsVisible.Should().BeFalse("skeleton mode has no ring");
        Part(placeholder, "PART_ContentPresenter").IsVisible.Should().BeFalse("the bars stand in for the list");

        placeholder.IsBusy = false;
        Dispatcher.UIThread.RunJobs();

        Part(placeholder, "PART_Skeleton").IsVisible.Should().BeFalse("the list has arrived");
        Part(placeholder, "PART_ContentPresenter").IsVisible.Should().BeTrue();
        content.IsEffectivelyVisible.Should().BeTrue();
        window.Close();
    }

    [AvaloniaFact]
    public void BusySpinner_ShowsTheRingOverDimmedContent_WithNoBars()
    {
        var (window, placeholder, content) = Open(BusyPlaceholderMode.Spinner, busy: true);

        Part(placeholder, "PART_Spinner").IsVisible.Should().BeTrue();
        Part(placeholder, "PART_Skeleton").IsVisible.Should().BeFalse("spinner mode draws no bars");
        content.IsEffectivelyVisible.Should().BeTrue();
        Part(placeholder, "PART_ContentPresenter").Opacity.Should().BeLessThan(1);
        window.Close();
    }

    /// <summary>The bars are quiet filled shapes, not outlined boxes (RemEx-pp4cm.22).</summary>
    [AvaloniaFact]
    public void SkeletonBars_AreFilledWithNoOutline()
    {
        // Production PostToUiThread, not the inline seam: see ShellRenderFixture.CreateAsync.
        new ThemeService().ApplyCustomization(new CustomizationSettings());
        Dispatcher.UIThread.RunJobs();
        var (window, placeholder, _) = Open(BusyPlaceholderMode.Skeleton, busy: true);

        var bars = placeholder.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Classes.Contains("skeleton-bar")).ToList();
        bars.Should().HaveCount(4);
        foreach (var bar in bars)
        {
            bar.BorderThickness.Should().Be(default(Avalonia.Thickness), "an outline makes each bar read as an empty box");
            bar.Background.Should().BeAssignableTo<ISolidColorBrush>();
            ((ISolidColorBrush)bar.Background!).Color.A.Should().BeGreaterThan(0, "the bar must actually be filled");
        }
        window.Close();
    }
}
