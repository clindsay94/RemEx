using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using Material.Icons;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Guards the icon system after the hand-drawn glyphs were retired for
/// <c>Material.Icons.Avalonia</c> (RemEx-wyx2c).
/// </summary>
/// <remarks>
/// <para>
/// EVERY FAILURE THIS GUARDS AGAINST IS SILENT. The old system was 28 <c>StreamGeometry</c>
/// resources in <c>App.axaml</c> drawn through <c>Path</c> elements, and the reason it is worth a
/// test rather than a code review is what the inventory turned up: <c>IconSearch</c> and
/// <c>IconClose</c> were referenced five times between <c>ShellView</c> and <c>HomeView</c> and
/// defined NOWHERE. Those five icons had never drawn anything, the build was green, and nobody
/// noticed. A missing resource key costs a glyph, not an exception.
/// </para>
/// <para>
/// The second silent failure is the styling one. <c>Path</c> paints with <c>Fill</c> and
/// <c>MaterialIcon</c> paints with <c>Foreground</c>, so a style selector left pointing at
/// <c>Path</c> — or a <c>Fill</c> setter left on a <c>MaterialIcon</c> — compiles perfectly and
/// simply never applies. The nav rail's whole pointerover and active-state colour language runs
/// through exactly those selectors.
/// </para>
/// <para>
/// A SOURCE-TEXT TEST, for the reason the other view tests in this folder give: there is no
/// headless render in this suite, and an icon that fails to draw throws nothing.
/// </para>
/// </remarks>
public class MaterialIconAdoptionTests
{
    private const string Avalonia = "https://github.com/avaloniaui";
    private const string MaterialIconsNs = "using:Material.Icons.Avalonia";

    /// <summary>
    /// The <c>Path</c> elements that are genuinely drawings rather than icons, and the reason each
    /// one stays. Anything not on this list must be a <c>MaterialIcon</c>.
    /// </summary>
    private static readonly Dictionary<string, string[]> AllowedPaths = new()
    {
        ["CanvasView.axaml"] =
        [
            "CoachArrow",     // geometry computed in code-behind to point at a moving target
            "CoachArrowHead",
        ],
        ["RemoteDesktopView.axaml"] =
        [
            "cursor-arrow",   // the remote pointer itself, drawn inline
            "cursor-crosshair",
        ],
        // NOT AN APP VIEW — a verbatim copy of Material.Avalonia 3.19.0's window-decorations
        // template, taken to clear the opaque underlay that was hiding the OS backdrop
        // (RemEx-c437b; see WindowChromeBackdropTests). These are upstream's caption-button
        // glyphs. Redrawing them as MaterialIcon would add drift to the one file whose entire
        // value is staying re-diffable against the vendor original on the next upgrade.
        ["WindowChrome.axaml"] =
        [
            "FullScreenButtonPath",
            "RestoreButtonPath",
            "(unnamed)",      // minimise, maximise, close, and the two popover glyphs
        ],
    };

    [Fact]
    public void NoView_StillDrawsAnIconThroughAStaticResourceGeometry()
    {
        var offenders =
            from view in ViewFiles()
            from path in XDocument.Load(view).Descendants(XName.Get("Path", Avalonia))
            where path.Attribute("Data")?.Value.StartsWith("{StaticResource Icon", StringComparison.Ordinal) == true
            select Path.GetFileName(view);

        offenders.Should().BeEmpty(
            "icons are MaterialIcon now; a StaticResource geometry reference resolves to nothing "
            + "and draws nothing, exactly as IconSearch and IconClose did for months");
    }

    [Fact]
    public void AppResources_NoLongerCarryIconGeometries()
    {
        var app = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "App.axaml"));

        app.Should().NotContain(
            "StreamGeometry",
            "the 28 hand-drawn icon geometries were replaced wholesale; a survivor means a view "
            + "was missed and is still drawing its own glyph");
    }

    /// <summary>
    /// Avalonia resolves <c>Kind</c> at XAML compile time, so a misspelling is a build error — but
    /// only for a literal. This catches the case the compiler cannot: a name that parses today and
    /// is dropped by a future Material.Icons bump.
    /// </summary>
    [Fact]
    public void EveryIconKind_ExistsInTheShippedIconSet()
    {
        var icons = MaterialIcons().ToList();

        icons.Should().NotBeEmpty("a query that matches nothing asserts nothing");

        foreach (var (view, icon) in icons)
        {
            var kind = icon.Attribute("Kind")?.Value;
            if (kind is null || kind.StartsWith('{'))
                continue;   // a binding, not a literal; TrayTile.Icon is typed and checked by the compiler

            Enum.TryParse<MaterialIconKind>(kind, ignoreCase: false, out _)
                .Should().BeTrue($"{view} names MaterialIconKind.{kind}");
        }
    }

    /// <summary>
    /// Everything else here that reads the XML finds icons by their namespace URI, and
    /// <c>using:Material.Icons.Avalonia</c> is only one of the legal spellings — the
    /// assembly-qualified form is another. A view written with the other spelling would drop out of
    /// every one of those tests silently, so cross-check the count against the raw text.
    /// </summary>
    [Fact]
    public void TheXmlQueries_SeeEveryMaterialIconInTheSource()
    {
        var found = MaterialIcons().Count();

        var written = ViewFiles()
            .Sum(view => Regex.Matches(File.ReadAllText(view), @"<\w+:MaterialIcon\b").Count);

        found.Should().Be(written,
            "an icon the namespace lookup misses is an icon no assertion in this file covers");
    }

    /// <summary>
    /// <c>MaterialIcon</c> has no <c>Fill</c>. A setter or attribute left over from the Path era is
    /// accepted by the XAML compiler as an attached-property-shaped no-op and the icon quietly keeps
    /// whatever colour it inherited.
    /// </summary>
    [Fact]
    public void NoMaterialIcon_IsColouredThroughFill()
    {
        var offenders =
            from icon in MaterialIcons()
            where icon.Element.Attribute("Fill") is not null
            select $"{icon.View}: {icon.Element.Attribute("Kind")?.Value ?? "(bound)"}";

        offenders.Should().BeEmpty("MaterialIcon paints with Foreground; Fill silently does nothing");
    }

    /// <summary>
    /// The nav rail's colour language — muted by default, primary on hover, accent when active —
    /// lives entirely in three descendant selectors. Retargeting them was the easiest half of this
    /// migration to forget, and forgetting it costs the rail its entire state feedback.
    /// </summary>
    [Fact]
    public void TheNavRail_StillColoursItsIconsPerButtonState()
    {
        var shell = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "ShellView.axaml"));

        foreach (var selector in new[]
                 {
                     "Button.nav-item mi|MaterialIcon",
                     "Button.nav-item:pointerover mi|MaterialIcon",
                     "Button.nav-item-active mi|MaterialIcon",
                 })
        {
            shell.Should().Contain($"Selector=\"{selector}\"",
                "a selector still naming Path matches nothing and the rail stops responding to state");
        }
    }

    [Fact]
    public void TheOnlyRemainingPaths_AreTheDocumentedDrawings()
    {
        foreach (var view in ViewFiles())
        {
            var file = Path.GetFileName(view);
            var names = XDocument.Load(view)
                .Descendants(XName.Get("Path", Avalonia))
                .Select(path => path.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value
                                ?? path.Attribute("Classes")?.Value
                                ?? "(unnamed)")
                .ToList();

            var allowed = AllowedPaths.TryGetValue(file, out var value) ? value : [];
            names.Should().BeSubsetOf(allowed,
                $"{file} may only keep a Path for a drawing that no icon font can express");
        }
    }

    /// <summary>Every MaterialIcon element in the app, with the file it came from.</summary>
    private static IEnumerable<(string View, XElement Element)> MaterialIcons()
        => from view in ViewFiles()
           from icon in XDocument.Load(view).Descendants(XName.Get("MaterialIcon", MaterialIconsNs))
           select (Path.GetFileName(view), icon);

    /// <summary>
    /// Every markup file in the app, not just <c>Views/</c>. MainWindow and <c>Controls/</c> carry no
    /// icons today, and that is the sort of thing that stays true only while something checks.
    /// </summary>
    private static IEnumerable<string> ViewFiles()
        => Directory
            .EnumerateFiles(Path.Combine(RepoRoot(), "remex.desktop"), "*.axaml", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                           && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
