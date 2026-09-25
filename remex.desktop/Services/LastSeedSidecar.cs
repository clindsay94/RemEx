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

    // P1-25: ApplyAndSave calls WriteAsync on every live customization change, including a slider
    // drag's per-tick ticks - most of which touch neither the seed, variant, mode nor contrast this
    // sidecar actually stores (e.g. a background-opacity drag), and a contrast drag changes this
    // exact value on every tick. RG:694 still requires every write that DOES happen to be atomic
    // (WriteAllTextAtomicAsync, unchanged below); what's new is deciding whether a write happens at
    // all. Two layers: skip entirely when the document is unchanged from the last thing actually
    // written, and debounce the ones that DO change (a contrast drag) to at most one write per
    // DebounceDelay instead of one per tick, always landing on the latest value once ticks stop.
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(400);
    private static readonly object Gate = new();
    private static SidecarDocument? _lastWritten;
    private static CustomizationSettings? _pendingSettings;
    private static Task? _pendingWrite;

    /// <summary>
    /// Test-only seam (round 2 review HIGH): lets a test hold a write open — deterministically, with
    /// no timing dependency — to prove a value arriving WHILE the disk write is in flight is not
    /// dropped (the loop in <see cref="DebounceThenWriteAsync"/> below). Defaults to the real atomic
    /// write; only ever reassigned by a test, and only for the duration of that test.
    /// </summary>
    internal static Func<string, string, Task> WriteFileForTests { get; set; } = RemexDataPaths.WriteAllTextAtomicAsync;

    /// <summary>
    /// Writes the sidecar from a live <see cref="CustomizationSettings"/>. Atomic via
    /// <see cref="RemexDataPaths.WriteAllTextAtomicAsync"/> — same staging-file rule as every other
    /// store this repo persists, so a reader never observes a half-written file. Never throws: a
    /// write failure is logged at Warning and swallowed, exactly like a failed sidecar write must
    /// not be able to fail the customization save it rides along with.
    /// </summary>
    public static Task WriteAsync(CustomizationSettings settings, ILogger? logger = null)
    {
        var document = BuildDocument(settings);

        lock (Gate)
        {
            // Review fix (round 1): the fast skip is ONLY safe when nothing is in flight. If a
            // debounce is already pending, this call must still record its own settings as the
            // latest intent even when they happen to equal _lastWritten - otherwise a revert (change
            // away and back to the on-disk value within one debounce window) would leave a stale,
            // already-in-flight write of the intermediate value as the final one, since that write
            // was never told the user reverted.
            if (_pendingWrite is null && document == _lastWritten) return Task.CompletedTask;

            _pendingSettings = settings;
            if (_pendingWrite is { IsCompleted: false }) return _pendingWrite;

            _pendingWrite = DebounceThenWriteAsync(logger);
            return _pendingWrite;
        }
    }

    /// <summary>
    /// Test-only: clears the dedupe/debounce cache so one test's write doesn't get silently skipped
    /// as a "no change" against a value left behind by an EARLIER test — these fields are static
    /// (one process-wide cache, matching the sidecar's one-file-per-machine shape), so they persist
    /// across test methods within the same run unless explicitly reset.
    /// </summary>
    internal static void ResetCacheForTests()
    {
        lock (Gate)
        {
            _lastWritten = null;
            _pendingSettings = null;
            _pendingWrite = null;
            WriteFileForTests = RemexDataPaths.WriteAllTextAtomicAsync;
        }
    }

    private static SidecarDocument BuildDocument(CustomizationSettings settings) => new(
        CurrentSchema,
        settings.AccentColor,
        settings.SchemeVariant,
        settings.ThemeMode ?? ThemeModes.Dark,
        Math.Clamp(settings.ThemeContrast, -1.0, 1.0));

    /// <summary>
    /// Review fix (round 1): loops rather than writing once, because a real disk write is not
    /// instantaneous and a new value can legitimately arrive while it's in flight (the tail end of a
    /// fast contrast drag). Each pass delays, takes the LATEST <see cref="_pendingSettings"/>, writes
    /// it, and then re-checks whether something even newer arrived during that write - if so, it
    /// loops for another debounced write of THAT value instead of declaring done and silently
    /// dropping it. <see cref="_pendingWrite"/> is only cleared once a pass finds nothing changed
    /// since the write it just performed, so a caller awaiting the very first call's task genuinely
    /// waits until the sidecar reflects the last value anyone passed in, however many passes that took.
    /// </summary>
    private static async Task DebounceThenWriteAsync(ILogger? logger)
    {
        while (true)
        {
            await Task.Delay(DebounceDelay);

            SidecarDocument document;
            lock (Gate)
            {
                // Guarded, not asserted: _pendingSettings is set under this same lock in WriteAsync
                // right before this method starts, but ResetCacheForTests can null it out from under
                // an in-flight debounce mid-test - treat that as nothing left to do rather than throw.
                if (_pendingSettings is not { } latest)
                {
                    _pendingWrite = null;
                    return;
                }

                document = BuildDocument(latest);
                if (document == _lastWritten)
                {
                    _pendingWrite = null;
                    return; // superseded by an equal value while we waited - nothing left to write
                }
            }

            try
            {
                var json = JsonSerializer.Serialize(document, JsonOptions);
                await WriteFileForTests(FilePath, json);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "LastSeedSidecar: failed to write '{FilePath}'", FilePath);
                lock (Gate) { _pendingWrite = null; }
                return;
            }

            lock (Gate)
            {
                _lastWritten = document;
                // Did something newer arrive while the write above was in flight? Compare against
                // the CURRENT _pendingSettings, not the `latest` this pass captured before writing.
                if (_pendingSettings is not null && BuildDocument(_pendingSettings) == document)
                {
                    _pendingWrite = null;
                    return; // still current - done
                }
                // else: loop again, under the same _pendingWrite task, for one more debounced write.
            }
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
