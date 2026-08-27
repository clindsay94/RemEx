using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Guards both halves of the fix for the Task Manager list card scaling over the search field
/// (RemEx-zm6gp).
/// </summary>
/// <remarks>
/// <para>
/// <c>Border.glass-card:pointerover</c> applies <c>translateY(-4px) scale(1.01)</c> — a hover lift
/// designed for finite cards. The Task Manager wraps its ENTIRE process list in one glass-card
/// Border, and because the ListBox sits in an unbounded StackPanel, that Border is as tall as the
/// full unvirtualized list — thousands of pixels. A centre-origin scale of 1.01 displaces the top
/// edge by 1% of half the height: at ~200 processes, hovering any row slid the card ~80px up over
/// the search field. Connor hit it live on 2026-08-26.
/// </para>
/// <para>
/// The fix is the <c>surface</c> class: glass cards that HOST a list keep the hover background and
/// shadow but give up the transform. Both halves are load-bearing — dropping the class from the
/// view restores the slide, and deleting the App.axaml style turns the class into an inert word.
/// </para>
/// <para>
/// A SOURCE-TEXT TEST for the usual reason: no headless render in this suite, and the failure is a
/// visual slide rather than an exception, so nothing else notices before a user does.
/// </para>
/// </remarks>
public class TaskManagerListSurfaceTests
{
    private const string Avalonia = "https://github.com/avaloniaui";

    [Fact]
    public void TheProcessListContainer_IsASurfaceCard()
    {
        var listBoxes = XDocument.Parse(ViewSource("TaskManagerView"))
            .Descendants(XName.Get("ListBox", Avalonia))
            .Where(element => element.Attribute("ItemsSource")?.Value == "{Binding Processes}")
            .ToList();

        listBoxes.Should().ContainSingle("the page binds exactly one ListBox to Processes");

        var container = listBoxes[0].Parent;
        container.Should().NotBeNull();
        container!.Name.LocalName.Should().Be("Border");

        var classes = (container.Attribute("Classes")?.Value ?? string.Empty).Split(' ');
        classes.Should().Contain("glass-card");
        classes.Should().Contain("surface",
            "without the surface class, the glass-card hover transform scales a list-height Border " +
            "and slides the top of the list over the search field");
    }

    [Fact]
    public void TheSurfaceVariant_SuppressesTheHoverTransform()
    {
        var appXaml = XDocument.Parse(File.ReadAllText(
            Path.Combine(RepoRoot(), "remex.desktop", "App.axaml")));

        var surfaceHoverStyles = appXaml
            .Descendants(XName.Get("Style", Avalonia))
            .Where(style => style.Attribute("Selector")?.Value == "Border.glass-card.surface:pointerover")
            .ToList();

        surfaceHoverStyles.Should().ContainSingle(
            "the surface class is only meaningful while App.axaml carries its hover override");

        var transformSetter = surfaceHoverStyles[0]
            .Elements(XName.Get("Setter", Avalonia))
            .SingleOrDefault(setter => setter.Attribute("Property")?.Value == "RenderTransform");

        transformSetter.Should().NotBeNull(
            "the override exists to neutralise the base style's RenderTransform; without this " +
            "setter the base translateY/scale still applies");
        transformSetter!.Attribute("Value")?.Value.Should().Be("none");
    }

    [Fact]
    public void TheSurfaceOverride_SitsBelowEveryTransformSettingHoverStyle()
    {
        // Avalonia has no selector specificity — among equal-priority styles the LAST matching one
        // wins. The surface override only neutralises the lift because it sits below BOTH
        // transform-setting hover styles; a tidy that reorders App.axaml would re-enable the slide
        // with every other test still green. Position is the invariant, so position is asserted.
        var appXaml = XDocument.Parse(File.ReadAllText(
            Path.Combine(RepoRoot(), "remex.desktop", "App.axaml")));

        var selectorsInDocumentOrder = appXaml
            .Descendants(XName.Get("Style", Avalonia))
            .Select(style => style.Attribute("Selector")?.Value)
            .Where(selector => selector is not null)
            .ToList();

        var surfaceIndex = selectorsInDocumentOrder.IndexOf("Border.glass-card.surface:pointerover");
        var baseHoverIndex = selectorsInDocumentOrder.IndexOf("Border.glass-card:pointerover");
        var interactiveHoverIndex = selectorsInDocumentOrder.IndexOf("Border.glass-card.interactive:pointerover");

        surfaceIndex.Should().BeGreaterThan(baseHoverIndex,
            "last-wins ordering is what lets the surface override beat the base hover lift");
        surfaceIndex.Should().BeGreaterThan(interactiveHoverIndex,
            "the interactive hover style sets its own RenderTransform, so a surface card that is " +
            "also interactive would re-acquire the lift if the surface override sat above it");
    }

    private static string ViewSource(string viewName)
        => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", viewName + ".axaml"));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
