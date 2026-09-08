using System.Text.Json;
using Microsoft.Extensions.Logging;
using Remex.Core.Models;
using Remex.Core.Services;

namespace Remex.Desktop.Services;

/// <summary>The fields <see cref="LastSeedSidecar"/> persists — the last-applied seed, scheme
/// variant, theme mode and contrast, exactly enough for <see cref="SplashPaletteResolver"/> to
/// rebuild a splash palette before <c>dashboard_layout.json</c> has loaded.</summary>
public sealed record LastSeedSidecarData(string Seed, string Variant, string Mode, double Contrast);

/// <summary>
/// Writes and reads <c>last-seed.json</c>, a tiny sidecar beside <c>dashboard_layout.json</c>
/// (RemEx-alwfa.1, Connor's decision (b) 2026-09-07): the splash reads this file, synchronously and
/// first, before the real dashboard profile has loaded, so it can recolour itself from the last
/// seed the user actually applied instead of always showing the fixed brand palette on every
/// launch. <see cref="WriteAsync"/> is called from <c>CustomizationViewModel.ApplyAndSave</c> (every
/// live change) and once after <c>DashboardLayoutService</c> loads a profile, so an existing install
/// gets a sidecar the first time it starts under this bead without the user touching Personalize.
/// </summary>
/// <remarks>
/// WRITE FAILURES ARE LOGGED, NEVER THROWN — a sidecar is a cache the splash can live without;
/// losing it must not be able to break a save. <see cref="TryRead"/> is equally defensive: missing,
/// unreadable or corrupt JSON all just return <see langword="false"/>, which
/// <see cref="SplashPaletteResolver.ResolveFromSidecar"/> turns into the brand default palette.
/// </remarks>
public static class LastSeedSidecar
{
    private const int CurrentSchema = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>The sidecar's path: beside <c>dashboard_layout.json</c>, same per-user directory
    /// (or the test redirect when one is set — see <see cref="RemexDataPaths.PerUserDirectory"/>).</summary>
    internal static string FilePath =>
        Path.Combine(RemexDataPaths.PerUserDirectory, "last-seed.json");

    private sealed record SidecarDocument(int Schema, string? Seed, string? Variant, string? Mode, double Contrast);

    /// <summary>
    /// Writes the sidecar from a live <see cref="CustomizationSettings"/>. Atomic via
    /// <see cref="RemexDataPaths.WriteAllTextAtomicAsync"/> — same staging-file rule as every other
    /// store this repo persists, so a reader never observes a half-written file. Never throws: a
    /// write failure is logged at Warning and swallowed, exactly like a failed sidecar write must
    /// not be able to fail the customization save it rides along with.
    /// </summary>
    public static async Task WriteAsync(CustomizationSettings settings, ILogger? logger = null)
    {
        try
        {
            var document = new SidecarDocument(
                CurrentSchema,
                settings.AccentColor,
                settings.SchemeVariant,
                settings.ThemeMode ?? ThemeModes.Dark,
                Math.Clamp(settings.ThemeContrast, -1.0, 1.0));
            var json = JsonSerializer.Serialize(document, JsonOptions);
            await RemexDataPaths.WriteAllTextAtomicAsync(FilePath, json);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "LastSeedSidecar: failed to write '{FilePath}'", FilePath);
        }
    }

    /// <summary>
    /// Reads and parses the sidecar. Synchronous and deliberately tiny — the splash calls this on
    /// <c>OnAttachedToVisualTree</c>, before its frame timer starts, and a file this small is cheap
    /// enough on the UI thread that an async round trip would only add latency to the first frame.
    /// Returns <see langword="false"/> for anything short of a valid document with a non-empty seed:
    /// a missing file, unreadable bytes, malformed JSON, or a document some field of which failed to
    /// deserialize into a usable value.
    /// </summary>
    public static bool TryRead(out LastSeedSidecarData seed)
    {
        seed = null!;
        try
        {
            if (!File.Exists(FilePath)) return false;
            var json = File.ReadAllText(FilePath);
            var document = JsonSerializer.Deserialize<SidecarDocument>(json, JsonOptions);
            if (document is null || string.IsNullOrWhiteSpace(document.Seed)) return false;

            seed = new LastSeedSidecarData(
                document.Seed,
                string.IsNullOrWhiteSpace(document.Variant) ? Remex.Desktop.Models.SchemeVariants.TonalSpot : document.Variant,
                string.IsNullOrWhiteSpace(document.Mode) ? ThemeModes.Dark : document.Mode,
                document.Contrast);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
