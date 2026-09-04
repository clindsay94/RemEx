using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Guards RemEx-1ufoa.1: the six <c>Canvas_*</c> strings that used to carry an emoji prefix
/// ("📡 Ping", "✕ Cancel", "✕ Disconnect", "📊 Graph Type", "🔒 Lock PC", "🔧 Reboot to UEFI") were
/// doubling the Material icon their control already drew, and every CanvasView context-menu item
/// that lacked an icon of its own now has one.
/// </summary>
/// <remarks>
/// Both halves are source-text guards for the reason every other file in this folder gives: there
/// is no headless render in this suite, so a reintroduced emoji prefix or a MenuItem missing its
/// icon compiles, renders something, and no assertion anywhere else notices.
/// </remarks>
public class CanvasMenuGlyphTests
{
    private const string Avalonia = "https://github.com/avaloniaui";
    private const string MaterialIconsNs = "using:Material.Icons.Avalonia";

    [Fact]
    public void NoCanvasStringInAnyLocale_StartsWithASymbolOrEmojiPrefix()
    {
        var files = ResxFiles().ToList();
        files.Should().HaveCount(9, "RemEx ships Strings.resx plus 8 translated locales");

        var offenders = new List<string>();

        foreach (var file in files)
        {
            var doc = XDocument.Load(file);
            var canvasEntries = doc.Root!.Elements("data")
                .Where(d => (string?)d.Attribute("name") is { } name && name.StartsWith("Canvas_", StringComparison.Ordinal))
                .Select(d => (Key: (string)d.Attribute("name")!, Value: (string?)d.Element("value") ?? string.Empty))
                .ToList();

            canvasEntries.Count.Should().BeGreaterOrEqualTo(20,
                $"{Path.GetFileName(file)} should carry the full Canvas_* key set; a low count means the parse missed entries");

            foreach (var (key, rawValue) in canvasEntries)
            {
                // Leading whitespace would otherwise hide a prefix at index 1 (" 📡 Ping").
                var value = rawValue.TrimStart();
                if (value.Length == 0)
                    continue;

                // Category "OtherSymbol" (Unicode "So") is where every emoji/dingbat prefix this
                // bead cares about lives (U+1F4E1, U+2715, U+1F4CA, U+1F512, U+1F527, ...). Plain
                // ASCII punctuation such as Canvas_AddCard's leading "+" (MathSymbol) is a
                // deliberate affordance, present in English too, and out of scope here.
                var category = CharUnicodeInfo.GetUnicodeCategory(value, 0);
                if (category == UnicodeCategory.OtherSymbol)
                {
                    var codepoint = char.ConvertToUtf32(value, 0);
                    offenders.Add($"{Path.GetFileName(file)}:{key} starts with U+{codepoint:X4}");
                }
            }
        }

        offenders.Should().BeEmpty(
            "a Canvas_* value should read as text, not as an emoji doubling the control's own MaterialIcon");
    }

    [Fact]
    public void EveryNonPresetCanvasContextMenuItem_CarriesA16pxMaterialIcon()
    {
        var path = Path.Combine(RepoRoot(), "remex.desktop", "Views", "CanvasView.axaml");
        var doc = XDocument.Load(path);

        var allMenuItems = doc.Descendants(XName.Get("MenuItem", Avalonia)).ToList();

        // A literal Header would slip past the key-based filter below and escape the icon check
        // (the localization gate catches the literal, but not the missing icon).
        allMenuItems.Where(mi => HeaderKey(mi) is null)
            .Select(mi => mi.Attribute("Header")?.Value ?? "(no Header)")
            .Should().BeEmpty("every CanvasView MenuItem localizes its Header through {local:Localize}");

        var menuItems = allMenuItems
            .Where(mi => HeaderKey(mi) is { } key && !key.StartsWith("Canvas_Preset_", StringComparison.Ordinal))
            .ToList();

        menuItems.Count.Should().BeGreaterOrEqualTo(12,
            "a query that matches almost nothing asserts almost nothing");

        var offenders = new List<string>();

        foreach (var menuItem in menuItems)
        {
            var key = HeaderKey(menuItem);
            var icon = menuItem.Element(XName.Get("MenuItem.Icon", Avalonia))
                ?.Elements(XName.Get("MaterialIcon", MaterialIconsNs))
                .FirstOrDefault();

            if (icon is null)
            {
                offenders.Add($"{key}: no MenuItem.Icon/MaterialIcon");
                continue;
            }

            var width = icon.Attribute("Width")?.Value;
            var height = icon.Attribute("Height")?.Value;
            if (width != "16" || height != "16")
                offenders.Add($"{key}: icon is {width ?? "(none)"}x{height ?? "(none)"}, expected 16x16");
        }

        offenders.Should().BeEmpty(
            "every CanvasView context-menu item other than a colour preset should carry its own 16px MaterialIcon");
    }

    /// <summary>Pulls the localization key out of a MenuItem's <c>Header="{local:Localize Key}"</c>.</summary>
    private static string? HeaderKey(XElement menuItem)
    {
        var header = menuItem.Attribute("Header")?.Value;
        if (header is null || !header.StartsWith("{local:Localize ", StringComparison.Ordinal))
            return null;

        return header["{local:Localize ".Length..].TrimEnd('}').Trim();
    }

    private static IEnumerable<string> ResxFiles()
        => Directory.EnumerateFiles(
            Path.Combine(RepoRoot(), "remex.desktop", "Localization"), "Strings*.resx");

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
