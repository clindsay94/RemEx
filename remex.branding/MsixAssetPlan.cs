namespace Remex.Branding;

/// <summary>Maps an MSIX asset filename (e.g. "Wide310x150Logo.scale-200.png") to its pixel size.</summary>
public static class MsixAssetPlan
{
    // Base (scale-100) dimensions per logo family.
    private static readonly Dictionary<string, (int W, int H)> BaseSizes = new(StringComparer.Ordinal)
    {
        ["Square44x44Logo"]   = (44, 44),
        ["Square150x150Logo"] = (150, 150),
        ["Wide310x150Logo"]   = (310, 150),
        ["LargeTile"]         = (310, 310),
        ["SmallTile"]         = (71, 71),
        ["StoreLogo"]         = (50, 50),
        ["SplashScreen"]      = (620, 300),
    };

    public static (int Width, int Height)? Dimensions(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;

        string name = fileName;
        int dot = name.IndexOf('.');
        string family = dot < 0 ? name : name[..dot];
        if (!BaseSizes.TryGetValue(family, out var baseSize)) return null;

        // targetsize-N (optionally with altform-* prefix) -> square N x N.
        int t = fileName.IndexOf("targetsize-", StringComparison.Ordinal);
        if (t >= 0)
        {
            int n = ParseTrailingInt(fileName, t + "targetsize-".Length);
            return n > 0 ? (n, n) : null;
        }

        // scale-N -> base * N/100 (default scale-100 if absent).
        int scale = 100;
        int s = fileName.IndexOf("scale-", StringComparison.Ordinal);
        if (s >= 0)
        {
            int n = ParseTrailingInt(fileName, s + "scale-".Length);
            if (n > 0) scale = n;
        }

        int w = (int)Math.Round(baseSize.W * scale / 100.0, MidpointRounding.AwayFromZero);
        int h = (int)Math.Round(baseSize.H * scale / 100.0, MidpointRounding.AwayFromZero);
        return (w, h);
    }

    private static int ParseTrailingInt(string s, int start)
    {
        int end = start;
        while (end < s.Length && char.IsDigit(s[end])) end++;
        return end > start && int.TryParse(s.AsSpan(start, end - start), out int v) ? v : 0;
    }
}
