using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Controls;

/// <summary>
/// Guards the window-decorations underlay that hid the Mica / Acrylic backdrop (RemEx-c437b).
/// </summary>
/// <remarks>
/// <para>
/// Avalonia 12 draws the main window's frame itself — ExtendClientAreaToDecorationsHint makes the
/// platform ask for managed decorations — through a WindowDrawnDecorations element.
/// Material.Avalonia 3.19's theme for it fills the UNDERLAY slot with MaterialPaperBrush at full
/// alpha, on PART_WindowBorder (the whole window) and PART_TitleBar. That underlay composites
/// BETWEEN the OS backdrop and everything the app draws, so Mica and Acrylic were applied by the
/// platform and then covered. Measured 2026-08-26: a dead-constant #303030 — MaterialPaperBrush
/// exactly — at two window positions over a varied wallpaper, 0.02% of sampled pixels differing,
/// while Window.ActualTransparencyLevel reported Mica the whole time.
/// </para>
/// <para>
/// THE FAILURE IS SILENT IN EVERY DIRECTION, which is why it is worth a source-level guard.
/// There is no exception and no log line; the transparency API reports success; the visual tree
/// reports Transparent everywhere, because WindowDrawnDecorations is a StyledElement that is not a
/// descendant of the Window. Two earlier attempts at this bug both went after the app's own tint
/// rectangles in DashboardBackgroundControl and found that dropping them to 1% moved the pixels by
/// ~3/255 and nothing else. This suite has no headless render, so only the source can be pinned.
/// </para>
/// <para>
/// Themes/Chrome/WindowChrome.axaml is a deliberate copy of Material.Avalonia 3.19.0's
/// Material.Styles/Resources/Themes/WindowDrawnDecorations.axaml — the one thing this repo
/// otherwise refuses to do. It is a copy because everything lighter was tried and MEASURED inert:
/// Window.Background (template-binds one layer up), an app-level Style on the parts (the element
/// has no logical parent in the app tree, so Application.Styles never match), a derived ControlTheme
/// with <c>^ /template/</c> setters (the parts are built by WindowDrawnDecorationsTemplate and are
/// not /template/ children), and ControlTheme.Resources overriding MaterialPaperBrush (the parts
/// resolve DynamicResource against Application scope). Only an Application-level MaterialPaperBrush
/// reached them, and that same brush is a FOREGROUND on Badge, ColorZone, CalendarDayButton,
/// FlyoutPresenter, ToolTip and PipsPager, so clearing it app-wide turns that text invisible.
/// </para>
/// </remarks>
public class WindowChromeBackdropTests
{
    private const string Avalonia = "https://github.com/avaloniaui";

    [Theory]
    [InlineData("Border", "PART_WindowBorder")]
    [InlineData("Panel", "PART_TitleBar")]
    public void TheDecorationsUnderlay_DoesNotPaintOverTheOsBackdrop(string element, string partName)
    {
        var part = Chrome()
            .Descendants(XName.Get(element, Avalonia))
            .Single(e => e.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value == partName);

        part.Attribute("Background")?.Value.Should().Be("Transparent",
            $"{partName} is the decorations underlay — it sits between the OS backdrop and " +
            "everything this app draws, so any opaque fill here silently deletes Mica and Acrylic " +
            "while the transparency API still reports success");
    }

    [Fact]
    public void TheTitleBarUnderlay_IsTransparentRatherThanUnset()
    {
        // Transparent, not absent. A null Background does not hit-test, and PART_TitleBar carries
        // WindowDecorationProperties.ElementRole="TitleBar" — it IS the drag region. Clearing the
        // fill by deleting the attribute would trade a dead backdrop for an undraggable window.
        var titleBar = Chrome()
            .Descendants(XName.Get("Panel", Avalonia))
            .Single(e => e.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value == "PART_TitleBar");

        titleBar.Attribute("Background").Should().NotBeNull(
            "an unset Background does not hit-test, and this part is the title-bar drag region");
    }

    [Fact]
    public void TheChromeTheme_IsKeyedByNameSoItNeverBecomesTheAppWideDefault()
    {
        var themes = Chrome()
            .Descendants(XName.Get("ControlTheme", Avalonia))
            .Select(t => t.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value)
            .ToList();

        themes.Should().Contain("BackdropSafeWindowDecorations",
            "MainWindow looks this key up by name and throws when it is missing");
        themes.Should().NotContain(k => k != null && k.Contains("x:Type", StringComparison.Ordinal),
            "keying by type would apply this to EVERY window, including the dialogs and tray " +
            "windows that legitimately want Material's opaque underlay");
    }

    [Fact]
    public void MainWindow_ActuallyAppliesTheChromeTheme()
    {
        // The wiring is the load-bearing half: the theme file can be perfect and the backdrop still
        // dead if the window never assigns it. It is assigned via TryGetResource rather than the
        // Resources indexer, because the indexer does not search MergedDictionaries and returns
        // null for a key that is plainly there — and assigning that null to a nullable
        // ControlTheme is not an error, so the window would quietly keep Material's decorations.
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "MainWindow.axaml.cs"));

        source.Should().Contain("WindowDecorationsTheme = decorationsTheme",
            "the chrome theme has to actually be applied to the window");
        source.Should().Contain("Resources.TryGetResource(\"BackdropSafeWindowDecorations\"",
            "the Resources indexer does not search MergedDictionaries and silently yields null");
        source.Should().Contain("throw new InvalidOperationException",
            "a missing chrome theme means a dead backdrop, so it must fail loudly rather than " +
            "degrade into the exact silent bug this guards");

        var window = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "MainWindow.axaml"));
        window.Should().Contain("Themes/Chrome/WindowChrome.axaml",
            "MainWindow merges the chrome dictionary that defines the theme");
    }

    private static XDocument Chrome()
        => XDocument.Parse(File.ReadAllText(Path.Combine(
            RepoRoot(), "remex.desktop", "Themes", "Chrome", "WindowChrome.axaml")));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
