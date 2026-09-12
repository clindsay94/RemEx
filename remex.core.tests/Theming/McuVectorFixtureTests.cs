using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Remex.Core.Tests.Theming;

/// <summary>RemEx-4kv0g.6: the committed oracle is complete and is the same bytes the phone's tests read.</summary>
public class McuVectorFixtureTests
{
    internal static readonly string[] RoleNames =
    {
        "primaryPaletteKeyColor", "secondaryPaletteKeyColor", "tertiaryPaletteKeyColor", "neutralPaletteKeyColor", "neutralVariantPaletteKeyColor",
        "background", "onBackground", "surface", "surfaceDim", "surfaceBright", "surfaceContainerLowest", "surfaceContainerLow", "surfaceContainer",
        "surfaceContainerHigh", "surfaceContainerHighest", "onSurface", "surfaceVariant", "onSurfaceVariant", "inverseSurface", "inverseOnSurface",
        "outline", "outlineVariant", "shadow", "scrim", "surfaceTint", "primary", "onPrimary", "primaryContainer", "onPrimaryContainer", "inversePrimary",
        "secondary", "onSecondary", "secondaryContainer", "onSecondaryContainer", "tertiary", "onTertiary", "tertiaryContainer", "onTertiaryContainer",
        "error", "onError", "errorContainer", "onErrorContainer", "primaryFixed", "primaryFixedDim", "onPrimaryFixed", "onPrimaryFixedVariant",
        "secondaryFixed", "secondaryFixedDim", "onSecondaryFixed", "onSecondaryFixedVariant", "tertiaryFixed", "tertiaryFixedDim", "onTertiaryFixed",
        "onTertiaryFixedVariant", "controlActivated", "controlNormal", "controlHighlight", "textPrimaryInverse", "textSecondaryAndTertiaryInverse",
        "textPrimaryInverseDisableOnly", "textSecondaryAndTertiaryInverseDisabled", "textHintInverse",
    };

    internal static string FixturePath() => Path.Combine(AppContext.BaseDirectory, "Fixtures", "mcu-vectors.json");

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));

    [Fact]
    public void TheFixtureCoversTheWholeGridWithEveryRole()
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(FixturePath()));
        var roles = doc.RootElement.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ToArray();
        Assert.Equal(RoleNames, roles);

        var vectors = doc.RootElement.GetProperty("vectors").EnumerateArray().ToArray();
        Assert.Equal(8 * 9 * 2 * 5, vectors.Length);
        var controlHighlightIndex = Array.IndexOf(RoleNames, "controlHighlight");
        foreach (var v in vectors)
        {
            var argb = v.GetProperty("argb").EnumerateArray().Select(a => a.GetString()!).ToArray();
            Assert.Equal(RoleNames.Length, argb.Length);
            // Every role is always FF alpha EXCEPT controlHighlight, a fixed-opacity ripple/overlay
            // token by Google's own definition (MaterialDynamicColors), not a bug here.
            Assert.All(argb, hex => Assert.Matches("^#[0-9A-F]{8}$", hex));
            for (var i = 0; i < argb.Length; i++)
            {
                if (i == controlHighlightIndex) continue;
                Assert.StartsWith("#FF", argb[i]);
            }
            var dark = v.GetProperty("dark").GetBoolean();
            var expectedControlHighlight = dark ? "#33FFFFFF" : "#1F000000";
            Assert.Equal(expectedControlHighlight, argb[controlHighlightIndex]);
        }
    }

    [Fact]
    public void TheAndroidCopyIsByteIdentical()
    {
        var android = Path.Combine(RepoRoot(), "remex.android", "app", "src", "test", "resources", "mcu-vectors.json");
        Assert.True(File.Exists(android), $"missing {android}");
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(android))),
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(FixturePath()))));
    }
}
