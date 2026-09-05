using System;
using System.Text;
using System.Text.Json;
using Avalonia.Media;
using Remex.Core.Models;
using Remex.Desktop.Models;

namespace Remex.Desktop.Services;

/// <summary>
/// A palette's recipe — everything needed to reproduce it, not the palette itself. Round-trips
/// through <see cref="PaletteExchange.ToJson"/>/<see cref="PaletteExchange.TryParseJson"/> as the
/// transport for "share this palette", and feeds <see cref="PaletteExchange.ToAxaml"/> alongside a generated
/// <see cref="DynamicColorGenerator.M3Palette"/>.
/// </summary>
public sealed record PaletteRecipe(string Seed, string Variant, string Mode, double Contrast, double SeedChroma);

/// <summary>
/// Serializes a palette recipe to/from JSON, and renders a generated palette as a compilable
/// Avalonia <c>ResourceDictionary</c>.
/// </summary>
/// <remarks>
/// PURE AND STATIC ON PURPOSE (RemEx-a7uzb handoff). No <c>Application.Current</c>, no view model,
/// no service dependency — so the round-trip and the AXAML shape can be pinned by xunit facts that
/// run without an Avalonia runtime, the same reason <see cref="SeedHct"/> is a bare static class.
/// </remarks>
public static class PaletteExchange
{
    /// <summary>Bumped only if the recipe shape changes in a way an older reader could not parse.</summary>
    private const int FormatVersion = 1;

    /// <summary>The three modes <c>ThemeModes</c> defines.</summary>
    private static readonly string[] ValidModes = { ThemeModes.Light, ThemeModes.Dark, ThemeModes.System };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed record PaletteRecipeDto(
        int FormatVersion, string Seed, string Variant, string Mode, double Contrast, double SeedChroma);

    /// <summary>Serializes a recipe to the <c>.remexpalette</c> JSON shape (camelCase, indented, versioned).</summary>
    public static string ToJson(PaletteRecipe recipe)
    {
        var dto = new PaletteRecipeDto(
            FormatVersion, recipe.Seed, recipe.Variant, recipe.Mode, recipe.Contrast, recipe.SeedChroma);
        return JsonSerializer.Serialize(dto, JsonOptions);
    }

    /// <summary>
    /// Parses a <c>.remexpalette</c> JSON payload. Returns false — never throws — for malformed
    /// JSON, a missing/unparseable seed, or an unknown mode, so callers can show one "not a RemEx
    /// palette" toast instead of catching several exception types. A variant outside
    /// <see cref="SchemeVariants.All"/> is not a reason to refuse the file: it normalises to Tonal
    /// Spot exactly as an old profile's <c>SchemeVariant</c> does.
    /// </summary>
    public static bool TryParseJson(string json, out PaletteRecipe? recipe)
    {
        recipe = null;

        PaletteRecipeDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<PaletteRecipeDto>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return false;
        }

        if (dto is null) return false;
        if (string.IsNullOrWhiteSpace(dto.Seed)) return false;
        if (dto.Seed.Length is not (7 or 9) || !Color.TryParse(dto.Seed, out _)) return false;
        if (Array.IndexOf(ValidModes, dto.Mode) < 0) return false;

        var contrast = Math.Clamp(dto.Contrast, -1.0, 1.0);
        recipe = new PaletteRecipe(dto.Seed, SchemeVariants.Normalize(dto.Variant), dto.Mode, contrast, dto.SeedChroma);
        return true;
    }

    /// <summary>
    /// Renders a generated palette as a self-contained Avalonia <c>ResourceDictionary</c>: one
    /// <c>Color</c> and one <c>SolidColorBrush</c> per <see cref="DynamicColorGenerator.M3Palette"/>
    /// role (28 of each), so pasting the output into any dictionary compiles.
    /// </summary>
    public static string ToAxaml(DynamicColorGenerator.M3Palette palette, PaletteRecipe recipe)
    {
        var roles = new (string Name, Color Value)[]
        {
            ("Primary", palette.Primary),
            ("OnPrimary", palette.OnPrimary),
            ("PrimaryContainer", palette.PrimaryContainer),
            ("OnPrimaryContainer", palette.OnPrimaryContainer),
            ("Secondary", palette.Secondary),
            ("OnSecondary", palette.OnSecondary),
            ("SecondaryContainer", palette.SecondaryContainer),
            ("OnSecondaryContainer", palette.OnSecondaryContainer),
            ("Tertiary", palette.Tertiary),
            ("OnTertiary", palette.OnTertiary),
            ("Surface", palette.Surface),
            ("SurfaceVariant", palette.SurfaceVariant),
            ("SurfaceContainerLow", palette.SurfaceContainerLow),
            ("SurfaceContainer", palette.SurfaceContainer),
            ("SurfaceContainerHigh", palette.SurfaceContainerHigh),
            ("OnSurface", palette.OnSurface),
            ("OnSurfaceVariant", palette.OnSurfaceVariant),
            ("Outline", palette.Outline),
            ("OutlineVariant", palette.OutlineVariant),
            ("Error", palette.Error),
            ("OnError", palette.OnError),
            ("Success", palette.Success),
            ("OnSuccess", palette.OnSuccess),
            ("Warning", palette.Warning),
            ("OnWarning", palette.OnWarning),
            ("BackgroundStart", palette.BackgroundStart),
            ("BackgroundMid", palette.BackgroundMid),
            ("BackgroundEnd", palette.BackgroundEnd),
        };

        var sb = new StringBuilder();
        sb.Append("<ResourceDictionary xmlns=\"https://github.com/avaloniaui\"\n")
          .Append("                    xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">\n")
          .Append("  <!-- RemEx palette export: seed=").Append(recipe.Seed)
          .Append(" variant=").Append(recipe.Variant)
          .Append(" mode=").Append(recipe.Mode)
          .Append(" contrast=").Append(recipe.Contrast.ToString("F2", System.Globalization.CultureInfo.InvariantCulture))
          .Append(" -->\n");

        foreach (var (name, value) in roles)
            sb.Append("  <Color x:Key=\"").Append(name).Append("Color\">")
              .Append(ToHexArgb(value)).Append("</Color>\n");

        foreach (var (name, _) in roles)
            sb.Append("  <SolidColorBrush x:Key=\"").Append(name).Append("Brush\" Color=\"{StaticResource ")
              .Append(name).Append("Color}\"/>\n");

        sb.Append("</ResourceDictionary>\n");
        return sb.ToString();
    }

    private static string ToHexArgb(Color c) =>
        $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
}
