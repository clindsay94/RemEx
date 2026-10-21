using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;

namespace Remex.Desktop.Render.Tests;

/// <summary>
/// RemEx-172ba: the drawer has to open with the current page's nav item on screen, and at the very
/// top for Home. The list overflows a short window since Remote desktop and the second divider
/// joined it, and its scroll offset outlives a close: before the fix a list the user had wheeled to
/// the bottom reopened at the bottom with Home selected, because nothing re-scrolls when the
/// selection does not change.
/// </summary>
public sealed class NavDrawerScrollRenderTests
{
    private static void Pump(Window window, int ms = 500)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame();
            Thread.Sleep(16);
        }
    }

    private static async Task<(ShellRenderFixture Fixture, ListBox Nav, ScrollViewer Scroll)> OpenShortWindowAsync()
    {
        var f = await ShellRenderFixture.CreateAsync();
        f.Window.Height = 560;
        f.CaptureFrame();
        Pump(f.Window);
        var nav = f.View.FindControl<ListBox>("NavList")!;
        var scroll = nav.GetVisualDescendants().OfType<ScrollViewer>().First();
        scroll.Extent.Height.Should().BeGreaterThan(scroll.Viewport.Height,
            "the bug only exists when the nav list overflows; a taller list would make this test vacuous");
        return (f, nav, scroll);
    }

    private static void Reopen(ShellRenderFixture f)
    {
        f.ViewModel.IsDrawerOpen = false;
        Pump(f.Window);
        f.ViewModel.IsDrawerOpen = true;
        Pump(f.Window);
    }

    [AvaloniaFact]
    public async Task AListScrolledToTheBottomReopensAtTheTopForHome()
    {
        var (f, _, scroll) = await OpenShortWindowAsync();
        using var _f = f;

        scroll.Offset = new Vector(0, scroll.Extent.Height - scroll.Viewport.Height);
        Pump(f.Window);
        scroll.Offset.Y.Should().BeGreaterThan(0);

        Reopen(f);

        scroll.Offset.Y.Should().Be(0, "Home is the first item, so the drawer opens at the very top");
    }

    [AvaloniaFact]
    public async Task AReopenThatChangesNeitherSelectionNorFocusStillScrollsToHome()
    {
        var (f, nav, scroll) = await OpenShortWindowAsync();
        using var _f = f;
        var home = nav.Items.OfType<ListBoxItem>().First();

        scroll.Offset = new Vector(0, scroll.Extent.Height - scroll.Viewport.Height);
        f.ViewModel.IsDrawerOpen = false;
        Pump(f.Window);
        // Focus left on Home through the close (the restore target was hidden): the reopen's focus
        // call is then a no-op, and so is the selection resync, so neither scrolls anything.
        home.Focus();
        Pump(f.Window, 100);
        f.ViewModel.IsDrawerOpen = true;
        Pump(f.Window);

        scroll.Offset.Y.Should().Be(0);
    }

    [AvaloniaFact]
    public async Task TheDrawerOpensWithTheCurrentPageVisible()
    {
        var (f, nav, scroll) = await OpenShortWindowAsync();
        using var _f = f;

        f.ViewModel.IsDrawerOpen = false;
        Pump(f.Window);
        f.ViewModel.ActiveNavIndex = 6; // About, the last item
        f.ViewModel.IsDrawerOpen = true;
        Pump(f.Window);

        var about = nav.Items.OfType<ListBoxItem>().Single(i => i.Tag as string == "6");
        var top = about.TranslatePoint(new Point(0, 0), scroll)!.Value.Y;
        top.Should().BeGreaterThanOrEqualTo(0);
        (top + about.Bounds.Height).Should().BeLessThanOrEqualTo(scroll.Viewport.Height + 0.5);

        f.ViewModel.IsDrawerOpen = false;
        Pump(f.Window);
        f.ViewModel.ActiveNavIndex = 0;
        f.ViewModel.IsDrawerOpen = true;
        Pump(f.Window);

        scroll.Offset.Y.Should().Be(0);
    }
}
