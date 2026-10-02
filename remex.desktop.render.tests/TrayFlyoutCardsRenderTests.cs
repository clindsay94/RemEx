using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Remex.Desktop.Views;

namespace Remex.Desktop.Render.Tests;

/// <summary>
/// RemEx-8tm8l. The tray flyout's sensor cards scale with the flyout: at every width they share the
/// cards row out evenly instead of sitting at a fixed 200px with a ragged gap on the right.
/// </summary>
/// <remarks>
/// Laid out through the real <see cref="TrayFlyoutWindow"/> XAML rather than checked as text,
/// because the thing that can break is the binding itself: a WrapPanel <c>ItemWidth</c> bound to a
/// <c>$parent</c> that no longer resolves fails silently and the cards fall back to 200px. The
/// window has no view model here, so the cards row's ItemsSource and visibility are set directly -
/// the layout under test does not read anything else from it.
/// </remarks>
public sealed class TrayFlyoutCardsRenderTests
{
    private static (TrayFlyoutWindow Window, ItemsControl Cards) Open(int sensorCount)
    {
        var window = new TrayFlyoutWindow();
        var scroller = window.FindControl<ScrollViewer>("CardsScrollViewer");
        scroller.Should().NotBeNull("the cards row is the ScrollViewer named CardsScrollViewer");
        var cards = scroller!.Content.Should().BeOfType<ItemsControl>().Subject;

        scroller.IsVisible = true;
        cards.ItemsSource = Enumerable.Range(1, sensorCount)
            .Select(i => new SensorViewModel { Name = $"Sensor {i}" })
            .ToList();

        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, cards);
    }

    private static List<Border> CardHosts(ItemsControl cards) =>
        cards.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("flyout-card")).ToList();

    [AvaloniaTheory]
    [InlineData(528)]  // TrayFlyoutGeometry.DefaultWidth, the transient popup
    [InlineData(640)]  // pinned, two wide columns
    [InlineData(760)]  // pinned, three columns
    [InlineData(944)]  // TrayFlyoutGeometryValidator.MaxWidth, four columns
    public void CardsFillTheRowAtEveryFlyoutWidth(int width)
    {
        var (window, cards) = Open(sensorCount: 6);
        try
        {
            window.Width = width;
            Dispatcher.UIThread.RunJobs();

            var panel = cards.Bounds.Width;
            var columns = TrayFlyoutGeometry.CardColumns(panel);
            var hosts = CardHosts(cards);
            hosts.Should().HaveCount(6, "every sensor should get a card");

            // Bounds are relative to each card's own item container, so rows are read in the panel's space.
            double Top(Border h) => h.TranslatePoint(default, cards)!.Value.Y;
            var firstRowTop = hosts.Min(Top);
            var firstRow = hosts.Where(h => Math.Abs(Top(h) - firstRowTop) < 1).ToList();
            firstRow.Should().HaveCount(columns,
                $"at {width}px the {panel}px cards panel holds {columns} columns");

            foreach (var host in hosts)
            {
                host.Bounds.Width.Should().BeGreaterThanOrEqualTo(TrayFlyoutGeometry.CardMinWidth);
                host.Bounds.Height.Should().Be(150, "only the width scales; rows stay whole for the height caps");
            }

            var used = firstRow.Sum(h => h.Bounds.Width + 2 * TrayFlyoutGeometry.CardMargin);
            (panel - used).Should().BeLessThan(columns,
                $"at {width}px the first row should fill the {panel}px panel, not leave {panel - used}px empty");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ResizingWiderGrowsTheCards()
    {
        var (window, cards) = Open(sensorCount: 2);
        try
        {
            window.Width = 528;
            Dispatcher.UIThread.RunJobs();
            var narrow = CardHosts(cards).First().Bounds.Width;

            window.Width = 700;
            Dispatcher.UIThread.RunJobs();
            var wide = CardHosts(cards).First().Bounds.Width;

            wide.Should().BeGreaterThan(narrow + 50,
                "at 700px two columns share ~170px more than at 528px, so each card should be visibly wider");
        }
        finally
        {
            window.Close();
        }
    }
}
