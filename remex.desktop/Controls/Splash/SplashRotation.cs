using Microsoft.Extensions.Logging;
using Remex.Core.Logging;
using Remex.Core.Services;

namespace Remex.Desktop.Controls.Splash;

/// <summary>
/// The "Random" splash style: picks one of the four real splashes each launch, never the same one
/// twice in a row. The last pick lives in <c>last-splash.txt</c> beside <c>last-seed.json</c>.
/// </summary>
public static class SplashRotation
{
    public const string Random = "Random";

    public static readonly IReadOnlyList<string> Playable = ["LiveHandshake", "RemexCommand", "CosmicZoom", "Pong"];

    internal static string FilePath => Path.Combine(RemexDataPaths.PerUserDirectory, "last-splash.txt");

    /// <summary>Uniform over <see cref="Playable"/> minus <paramref name="previous"/>; all four when it is null or unknown.</summary>
    public static string Next(string? previous, System.Random rng)
    {
        var pool = Playable.Where(id => !string.Equals(id, previous, StringComparison.Ordinal)).ToArray();
        return pool[rng.Next(pool.Length)];
    }

    /// <summary>The splash played last time, or null when there is no usable record. Never throws.</summary>
    public static string? ReadLast()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var id = File.ReadAllText(FilePath).Trim();
            return Playable.Contains(id) ? id : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Records <paramref name="id"/> as the splash just played. Fire-and-forget; failures are logged, never thrown.</summary>
    public static void Remember(string id)
    {
        if (!Playable.Contains(id)) return;
        _ = RememberAsync(id);
    }

    internal static async Task RememberAsync(string id)
    {
        try
        {
            Directory.CreateDirectory(RemexDataPaths.PerUserDirectory);
            await RemexDataPaths.WriteAllTextAtomicAsync(FilePath, id);
        }
        catch (Exception ex)
        {
            // A lost record only means the next launch may repeat a splash.
            InMemoryLogSink.Append(LogLevel.Warning, "Splash", "Could not record the last random splash", ex);
        }
    }
}
