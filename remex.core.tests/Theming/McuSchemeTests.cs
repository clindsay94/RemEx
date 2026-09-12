using Remex.Core.Theming.Mcu;

namespace Remex.Core.Tests.Theming;

/// <summary>
/// RemEx-4kv0g.10: the scheme layer against material 1.14.0's javatests -- full ports of the nine
/// Scheme*Test.java files (SchemeTonalSpotTest, SchemeVibrantTest, SchemeExpressiveTest, SchemeNeutralTest,
/// SchemeMonochromeTest, SchemeFidelityTest, SchemeContentTest, SchemeRainbowTest, SchemeFruitSaladTest),
/// DynamicColorTest.java and FixedDynamicColorTest.java (both ported as separate classes below), plus the
/// ContrastCurve anchors, RotatedHue table search and wire vocabulary. SchemeTest.java targets the unported
/// pre-DynamicScheme Scheme.java and is correctly out of scope.
/// </summary>
public class McuSchemeTests
{
    /// <summary>Parses a Java int-literal hex color ("0xff0000ff" or "0xFF0000FF") to its uint ARGB value.</summary>
    internal static uint ParseJavaHex(string javaHex) => Convert.ToUInt32(javaHex, 16);

    [Theory]
    [InlineData(SchemeVariant.TonalSpot, "0xff0000ff", false, 0.0, "primaryPaletteKeyColor", "0xff6E72AC")]
    [InlineData(SchemeVariant.TonalSpot, "0xff0000ff", false, 0.0, "secondaryPaletteKeyColor", "0xff75758B")]
    [InlineData(SchemeVariant.TonalSpot, "0xff0000ff", false, 0.0, "tertiaryPaletteKeyColor", "0xff936B84")]
    [InlineData(SchemeVariant.TonalSpot, "0xff0000ff", false, 0.0, "neutralPaletteKeyColor", "0xff77767d")]
    [InlineData(SchemeVariant.TonalSpot, "0xff0000ff", false, 0.0, "neutralVariantPaletteKeyColor", "0xff777680")]
    [InlineData(SchemeVariant.TonalSpot, "0xff0000ff", false, -1.0, "primary", "0xff6c70aa")]
    [InlineData(SchemeVariant.TonalSpot, "0xff0000ff", false, 0.0, "primary", "0xff555992")]
    [InlineData(SchemeVariant.TonalSpot, "0xff0000ff", false, 1.0, "primary", "0xff22265c")]
    [InlineData(SchemeVariant.TonalSpot, "0xff0000ff", false, -1.0, "primaryContainer", "0xffd5d6ff")]
    [InlineData(SchemeVariant.TonalSpot, "0xff0000ff", false, 0.0, "primaryContainer", "0xffe0e0ff")]
    [InlineData(SchemeVariant.TonalSpot, "0xff0000ff", false, 1.0, "primaryContainer", "0xff40447b")]
    [InlineData(SchemeVariant.TonalSpot, "0xff0000ff", false, -1.0, "onPrimaryContainer", "0xff7175b0")]
    [InlineData(SchemeVariant.TonalSpot, "0xff0000ff", false, 0.0, "onPrimaryContainer", "0xff3e4278")]
    [InlineData(SchemeVariant.TonalSpot, "0xff0000ff", false, 1.0, "onPrimaryContainer", "0xffffffff")]
    [InlineData(SchemeVariant.TonalSpot, "0xff0000ff", false, -1.0, "surface", "0xfffbf8ff")]
    [InlineData(SchemeVariant.TonalSpot, "0xff0000ff", false, 0.0, "surface", "0xfffbf8ff")]
    [InlineData(SchemeVariant.TonalSpot, "0xff0000ff", false, 1.0, "surface", "0xfffbf8ff")]
    [InlineData(SchemeVariant.TonalSpot, "0xff0000ff", false, -1.0, "onSurface", "0xff5f5e65")]
    [InlineData(SchemeVariant.TonalSpot, "0xff0000ff", false, 0.0, "onSurface", "0xff1b1b21")]
    [InlineData(SchemeVariant.TonalSpot, "0xff0000ff", false, 1.0, "onSurface", "0xff000000")]
    [InlineData(SchemeVariant.TonalSpot, "0xff0000ff", false, -1.0, "onSecondary", "0xfffffbff")]
    [InlineData(SchemeVariant.TonalSpot, "0xff0000ff", false, 0.0, "onSecondary", "0xffffffff")]
    [InlineData(SchemeVariant.TonalSpot, "0xff0000ff", false, 1.0, "onSecondary", "0xffffffff")]
    [InlineData(SchemeVariant.TonalSpot, "0xff0000ff", false, -1.0, "onTertiary", "0xfffffbff")]
    [InlineData(SchemeVariant.TonalSpot, "0xff0000ff", false, 0.0, "onTertiary", "0xffffffff")]
    [InlineData(SchemeVariant.TonalSpot, "0xff0000ff", false, 1.0, "onTertiary", "0xffffffff")]
    [InlineData(SchemeVariant.TonalSpot, "0xff0000ff", false, -1.0, "onError", "0xfffffbff")]
    [InlineData(SchemeVariant.TonalSpot, "0xff0000ff", false, 0.0, "onError", "0xffffffff")]
    [InlineData(SchemeVariant.TonalSpot, "0xff0000ff", false, 1.0, "onError", "0xffffffff")]
    [InlineData(SchemeVariant.TonalSpot, "0xff0000ff", true, -1.0, "primary", "0xff888cc8")]
    [InlineData(SchemeVariant.TonalSpot, "0xff0000ff", true, 0.0, "primary", "0xffbec2ff")]
    [InlineData(SchemeVariant.TonalSpot, "0xff0000ff", true, 1.0, "primary", "0xfff0eeff")]
    [InlineData(SchemeVariant.TonalSpot, "0xff0000ff", true, -1.0, "primaryContainer", "0xff31356b")]
    [InlineData(SchemeVariant.TonalSpot, "0xff0000ff", true, 0.0, "primaryContainer", "0xff3E4278")]
    [InlineData(SchemeVariant.TonalSpot, "0xff0000ff", true, 1.0, "primaryContainer", "0xffbabefd")]
    [InlineData(SchemeVariant.TonalSpot, "0xff0000ff", true, -1.0, "onPrimaryContainer", "0xff7b7fbb")]
    [InlineData(SchemeVariant.TonalSpot, "0xff0000ff", true, 0.0, "onPrimaryContainer", "0xffe0e0ff")]
    [InlineData(SchemeVariant.TonalSpot, "0xff0000ff", true, 1.0, "onPrimaryContainer", "0xff00003c")]
    [InlineData(SchemeVariant.TonalSpot, "0xff0000ff", true, -1.0, "onTertiaryContainer", "0xffa17891")]
    [InlineData(SchemeVariant.TonalSpot, "0xff0000ff", true, 0.0, "onTertiaryContainer", "0xffffd8ee")]
    [InlineData(SchemeVariant.TonalSpot, "0xff0000ff", true, 1.0, "onTertiaryContainer", "0xff1b0315")]
    [InlineData(SchemeVariant.TonalSpot, "0xff0000ff", true, -1.0, "onSecondary", "0xff27283b")]
    [InlineData(SchemeVariant.TonalSpot, "0xff0000ff", true, 0.0, "onSecondary", "0xff2e2f42")]
    [InlineData(SchemeVariant.TonalSpot, "0xff0000ff", true, 1.0, "onSecondary", "0xff000000")]
    [InlineData(SchemeVariant.TonalSpot, "0xff0000ff", true, -1.0, "onTertiary", "0xff3e1f34")]
    [InlineData(SchemeVariant.TonalSpot, "0xff0000ff", true, 0.0, "onTertiary", "0xff46263b")]
    [InlineData(SchemeVariant.TonalSpot, "0xff0000ff", true, 1.0, "onTertiary", "0xff000000")]
    [InlineData(SchemeVariant.TonalSpot, "0xff0000ff", true, -1.0, "onError", "0xff5c0003")]
    [InlineData(SchemeVariant.TonalSpot, "0xff0000ff", true, 0.0, "onError", "0xff690005")]
    [InlineData(SchemeVariant.TonalSpot, "0xff0000ff", true, 1.0, "onError", "0xff000000")]
    [InlineData(SchemeVariant.TonalSpot, "0xff0000ff", true, -1.0, "surface", "0xff131318")]
    [InlineData(SchemeVariant.TonalSpot, "0xff0000ff", true, 0.0, "surface", "0xff131318")]
    [InlineData(SchemeVariant.TonalSpot, "0xff0000ff", true, 1.0, "surface", "0xff131318")]
    [InlineData(SchemeVariant.TonalSpot, "0xff0000ff", true, -1.0, "onSurface", "0xffa4a2a9")]
    [InlineData(SchemeVariant.TonalSpot, "0xff0000ff", true, 0.0, "onSurface", "0xffe4e1e9")]
    [InlineData(SchemeVariant.TonalSpot, "0xff0000ff", true, 1.0, "onSurface", "0xffffffff")]
    [InlineData(SchemeVariant.Vibrant, "0xff0000ff", false, 0.0, "primaryPaletteKeyColor", "0xff080CFF")]
    [InlineData(SchemeVariant.Vibrant, "0xff0000ff", false, 0.0, "secondaryPaletteKeyColor", "0xff7B7296")]
    [InlineData(SchemeVariant.Vibrant, "0xff0000ff", false, 0.0, "tertiaryPaletteKeyColor", "0xff886C9D")]
    [InlineData(SchemeVariant.Vibrant, "0xff0000ff", false, 0.0, "neutralPaletteKeyColor", "0xff777682")]
    [InlineData(SchemeVariant.Vibrant, "0xff0000ff", false, 0.0, "neutralVariantPaletteKeyColor", "0xff767685")]
    [InlineData(SchemeVariant.Vibrant, "0xff0000ff", false, -1.0, "primary", "0xff5660ff")]
    [InlineData(SchemeVariant.Vibrant, "0xff0000ff", false, 0.0, "primary", "0xff343dff")]
    [InlineData(SchemeVariant.Vibrant, "0xff0000ff", false, 1.0, "primary", "0xff00019f")]
    [InlineData(SchemeVariant.Vibrant, "0xff0000ff", false, -1.0, "primaryContainer", "0xffd5d6ff")]
    [InlineData(SchemeVariant.Vibrant, "0xff0000ff", false, 0.0, "primaryContainer", "0xffe0e0ff")]
    [InlineData(SchemeVariant.Vibrant, "0xff0000ff", false, 1.0, "primaryContainer", "0xff0000f6")]
    [InlineData(SchemeVariant.Vibrant, "0xff0000ff", false, -1.0, "onPrimaryContainer", "0xff5e68ff")]
    [InlineData(SchemeVariant.Vibrant, "0xff0000ff", false, 0.0, "onPrimaryContainer", "0xff0000ef")]
    [InlineData(SchemeVariant.Vibrant, "0xff0000ff", false, 1.0, "onPrimaryContainer", "0xffffffff")]
    [InlineData(SchemeVariant.Vibrant, "0xff0000ff", false, -1.0, "surface", "0xfffbf8ff")]
    [InlineData(SchemeVariant.Vibrant, "0xff0000ff", false, 0.0, "surface", "0xfffbf8ff")]
    [InlineData(SchemeVariant.Vibrant, "0xff0000ff", false, 1.0, "surface", "0xfffbf8ff")]
    [InlineData(SchemeVariant.Vibrant, "0xff0000ff", true, -1.0, "primary", "0xff7c84ff")]
    [InlineData(SchemeVariant.Vibrant, "0xff0000ff", true, 0.0, "primary", "0xffbec2ff")]
    [InlineData(SchemeVariant.Vibrant, "0xff0000ff", true, 1.0, "primary", "0xfff0eeff")]
    [InlineData(SchemeVariant.Vibrant, "0xff0000ff", true, -1.0, "primaryContainer", "0xff0001c9")]
    [InlineData(SchemeVariant.Vibrant, "0xff0000ff", true, 0.0, "primaryContainer", "0xff0000ef")]
    [InlineData(SchemeVariant.Vibrant, "0xff0000ff", true, 1.0, "primaryContainer", "0xffbabdff")]
    [InlineData(SchemeVariant.Vibrant, "0xff0000ff", true, -1.0, "onPrimaryContainer", "0xff6b75ff")]
    [InlineData(SchemeVariant.Vibrant, "0xff0000ff", true, 0.0, "onPrimaryContainer", "0xffe0e0ff")]
    [InlineData(SchemeVariant.Vibrant, "0xff0000ff", true, 1.0, "onPrimaryContainer", "0xff00003d")]
    [InlineData(SchemeVariant.Vibrant, "0xff0000ff", true, -1.0, "onTertiaryContainer", "0xff9679ab")]
    [InlineData(SchemeVariant.Vibrant, "0xff0000ff", true, 0.0, "onTertiaryContainer", "0xfff2daff")]
    [InlineData(SchemeVariant.Vibrant, "0xff0000ff", true, 1.0, "onTertiaryContainer", "0xff16002a")]
    [InlineData(SchemeVariant.Vibrant, "0xff0000ff", true, -1.0, "surface", "0xff12131c")]
    [InlineData(SchemeVariant.Vibrant, "0xff0000ff", true, 0.0, "surface", "0xff12131c")]
    [InlineData(SchemeVariant.Vibrant, "0xff0000ff", true, 1.0, "surface", "0xff12131c")]
    [InlineData(SchemeVariant.Expressive, "0xff0000ff", false, 0.0, "primaryPaletteKeyColor", "0xff35855F")]
    [InlineData(SchemeVariant.Expressive, "0xff0000ff", false, 0.0, "secondaryPaletteKeyColor", "0xff8C6D8C")]
    [InlineData(SchemeVariant.Expressive, "0xff0000ff", false, 0.0, "tertiaryPaletteKeyColor", "0xff806EA1")]
    [InlineData(SchemeVariant.Expressive, "0xff0000ff", false, 0.0, "neutralPaletteKeyColor", "0xff79757F")]
    [InlineData(SchemeVariant.Expressive, "0xff0000ff", false, 0.0, "neutralVariantPaletteKeyColor", "0xff7A7585")]
    [InlineData(SchemeVariant.Expressive, "0xff0000ff", false, -1.0, "primary", "0xff32835d")]
    [InlineData(SchemeVariant.Expressive, "0xff0000ff", false, 0.0, "primary", "0xff146c48")]
    [InlineData(SchemeVariant.Expressive, "0xff0000ff", false, 1.0, "primary", "0xff00341f")]
    [InlineData(SchemeVariant.Expressive, "0xff0000ff", false, -1.0, "primaryContainer", "0xff99eabd")]
    [InlineData(SchemeVariant.Expressive, "0xff0000ff", false, 0.0, "primaryContainer", "0xffa2f4c6")]
    [InlineData(SchemeVariant.Expressive, "0xff0000ff", false, 1.0, "primaryContainer", "0xff005436")]
    [InlineData(SchemeVariant.Expressive, "0xff0000ff", false, -1.0, "onPrimaryContainer", "0xff388862")]
    [InlineData(SchemeVariant.Expressive, "0xff0000ff", false, 0.0, "onPrimaryContainer", "0xff005234")]
    [InlineData(SchemeVariant.Expressive, "0xff0000ff", false, 1.0, "onPrimaryContainer", "0xffffffff")]
    [InlineData(SchemeVariant.Expressive, "0xff0000ff", false, -1.0, "surface", "0xfffdf7ff")]
    [InlineData(SchemeVariant.Expressive, "0xff0000ff", false, 0.0, "surface", "0xfffdf7ff")]
    [InlineData(SchemeVariant.Expressive, "0xff0000ff", false, 1.0, "surface", "0xfffdf7ff")]
    [InlineData(SchemeVariant.Expressive, "0xff0000ff", true, -1.0, "primary", "0xff51a078")]
    [InlineData(SchemeVariant.Expressive, "0xff0000ff", true, 0.0, "primary", "0xff87d7ab")]
    [InlineData(SchemeVariant.Expressive, "0xff0000ff", true, 1.0, "primary", "0xffbbffd7")]
    [InlineData(SchemeVariant.Expressive, "0xff0000ff", true, -1.0, "primaryContainer", "0xff00432a")]
    [InlineData(SchemeVariant.Expressive, "0xff0000ff", true, 0.0, "primaryContainer", "0xff005234")]
    [InlineData(SchemeVariant.Expressive, "0xff0000ff", true, 1.0, "primaryContainer", "0xff83d3a8")]
    [InlineData(SchemeVariant.Expressive, "0xff0000ff", true, -1.0, "onPrimaryContainer", "0xff43936c")]
    [InlineData(SchemeVariant.Expressive, "0xff0000ff", true, 0.0, "onPrimaryContainer", "0xffa2f4c6")]
    [InlineData(SchemeVariant.Expressive, "0xff0000ff", true, 1.0, "onPrimaryContainer", "0xff000e06")]
    [InlineData(SchemeVariant.Expressive, "0xff0000ff", true, -1.0, "surface", "0xff14121a")]
    [InlineData(SchemeVariant.Expressive, "0xff0000ff", true, 0.0, "surface", "0xff14121a")]
    [InlineData(SchemeVariant.Expressive, "0xff0000ff", true, 1.0, "surface", "0xff14121a")]
    [InlineData(SchemeVariant.Neutral, "0xff0000ff", false, 0.0, "primaryPaletteKeyColor", "0xff767685")]
    [InlineData(SchemeVariant.Neutral, "0xff0000ff", false, 0.0, "secondaryPaletteKeyColor", "0xff777680")]
    [InlineData(SchemeVariant.Neutral, "0xff0000ff", false, 0.0, "tertiaryPaletteKeyColor", "0xff75758B")]
    [InlineData(SchemeVariant.Neutral, "0xff0000ff", false, 0.0, "neutralPaletteKeyColor", "0xff787678")]
    [InlineData(SchemeVariant.Neutral, "0xff0000ff", false, 0.0, "neutralVariantPaletteKeyColor", "0xff787678")]
    [InlineData(SchemeVariant.Neutral, "0xff0000ff", false, -1.0, "primary", "0xff737383")]
    [InlineData(SchemeVariant.Neutral, "0xff0000ff", false, 0.0, "primary", "0xff5d5d6c")]
    [InlineData(SchemeVariant.Neutral, "0xff0000ff", false, 1.0, "primary", "0xff2b2b38")]
    [InlineData(SchemeVariant.Neutral, "0xff0000ff", false, -1.0, "primaryContainer", "0xffd9d7e9")]
    [InlineData(SchemeVariant.Neutral, "0xff0000ff", false, 0.0, "primaryContainer", "0xffe2e1f3")]
    [InlineData(SchemeVariant.Neutral, "0xff0000ff", false, 1.0, "primaryContainer", "0xff484856")]
    [InlineData(SchemeVariant.Neutral, "0xff0000ff", false, -1.0, "onPrimaryContainer", "0xff797888")]
    [InlineData(SchemeVariant.Neutral, "0xff0000ff", false, 0.0, "onPrimaryContainer", "0xff454654")]
    [InlineData(SchemeVariant.Neutral, "0xff0000ff", false, 1.0, "onPrimaryContainer", "0xffffffff")]
    [InlineData(SchemeVariant.Neutral, "0xff0000ff", false, -1.0, "surface", "0xfffcf8fa")]
    [InlineData(SchemeVariant.Neutral, "0xff0000ff", false, 0.0, "surface", "0xfffcf8fa")]
    [InlineData(SchemeVariant.Neutral, "0xff0000ff", false, 1.0, "surface", "0xfffcf8fa")]
    [InlineData(SchemeVariant.Neutral, "0xff0000ff", true, -1.0, "primary", "0xff908f9f")]
    [InlineData(SchemeVariant.Neutral, "0xff0000ff", true, 0.0, "primary", "0xffc6c5d6")]
    [InlineData(SchemeVariant.Neutral, "0xff0000ff", true, 1.0, "primary", "0xfff0eeff")]
    [InlineData(SchemeVariant.Neutral, "0xff0000ff", true, -1.0, "primaryContainer", "0xff393947")]
    [InlineData(SchemeVariant.Neutral, "0xff0000ff", true, 0.0, "primaryContainer", "0xff454654")]
    [InlineData(SchemeVariant.Neutral, "0xff0000ff", true, 1.0, "primaryContainer", "0xffc2c1d2")]
    [InlineData(SchemeVariant.Neutral, "0xff0000ff", true, -1.0, "onPrimaryContainer", "0xff838393")]
    [InlineData(SchemeVariant.Neutral, "0xff0000ff", true, 0.0, "onPrimaryContainer", "0xffe2e1f3")]
    [InlineData(SchemeVariant.Neutral, "0xff0000ff", true, 1.0, "onPrimaryContainer", "0xff090a16")]
    [InlineData(SchemeVariant.Neutral, "0xff0000ff", true, -1.0, "onTertiaryContainer", "0xff828299")]
    [InlineData(SchemeVariant.Neutral, "0xff0000ff", true, 0.0, "onTertiaryContainer", "0xffe1e0f9")]
    [InlineData(SchemeVariant.Neutral, "0xff0000ff", true, 1.0, "onTertiaryContainer", "0xff080a1b")]
    [InlineData(SchemeVariant.Neutral, "0xff0000ff", true, -1.0, "surface", "0xff131315")]
    [InlineData(SchemeVariant.Neutral, "0xff0000ff", true, 0.0, "surface", "0xff131315")]
    [InlineData(SchemeVariant.Neutral, "0xff0000ff", true, 1.0, "surface", "0xff131315")]
    [InlineData(SchemeVariant.Monochrome, "0xff0000ff", false, 0.0, "primaryPaletteKeyColor", "0xff777777")]
    [InlineData(SchemeVariant.Monochrome, "0xff0000ff", false, 0.0, "secondaryPaletteKeyColor", "0xff777777")]
    [InlineData(SchemeVariant.Monochrome, "0xff0000ff", false, 0.0, "tertiaryPaletteKeyColor", "0xff777777")]
    [InlineData(SchemeVariant.Monochrome, "0xff0000ff", false, 0.0, "neutralPaletteKeyColor", "0xff777777")]
    [InlineData(SchemeVariant.Monochrome, "0xff0000ff", false, 0.0, "neutralVariantPaletteKeyColor", "0xff777777")]
    [InlineData(SchemeVariant.Monochrome, "0xff0000ff", false, -1.0, "primary", "0xff747474")]
    [InlineData(SchemeVariant.Monochrome, "0xff0000ff", false, 0.0, "primary", "0xff000000")]
    [InlineData(SchemeVariant.Monochrome, "0xff0000ff", false, 1.0, "primary", "0xff000000")]
    [InlineData(SchemeVariant.Monochrome, "0xff0000ff", false, -1.0, "primaryContainer", "0xffd9d9d9")]
    [InlineData(SchemeVariant.Monochrome, "0xff0000ff", false, 0.0, "primaryContainer", "0xff3b3b3b")]
    [InlineData(SchemeVariant.Monochrome, "0xff0000ff", false, 1.0, "primaryContainer", "0xff3b3b3b")]
    [InlineData(SchemeVariant.Monochrome, "0xff0000ff", false, -1.0, "onPrimaryContainer", "0xff7a7a7a")]
    [InlineData(SchemeVariant.Monochrome, "0xff0000ff", false, 0.0, "onPrimaryContainer", "0xffffffff")]
    [InlineData(SchemeVariant.Monochrome, "0xff0000ff", false, 1.0, "onPrimaryContainer", "0xffffffff")]
    [InlineData(SchemeVariant.Monochrome, "0xff0000ff", false, -1.0, "surface", "0xfff9f9f9")]
    [InlineData(SchemeVariant.Monochrome, "0xff0000ff", false, 0.0, "surface", "0xfff9f9f9")]
    [InlineData(SchemeVariant.Monochrome, "0xff0000ff", false, 1.0, "surface", "0xfff9f9f9")]
    [InlineData(SchemeVariant.Monochrome, "0xff0000ff", true, -1.0, "primary", "0xff919191")]
    [InlineData(SchemeVariant.Monochrome, "0xff0000ff", true, 0.0, "primary", "0xffffffff")]
    [InlineData(SchemeVariant.Monochrome, "0xff0000ff", true, 1.0, "primary", "0xffffffff")]
    [InlineData(SchemeVariant.Monochrome, "0xff0000ff", true, -1.0, "primaryContainer", "0xff3a3a3a")]
    [InlineData(SchemeVariant.Monochrome, "0xff0000ff", true, 0.0, "primaryContainer", "0xffd4d4d4")]
    [InlineData(SchemeVariant.Monochrome, "0xff0000ff", true, 1.0, "primaryContainer", "0xffd4d4d4")]
    [InlineData(SchemeVariant.Monochrome, "0xff0000ff", true, -1.0, "onPrimaryContainer", "0xff848484")]
    [InlineData(SchemeVariant.Monochrome, "0xff0000ff", true, 0.0, "onPrimaryContainer", "0xff000000")]
    [InlineData(SchemeVariant.Monochrome, "0xff0000ff", true, 1.0, "onPrimaryContainer", "0xff000000")]
    [InlineData(SchemeVariant.Monochrome, "0xff0000ff", true, -1.0, "onTertiaryContainer", "0xff848484")]
    [InlineData(SchemeVariant.Monochrome, "0xff0000ff", true, 0.0, "onTertiaryContainer", "0xff000000")]
    [InlineData(SchemeVariant.Monochrome, "0xff0000ff", true, 1.0, "onTertiaryContainer", "0xff000000")]
    [InlineData(SchemeVariant.Monochrome, "0xff0000ff", true, -1.0, "surface", "0xff131313")]
    [InlineData(SchemeVariant.Monochrome, "0xff0000ff", true, 0.0, "surface", "0xff131313")]
    [InlineData(SchemeVariant.Monochrome, "0xff0000ff", true, 1.0, "surface", "0xff131313")]
    [InlineData(SchemeVariant.Fidelity, "0xff0000ff", false, 0.0, "primaryPaletteKeyColor", "0xff080CFF")]
    [InlineData(SchemeVariant.Fidelity, "0xff0000ff", false, 0.0, "secondaryPaletteKeyColor", "0xff656DD3")]
    [InlineData(SchemeVariant.Fidelity, "0xff0000ff", false, 0.0, "tertiaryPaletteKeyColor", "0xff9D0002")]
    [InlineData(SchemeVariant.Fidelity, "0xff0000ff", false, 0.0, "neutralPaletteKeyColor", "0xff767684")]
    [InlineData(SchemeVariant.Fidelity, "0xff0000ff", false, 0.0, "neutralVariantPaletteKeyColor", "0xff757589")]
    [InlineData(SchemeVariant.Fidelity, "0xff0000ff", false, -1.0, "primary", "0xff5660ff")]
    [InlineData(SchemeVariant.Fidelity, "0xff0000ff", false, 0.0, "primary", "0xff0001bb")]
    [InlineData(SchemeVariant.Fidelity, "0xff0000ff", false, 1.0, "primary", "0xff00019f")]
    [InlineData(SchemeVariant.Fidelity, "0xff0000ff", false, -1.0, "primaryContainer", "0xffd5d6ff")]
    [InlineData(SchemeVariant.Fidelity, "0xff0000ff", false, 0.0, "primaryContainer", "0xff0000ff")]
    [InlineData(SchemeVariant.Fidelity, "0xff0000ff", false, 1.0, "primaryContainer", "0xff0000f6")]
    [InlineData(SchemeVariant.Fidelity, "0xff0000ff", false, -1.0, "tertiaryContainer", "0xffffcdc6")]
    [InlineData(SchemeVariant.Fidelity, "0xff0000ff", false, 0.0, "tertiaryContainer", "0xff9d0002")]
    [InlineData(SchemeVariant.Fidelity, "0xff0000ff", false, 1.0, "tertiaryContainer", "0xff980002")]
    [InlineData(SchemeVariant.Fidelity, "0xff850096", false, -1.0, "tertiaryContainer", "0xffebd982")]
    [InlineData(SchemeVariant.Fidelity, "0xff850096", false, 0.0, "tertiaryContainer", "0xffbcac5a")]
    [InlineData(SchemeVariant.Fidelity, "0xff850096", false, 1.0, "tertiaryContainer", "0xff544900")]
    [InlineData(SchemeVariant.Fidelity, "0xff0000ff", false, -1.0, "onPrimaryContainer", "0xff5e68ff")]
    [InlineData(SchemeVariant.Fidelity, "0xff0000ff", false, 0.0, "onPrimaryContainer", "0xffb3b7ff")]
    [InlineData(SchemeVariant.Fidelity, "0xff0000ff", false, 1.0, "onPrimaryContainer", "0xffffffff")]
    [InlineData(SchemeVariant.Fidelity, "0xff0000ff", false, -1.0, "surface", "0xfffbf8ff")]
    [InlineData(SchemeVariant.Fidelity, "0xff0000ff", false, 0.0, "surface", "0xfffbf8ff")]
    [InlineData(SchemeVariant.Fidelity, "0xff0000ff", false, 1.0, "surface", "0xfffbf8ff")]
    [InlineData(SchemeVariant.Fidelity, "0xff0000ff", true, -1.0, "primary", "0xff7c84ff")]
    [InlineData(SchemeVariant.Fidelity, "0xff0000ff", true, 0.0, "primary", "0xffbec2ff")]
    [InlineData(SchemeVariant.Fidelity, "0xff0000ff", true, 1.0, "primary", "0xfff0eeff")]
    [InlineData(SchemeVariant.Fidelity, "0xff0000ff", true, -1.0, "primaryContainer", "0xff0001c9")]
    [InlineData(SchemeVariant.Fidelity, "0xff0000ff", true, 0.0, "primaryContainer", "0xff0000ff")]
    [InlineData(SchemeVariant.Fidelity, "0xff0000ff", true, 1.0, "primaryContainer", "0xffbabdff")]
    [InlineData(SchemeVariant.Fidelity, "0xff0000ff", true, -1.0, "onPrimaryContainer", "0xff6b75ff")]
    [InlineData(SchemeVariant.Fidelity, "0xff0000ff", true, 0.0, "onPrimaryContainer", "0xffb3b7ff")]
    [InlineData(SchemeVariant.Fidelity, "0xff0000ff", true, 1.0, "onPrimaryContainer", "0xff00003d")]
    [InlineData(SchemeVariant.Fidelity, "0xff0000ff", true, -1.0, "onTertiaryContainer", "0xffef4635")]
    [InlineData(SchemeVariant.Fidelity, "0xff0000ff", true, 0.0, "onTertiaryContainer", "0xffffa598")]
    [InlineData(SchemeVariant.Fidelity, "0xff0000ff", true, 1.0, "onTertiaryContainer", "0xff220000")]
    [InlineData(SchemeVariant.Fidelity, "0xff0000ff", true, -1.0, "surface", "0xff12121d")]
    [InlineData(SchemeVariant.Fidelity, "0xff0000ff", true, 0.0, "surface", "0xff12121d")]
    [InlineData(SchemeVariant.Fidelity, "0xff0000ff", true, 1.0, "surface", "0xff12121d")]
    [InlineData(SchemeVariant.Content, "0xff0000ff", false, 0.0, "primaryPaletteKeyColor", "0xff080CFF")]
    [InlineData(SchemeVariant.Content, "0xff0000ff", false, 0.0, "secondaryPaletteKeyColor", "0xff656DD3")]
    [InlineData(SchemeVariant.Content, "0xff0000ff", false, 0.0, "tertiaryPaletteKeyColor", "0xff81009F")]
    [InlineData(SchemeVariant.Content, "0xff0000ff", false, 0.0, "neutralPaletteKeyColor", "0xff767684")]
    [InlineData(SchemeVariant.Content, "0xff0000ff", false, 0.0, "neutralVariantPaletteKeyColor", "0xff757589")]
    [InlineData(SchemeVariant.Content, "0xFF0000ff", false, -1.0, "primary", "0xFF5660ff")]
    [InlineData(SchemeVariant.Content, "0xFF0000ff", false, 0.0, "primary", "0xFF0001bb")]
    [InlineData(SchemeVariant.Content, "0xFF0000ff", false, 1.0, "primary", "0xFF00019f")]
    [InlineData(SchemeVariant.Content, "0xFF0000ff", false, -1.0, "primaryContainer", "0xFFd5d6ff")]
    [InlineData(SchemeVariant.Content, "0xFF0000ff", false, 0.0, "primaryContainer", "0xFF0000ff")]
    [InlineData(SchemeVariant.Content, "0xFF0000ff", false, 1.0, "primaryContainer", "0xFF0000f6")]
    [InlineData(SchemeVariant.Content, "0xFF0000ff", false, -1.0, "tertiaryContainer", "0xfffac9ff")]
    [InlineData(SchemeVariant.Content, "0xFF0000ff", false, 0.0, "tertiaryContainer", "0xff81009f")]
    [InlineData(SchemeVariant.Content, "0xFF0000ff", false, 1.0, "tertiaryContainer", "0xFF7d009a")]
    [InlineData(SchemeVariant.Content, "0xFF850096", false, -1.0, "tertiaryContainer", "0xffffccd7")]
    [InlineData(SchemeVariant.Content, "0xFF850096", false, 0.0, "tertiaryContainer", "0xFF980249")]
    [InlineData(SchemeVariant.Content, "0xFF850096", false, 1.0, "tertiaryContainer", "0xFF930046")]
    [InlineData(SchemeVariant.Content, "0xFF0000ff", false, -1.0, "onPrimaryContainer", "0xFF5e68ff")]
    [InlineData(SchemeVariant.Content, "0xFF0000ff", false, 0.0, "onPrimaryContainer", "0xFFb3b7ff")]
    [InlineData(SchemeVariant.Content, "0xFF0000ff", false, 1.0, "onPrimaryContainer", "0xffffffff")]
    [InlineData(SchemeVariant.Content, "0xFF0000ff", false, -1.0, "surface", "0xFFFBF8FF")]
    [InlineData(SchemeVariant.Content, "0xFF0000ff", false, 0.0, "surface", "0xFFFBF8FF")]
    [InlineData(SchemeVariant.Content, "0xFF0000ff", false, 1.0, "surface", "0xFFFBF8FF")]
    [InlineData(SchemeVariant.Content, "0xFF0000ff", true, -1.0, "primary", "0xFF7c84ff")]
    [InlineData(SchemeVariant.Content, "0xFF0000ff", true, 0.0, "primary", "0xFFBEC2FF")]
    [InlineData(SchemeVariant.Content, "0xFF0000ff", true, 1.0, "primary", "0xFFf0eeff")]
    [InlineData(SchemeVariant.Content, "0xFF0000ff", true, -1.0, "primaryContainer", "0xFF0001c9")]
    [InlineData(SchemeVariant.Content, "0xFF0000ff", true, 0.0, "primaryContainer", "0xFF0000ff")]
    [InlineData(SchemeVariant.Content, "0xFF0000ff", true, 1.0, "primaryContainer", "0xFFbabdff")]
    [InlineData(SchemeVariant.Content, "0xFF0000ff", true, -1.0, "onPrimaryContainer", "0xFF6b75ff")]
    [InlineData(SchemeVariant.Content, "0xFF0000ff", true, 0.0, "onPrimaryContainer", "0xFFb3b7ff")]
    [InlineData(SchemeVariant.Content, "0xFF0000ff", true, 1.0, "onPrimaryContainer", "0xFF00003d")]
    [InlineData(SchemeVariant.Content, "0xFF0000ff", true, -1.0, "onTertiaryContainer", "0xFFc254de")]
    [InlineData(SchemeVariant.Content, "0xFF0000ff", true, 0.0, "onTertiaryContainer", "0xFFf09fff")]
    [InlineData(SchemeVariant.Content, "0xFF0000ff", true, 1.0, "onTertiaryContainer", "0xFF1a0022")]
    [InlineData(SchemeVariant.Content, "0xFF0000ff", true, -1.0, "surface", "0xFF12121D")]
    [InlineData(SchemeVariant.Content, "0xFF0000ff", true, 0.0, "surface", "0xFF12121D")]
    [InlineData(SchemeVariant.Content, "0xFF0000ff", true, 1.0, "surface", "0xFF12121D")]
    [InlineData(SchemeVariant.Rainbow, "0xff0000ff", false, 0.0, "primaryPaletteKeyColor", "0xff696FC4")]
    [InlineData(SchemeVariant.Rainbow, "0xff0000ff", false, 0.0, "secondaryPaletteKeyColor", "0xff75758B")]
    [InlineData(SchemeVariant.Rainbow, "0xff0000ff", false, 0.0, "tertiaryPaletteKeyColor", "0xff936B84")]
    [InlineData(SchemeVariant.Rainbow, "0xff0000ff", false, 0.0, "neutralPaletteKeyColor", "0xff777777")]
    [InlineData(SchemeVariant.Rainbow, "0xff0000ff", false, 0.0, "neutralVariantPaletteKeyColor", "0xff777777")]
    [InlineData(SchemeVariant.Rainbow, "0xff0000ff", false, -1.0, "primary", "0xff676DC1")]
    [InlineData(SchemeVariant.Rainbow, "0xff0000ff", false, 0.0, "primary", "0xff5056A9")]
    [InlineData(SchemeVariant.Rainbow, "0xff0000ff", false, 1.0, "primary", "0xff1b2074")]
    [InlineData(SchemeVariant.Rainbow, "0xff0000ff", false, -1.0, "primaryContainer", "0xffd5d6ff")]
    [InlineData(SchemeVariant.Rainbow, "0xff0000ff", false, 0.0, "primaryContainer", "0xffE0E0FF")]
    [InlineData(SchemeVariant.Rainbow, "0xff0000ff", false, 1.0, "primaryContainer", "0xff3a4092")]
    [InlineData(SchemeVariant.Rainbow, "0xff0000ff", false, -1.0, "tertiaryContainer", "0xfffbcbe7")]
    [InlineData(SchemeVariant.Rainbow, "0xff0000ff", false, 0.0, "tertiaryContainer", "0xffffd8ee")]
    [InlineData(SchemeVariant.Rainbow, "0xff0000ff", false, 1.0, "tertiaryContainer", "0xff613e55")]
    [InlineData(SchemeVariant.Rainbow, "0xff0000ff", false, -1.0, "onPrimaryContainer", "0xff6c72c7")]
    [InlineData(SchemeVariant.Rainbow, "0xff0000ff", false, 0.0, "onPrimaryContainer", "0xff383e8f")]
    [InlineData(SchemeVariant.Rainbow, "0xff0000ff", false, 1.0, "onPrimaryContainer", "0xffffffff")]
    [InlineData(SchemeVariant.Rainbow, "0xff0000ff", false, -1.0, "surface", "0xfff9f9f9")]
    [InlineData(SchemeVariant.Rainbow, "0xff0000ff", false, 0.0, "surface", "0xfff9f9f9")]
    [InlineData(SchemeVariant.Rainbow, "0xff0000ff", false, 1.0, "surface", "0xfff9f9f9")]
    [InlineData(SchemeVariant.Rainbow, "0xff0000ff", false, 0.0, "secondary", "0xff5c5d72")]
    [InlineData(SchemeVariant.Rainbow, "0xff0000ff", false, 0.0, "secondaryContainer", "0xffe1e0f9")]
    [InlineData(SchemeVariant.Rainbow, "0xff0000ff", true, -1.0, "primary", "0xff8389e0")]
    [InlineData(SchemeVariant.Rainbow, "0xff0000ff", true, 0.0, "primary", "0xffbec2ff")]
    [InlineData(SchemeVariant.Rainbow, "0xff0000ff", true, 1.0, "primary", "0xfff0eeff")]
    [InlineData(SchemeVariant.Rainbow, "0xff0000ff", true, -1.0, "primaryContainer", "0xff2a3082")]
    [InlineData(SchemeVariant.Rainbow, "0xff0000ff", true, 0.0, "primaryContainer", "0xff383E8F")]
    [InlineData(SchemeVariant.Rainbow, "0xff0000ff", true, 1.0, "primaryContainer", "0xffbabdff")]
    [InlineData(SchemeVariant.Rainbow, "0xff0000ff", true, -1.0, "onPrimaryContainer", "0xff767cd2")]
    [InlineData(SchemeVariant.Rainbow, "0xff0000ff", true, 0.0, "onPrimaryContainer", "0xffe0e0ff")]
    [InlineData(SchemeVariant.Rainbow, "0xff0000ff", true, 1.0, "onPrimaryContainer", "0xff00003d")]
    [InlineData(SchemeVariant.Rainbow, "0xff0000ff", true, -1.0, "onTertiaryContainer", "0xffa17891")]
    [InlineData(SchemeVariant.Rainbow, "0xff0000ff", true, 0.0, "onTertiaryContainer", "0xffffd8ee")]
    [InlineData(SchemeVariant.Rainbow, "0xff0000ff", true, 1.0, "onTertiaryContainer", "0xff1b0315")]
    [InlineData(SchemeVariant.Rainbow, "0xff0000ff", true, -1.0, "surface", "0xff131313")]
    [InlineData(SchemeVariant.Rainbow, "0xff0000ff", true, 0.0, "surface", "0xff131313")]
    [InlineData(SchemeVariant.Rainbow, "0xff0000ff", true, 1.0, "surface", "0xff131313")]
    [InlineData(SchemeVariant.Rainbow, "0xff0000ff", true, 0.0, "secondary", "0xffc5c4dd")]
    [InlineData(SchemeVariant.Rainbow, "0xff0000ff", true, 0.0, "secondaryContainer", "0xff444559")]
    [InlineData(SchemeVariant.FruitSalad, "0xff0000ff", false, 0.0, "primaryPaletteKeyColor", "0xff0393c3")]
    [InlineData(SchemeVariant.FruitSalad, "0xff0000ff", false, 0.0, "secondaryPaletteKeyColor", "0xff3A7E9E")]
    [InlineData(SchemeVariant.FruitSalad, "0xff0000ff", false, 0.0, "tertiaryPaletteKeyColor", "0xff6E72AC")]
    [InlineData(SchemeVariant.FruitSalad, "0xff0000ff", false, 0.0, "neutralPaletteKeyColor", "0xff777682")]
    [InlineData(SchemeVariant.FruitSalad, "0xff0000ff", false, 0.0, "neutralVariantPaletteKeyColor", "0xff75758B")]
    [InlineData(SchemeVariant.FruitSalad, "0xff0000ff", false, -1.0, "primary", "0xff007ea7")]
    [InlineData(SchemeVariant.FruitSalad, "0xff0000ff", false, 0.0, "primary", "0xff006688")]
    [InlineData(SchemeVariant.FruitSalad, "0xff0000ff", false, 1.0, "primary", "0xff003042")]
    [InlineData(SchemeVariant.FruitSalad, "0xff0000ff", false, -1.0, "primaryContainer", "0xffaae0ff")]
    [InlineData(SchemeVariant.FruitSalad, "0xff0000ff", false, 0.0, "primaryContainer", "0xffC2E8FF")]
    [InlineData(SchemeVariant.FruitSalad, "0xff0000ff", false, 1.0, "primaryContainer", "0xff004f6b")]
    [InlineData(SchemeVariant.FruitSalad, "0xff0000ff", false, -1.0, "tertiaryContainer", "0xffd5d6ff")]
    [InlineData(SchemeVariant.FruitSalad, "0xff0000ff", false, 0.0, "tertiaryContainer", "0xffE0E0FF")]
    [InlineData(SchemeVariant.FruitSalad, "0xff0000ff", false, 1.0, "tertiaryContainer", "0xff40447b")]
    [InlineData(SchemeVariant.FruitSalad, "0xff0000ff", false, -1.0, "onPrimaryContainer", "0xff0083ae")]
    [InlineData(SchemeVariant.FruitSalad, "0xff0000ff", false, 0.0, "onPrimaryContainer", "0xff004d67")]
    [InlineData(SchemeVariant.FruitSalad, "0xff0000ff", false, 1.0, "onPrimaryContainer", "0xffffffff")]
    [InlineData(SchemeVariant.FruitSalad, "0xff0000ff", false, -1.0, "surface", "0xfffbf8ff")]
    [InlineData(SchemeVariant.FruitSalad, "0xff0000ff", false, 0.0, "surface", "0xfffbf8ff")]
    [InlineData(SchemeVariant.FruitSalad, "0xff0000ff", false, 1.0, "surface", "0xfffbf8ff")]
    [InlineData(SchemeVariant.FruitSalad, "0xff0000ff", false, 0.0, "secondary", "0xff196584")]
    [InlineData(SchemeVariant.FruitSalad, "0xff0000ff", false, 0.0, "secondaryContainer", "0xffc2e8ff")]
    [InlineData(SchemeVariant.FruitSalad, "0xff0000ff", true, -1.0, "primary", "0xff1e9bcb")]
    [InlineData(SchemeVariant.FruitSalad, "0xff0000ff", true, 0.0, "primary", "0xFF76D1FF")]
    [InlineData(SchemeVariant.FruitSalad, "0xff0000ff", true, 1.0, "primary", "0xFFe0f3ff")]
    [InlineData(SchemeVariant.FruitSalad, "0xff0000ff", true, -1.0, "primaryContainer", "0xff003f56")]
    [InlineData(SchemeVariant.FruitSalad, "0xff0000ff", true, 0.0, "primaryContainer", "0xFF004D67")]
    [InlineData(SchemeVariant.FruitSalad, "0xff0000ff", true, 1.0, "primaryContainer", "0xFF68ceff")]
    [InlineData(SchemeVariant.FruitSalad, "0xff0000ff", true, -1.0, "onPrimaryContainer", "0xff008ebc")]
    [InlineData(SchemeVariant.FruitSalad, "0xff0000ff", true, 0.0, "onPrimaryContainer", "0xffC2E8FF")]
    [InlineData(SchemeVariant.FruitSalad, "0xff0000ff", true, 1.0, "onPrimaryContainer", "0xFF000d15")]
    [InlineData(SchemeVariant.FruitSalad, "0xff0000ff", true, -1.0, "onTertiaryContainer", "0xff7b7fbb")]
    [InlineData(SchemeVariant.FruitSalad, "0xff0000ff", true, 0.0, "onTertiaryContainer", "0xffe0e0ff")]
    [InlineData(SchemeVariant.FruitSalad, "0xff0000ff", true, 1.0, "onTertiaryContainer", "0xFF00003c")]
    [InlineData(SchemeVariant.FruitSalad, "0xff0000ff", true, -1.0, "surface", "0xff12131c")]
    [InlineData(SchemeVariant.FruitSalad, "0xff0000ff", true, 0.0, "surface", "0xff12131c")]
    [InlineData(SchemeVariant.FruitSalad, "0xff0000ff", true, 1.0, "surface", "0xff12131c")]
    [InlineData(SchemeVariant.FruitSalad, "0xff0000ff", true, 0.0, "secondary", "0xff8ecff2")]
    [InlineData(SchemeVariant.FruitSalad, "0xff0000ff", true, 0.0, "secondaryContainer", "0xff004d67")]
    public void GoogleScheme_Argb_MatchesJava(SchemeVariant variant, string seedHex, bool dark, double contrast, string role, string expectedHex)
        => Assert.Equal(ParseJavaHex(expectedHex), McuScheme.Build(ParseJavaHex(seedHex), variant, dark, contrast)[role]);

    [Theory]
    [InlineData(SchemeVariant.Monochrome, "0xff0000ff", true, 0.0, "primary", 100)]
    [InlineData(SchemeVariant.Monochrome, "0xff0000ff", true, 0.0, "onPrimary", 10)]
    [InlineData(SchemeVariant.Monochrome, "0xff0000ff", true, 0.0, "primaryContainer", 85)]
    [InlineData(SchemeVariant.Monochrome, "0xff0000ff", true, 0.0, "onPrimaryContainer", 0)]
    [InlineData(SchemeVariant.Monochrome, "0xff0000ff", true, 0.0, "secondary", 80)]
    [InlineData(SchemeVariant.Monochrome, "0xff0000ff", true, 0.0, "onSecondary", 10)]
    [InlineData(SchemeVariant.Monochrome, "0xff0000ff", true, 0.0, "secondaryContainer", 30)]
    [InlineData(SchemeVariant.Monochrome, "0xff0000ff", true, 0.0, "onSecondaryContainer", 90)]
    [InlineData(SchemeVariant.Monochrome, "0xff0000ff", true, 0.0, "tertiary", 90)]
    [InlineData(SchemeVariant.Monochrome, "0xff0000ff", true, 0.0, "onTertiary", 10)]
    [InlineData(SchemeVariant.Monochrome, "0xff0000ff", true, 0.0, "tertiaryContainer", 60)]
    [InlineData(SchemeVariant.Monochrome, "0xff0000ff", true, 0.0, "onTertiaryContainer", 0)]
    [InlineData(SchemeVariant.Monochrome, "0xff0000ff", false, 0.0, "primary", 0)]
    [InlineData(SchemeVariant.Monochrome, "0xff0000ff", false, 0.0, "onPrimary", 90)]
    [InlineData(SchemeVariant.Monochrome, "0xff0000ff", false, 0.0, "primaryContainer", 25)]
    [InlineData(SchemeVariant.Monochrome, "0xff0000ff", false, 0.0, "onPrimaryContainer", 100)]
    [InlineData(SchemeVariant.Monochrome, "0xff0000ff", false, 0.0, "secondary", 40)]
    [InlineData(SchemeVariant.Monochrome, "0xff0000ff", false, 0.0, "onSecondary", 100)]
    [InlineData(SchemeVariant.Monochrome, "0xff0000ff", false, 0.0, "secondaryContainer", 85)]
    [InlineData(SchemeVariant.Monochrome, "0xff0000ff", false, 0.0, "onSecondaryContainer", 10)]
    [InlineData(SchemeVariant.Monochrome, "0xff0000ff", false, 0.0, "tertiary", 25)]
    [InlineData(SchemeVariant.Monochrome, "0xff0000ff", false, 0.0, "onTertiary", 90)]
    [InlineData(SchemeVariant.Monochrome, "0xff0000ff", false, 0.0, "tertiaryContainer", 49)]
    [InlineData(SchemeVariant.Monochrome, "0xff0000ff", false, 0.0, "onTertiaryContainer", 100)]
    // SchemeMonochromeTest.{light,dark}Theme_monochromeSpec -- Google asserts these tones isWithin(1), not exactly.
    public void GoogleScheme_Tone_MatchesJavaWithinOne(SchemeVariant variant, string seedHex, bool dark, double contrast, string role, double expectedTone)
    {
        var scheme = McuScheme.Create(ParseJavaHex(seedHex), variant, dark, contrast);
        var colors = new MaterialDynamicColors();
        DynamicColor color = role switch
        {
            "primary" => colors.Primary(),
            "onPrimary" => colors.OnPrimary(),
            "primaryContainer" => colors.PrimaryContainer(),
            "onPrimaryContainer" => colors.OnPrimaryContainer(),
            "secondary" => colors.Secondary(),
            "onSecondary" => colors.OnSecondary(),
            "secondaryContainer" => colors.SecondaryContainer(),
            "onSecondaryContainer" => colors.OnSecondaryContainer(),
            "tertiary" => colors.Tertiary(),
            "onTertiary" => colors.OnTertiary(),
            "tertiaryContainer" => colors.TertiaryContainer(),
            "onTertiaryContainer" => colors.OnTertiaryContainer(),
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, "unexpected role in the Monochrome tone-within-1 rows"),
        };
        double actual = color.GetHct(scheme).Tone;
        Assert.True(Math.Abs(actual - expectedTone) <= 1.0, $"{variant} dark={dark} {role}: expected tone within 1 of {expectedTone}, got {actual}");
    }

    [Theory]
    [InlineData(-1.0, 3.0)] [InlineData(-0.5, 3.75)] [InlineData(0.0, 4.5)] [InlineData(0.25, 5.75)] [InlineData(0.5, 7.0)] [InlineData(1.0, 11.0)] [InlineData(2.0, 11.0)] [InlineData(-3.0, 3.0)]
    public void ContrastCurve_InterpolatesBetweenItsFourAnchors(double level, double expected)
        => Assert.Equal(expected, new ContrastCurve(3.0, 4.5, 7.0, 11.0).Get(level), 0.0001);

    [Fact]
    public void DynamicScheme_RotatedHue_UsesTheTable()
    {
        // The single-rotation shortcut (rotations.Length == 1) is exercised, end to end, by every
        // Vibrant/Expressive row in GoogleScheme_Argb_MatchesJava above (their secondary/tertiary palettes
        // are not built that way, but the shortcut branch itself is trivial: SanitizeDegreesDouble(hue
        // + rotations[0]) -- asserting it here would just restate the implementation). This test only
        // covers the multi-bucket table search, which the vector grid exercises but does not isolate.
        var hues = new double[] { 0, 41, 61, 101, 131, 181, 251, 301, 360 };            // SchemeVibrant.HUES
        var rotations = new double[] { 18, 15, 10, 12, 15, 18, 15, 12, 12 };           // SchemeVibrant.SECONDARY_ROTATIONS
        var fiftyHueHct = Hct.From(50.0, 40.0, 50.0);
        Assert.Equal(MathUtils.SanitizeDegreesDouble(fiftyHueHct.Hue + 15.0),
            DynamicScheme.GetRotatedHue(fiftyHueHct, hues, rotations), 0.0001);
    }

    [Fact]
    public void Wire_RoundTripsAllNineAndFallsBackToTonalSpot()
    {
        var expected = new[] { "monochrome", "neutral", "tonal_spot", "vibrant", "expressive", "fidelity", "content", "rainbow", "fruit_salad" };
        Assert.Equal(expected, SchemeVariantWire.All.Select(v => v.ToWire()).ToArray());
        foreach (var v in SchemeVariantWire.All)
        {
            Assert.True(SchemeVariantWire.TryFromWire(v.ToWire(), out var back));
            Assert.Equal(v, back);
        }
        Assert.False(SchemeVariantWire.TryFromWire("spritz", out _));
        Assert.False(SchemeVariantWire.TryFromWire("TonalSpot", out _));   // exact wire form only; the desktop's PascalCase table is SchemeVariants (Task 7)
        Assert.Equal(SchemeVariant.TonalSpot, SchemeVariantWire.FromWireOrDefault("spritz"));
        Assert.Equal(SchemeVariant.TonalSpot, SchemeVariantWire.FromWireOrDefault(null));
    }

    [Fact]
    public void EveryVariantIsConstructibleAndErrorPaletteIsAlwaysTheFixedRed()
    {
        foreach (var v in SchemeVariantWire.All)
        {
            var s = McuScheme.Create(0xFF6750A4u, v, true, 0.0);
            Assert.Equal(v, s.Variant);
            Assert.Equal(25.0, s.ErrorPalette.Hue, 0.0001);
            Assert.Equal(84.0, s.ErrorPalette.Chroma, 0.0001);
        }
    }
}

/// <summary>Ported from material 1.14.0's DynamicColorTest.java (RemEx-4kv0g.10 review finding 2).</summary>
public class DynamicColorTests
{
    private static readonly MaterialDynamicColors Colors = new();

    [Fact]
    public void FromArgbNoBackground_DoesntChangeForContrast()
    {
        const uint blueArgb = 0xff0000ffu;
        var dynamicColor = DynamicColor.FromArgb("blue", blueArgb);

        var standardContrast = new SchemeTonalSpot(Hct.FromInt(blueArgb), false, 0.0);
        Assert.Equal(blueArgb, dynamicColor.GetArgb(standardContrast));

        var minContrast = new SchemeTonalSpot(Hct.FromInt(blueArgb), false, -1.0);
        Assert.Equal(blueArgb, dynamicColor.GetArgb(minContrast));

        var maxContrast = new SchemeTonalSpot(Hct.FromInt(blueArgb), false, 1.0);
        Assert.Equal(blueArgb, dynamicColor.GetArgb(maxContrast));
    }

    [Fact]
    public void DynamicColor_WithOpacity()
    {
        var dynamicColor = new DynamicColor(
            name: "control",
            palette: s => s.PrimaryPalette,
            tone: s => s.IsDark ? 100.0 : 0.0,
            isBackground: false,
            background: null,
            secondBackground: null,
            contrastCurve: null,
            toneDeltaPair: null,
            opacity: s => s.IsDark ? 0.20 : 0.12);

        var lightScheme = new SchemeTonalSpot(Hct.FromInt(0xff4285f4u), false, 0.0);
        Assert.Equal(0x1f000000u, dynamicColor.GetArgb(lightScheme));

        var darkScheme = new SchemeTonalSpot(Hct.FromInt(0xff4285f4u), true, 0.0);
        Assert.Equal(0x33ffffffu, dynamicColor.GetArgb(darkScheme));
    }

    [Fact]
    public void RespectsContrast()
    {
        var seedColors = new[]
        {
            Hct.FromInt(0xffff0000u), Hct.FromInt(0xffffff00u), Hct.FromInt(0xff00ff00u), Hct.FromInt(0xff0000ffu),
        };
        var contrastLevels = new[] { -1.0, -0.5, 0.0, 0.5, 1.0 };

        foreach (var seedColor in seedColors)
        {
            foreach (var contrastLevel in contrastLevels)
            {
                foreach (var isDark in new[] { false, true })
                {
                    var schemes = new DynamicScheme[]
                    {
                        new SchemeContent(seedColor, isDark, contrastLevel),
                        new SchemeMonochrome(seedColor, isDark, contrastLevel),
                        new SchemeTonalSpot(seedColor, isDark, contrastLevel),
                        new SchemeFidelity(seedColor, isDark, contrastLevel),
                    };
                    foreach (var scheme in schemes)
                    {
                        Assert.True(PairSatisfiesContrast(scheme, Colors.OnPrimary(), Colors.Primary()));
                        Assert.True(PairSatisfiesContrast(scheme, Colors.OnPrimaryContainer(), Colors.PrimaryContainer()));
                        Assert.True(PairSatisfiesContrast(scheme, Colors.OnSecondary(), Colors.Secondary()));
                        Assert.True(PairSatisfiesContrast(scheme, Colors.OnSecondaryContainer(), Colors.SecondaryContainer()));
                        Assert.True(PairSatisfiesContrast(scheme, Colors.OnTertiary(), Colors.Tertiary()));
                        Assert.True(PairSatisfiesContrast(scheme, Colors.OnTertiaryContainer(), Colors.TertiaryContainer()));
                        Assert.True(PairSatisfiesContrast(scheme, Colors.OnError(), Colors.Error()));
                        Assert.True(PairSatisfiesContrast(scheme, Colors.OnErrorContainer(), Colors.ErrorContainer()));
                        Assert.True(PairSatisfiesContrast(scheme, Colors.OnBackground(), Colors.Background()));
                        Assert.True(PairSatisfiesContrast(scheme, Colors.OnSurfaceVariant(), Colors.SurfaceBright()));
                        Assert.True(PairSatisfiesContrast(scheme, Colors.OnSurfaceVariant(), Colors.SurfaceDim()));
                        Assert.True(PairSatisfiesContrast(scheme, Colors.InverseOnSurface(), Colors.InverseSurface()));
                    }
                }
            }
        }
    }

    [Fact]
    public void ValuesAreCorrect()
    {
        // Checks that the values of certain dynamic colors match Dart results.
        Assert.Equal(0xFFFFFFFFu, Colors.OnPrimaryContainer().GetArgb(new SchemeFidelity(Hct.FromInt(0xFFFF0000u), false, 0.5)));
        Assert.Equal(0xFFFFFFFFu, Colors.OnSecondaryContainer().GetArgb(new SchemeContent(Hct.FromInt(0xFF0000FFu), false, 0.5)));
        Assert.Equal(0xFF959b1au, Colors.OnTertiaryContainer().GetArgb(new SchemeContent(Hct.FromInt(0xFFFFFF00u), true, -0.5)));
        Assert.Equal(0xFF2F2F3Bu, Colors.InverseSurface().GetArgb(new SchemeContent(Hct.FromInt(0xFF0000FFu), false, 0.0)));
        Assert.Equal(0xffff422fu, Colors.InversePrimary().GetArgb(new SchemeContent(Hct.FromInt(0xFFFF0000u), false, -0.5)));
        Assert.Equal(0xFF484831u, Colors.OutlineVariant().GetArgb(new SchemeContent(Hct.FromInt(0xFFFFFF00u), true, 0.0)));
    }

    [Fact]
    public void FidelityValuesAreCorrect()
    {
        var fidelityColors = new MaterialDynamicColors(true);

        Assert.Equal(0xffffffffu, Colors.OnPrimaryContainer().GetArgb(new SchemeTonalSpot(Hct.FromInt(0xffff0000u), false, 0.5)));
        Assert.Equal(0xffffffffu, fidelityColors.OnPrimaryContainer().GetArgb(new SchemeTonalSpot(Hct.FromInt(0xffff0000u), false, 0.5)));

        Assert.Equal(0xffffffffu, Colors.OnSecondaryContainer().GetArgb(new SchemeVibrant(Hct.FromInt(0xff0000ffu), false, 0.5)));
        Assert.Equal(0xffffffffu, fidelityColors.OnSecondaryContainer().GetArgb(new SchemeVibrant(Hct.FromInt(0xff0000ffu), false, 0.5)));

        Assert.Equal(0xff000000u, Colors.OnTertiaryContainer().GetArgb(new SchemeExpressive(Hct.FromInt(0xffffff00u), true, 0.5)));
        Assert.Equal(0xff394e1du, fidelityColors.OnTertiaryContainer().GetArgb(new SchemeExpressive(Hct.FromInt(0xffffff00u), true, 0.5)));

        Assert.Equal(0xff303036u, Colors.InverseSurface().GetArgb(new SchemeTonalSpot(Hct.FromInt(0xff0000ffu), false, 0.0)));
        Assert.Equal(0xff303036u, fidelityColors.InverseSurface().GetArgb(new SchemeTonalSpot(Hct.FromInt(0xff0000ffu), false, 0.0)));

        Assert.Equal(0xffffb4a8u, Colors.InversePrimary().GetArgb(new SchemeVibrant(Hct.FromInt(0xffff0000u), false, 0.5)));
        Assert.Equal(0xffffb4a8u, fidelityColors.InversePrimary().GetArgb(new SchemeVibrant(Hct.FromInt(0xffff0000u), false, 0.5)));

        Assert.Equal(0xff444937u, Colors.OutlineVariant().GetArgb(new SchemeExpressive(Hct.FromInt(0xffffff00u), true, 0.0)));
        Assert.Equal(0xff444937u, fidelityColors.OutlineVariant().GetArgb(new SchemeExpressive(Hct.FromInt(0xffffff00u), true, 0.0)));

        Assert.Equal(0xff554050u, Colors.SecondaryContainer().GetArgb(new SchemeTonalSpot(Hct.FromInt(0xfffa2becu), true, 0.0)));
        Assert.Equal(0xff554050u, fidelityColors.SecondaryContainer().GetArgb(new SchemeTonalSpot(Hct.FromInt(0xfffa2becu), true, 0.0)));

        Assert.Equal(0xff603b4fu, Colors.SecondaryContainer().GetArgb(new SchemeVibrant(Hct.FromInt(0xfffa2becu), true, 0.0)));
        Assert.Equal(0xff603b4fu, fidelityColors.SecondaryContainer().GetArgb(new SchemeVibrant(Hct.FromInt(0xfffa2becu), true, 0.0)));

        Assert.Equal(0xff663b38u, Colors.SecondaryContainer().GetArgb(new SchemeExpressive(Hct.FromInt(0xfffa2becu), true, 0.0)));
        Assert.Equal(0xff693d3au, fidelityColors.SecondaryContainer().GetArgb(new SchemeExpressive(Hct.FromInt(0xfffa2becu), true, 0.0)));
    }

    private static bool PairSatisfiesContrast(DynamicScheme scheme, DynamicColor fg, DynamicColor bg)
    {
        double fgTone = fg.GetHct(scheme).Tone;
        double bgTone = bg.GetHct(scheme).Tone;
        double minimumRequirement = scheme.ContrastLevel >= 0.0 ? 4.5 : 3.0;
        return Contrast.RatioOfTones(fgTone, bgTone) >= minimumRequirement;
    }
}

/// <summary>
/// Ported from material 1.14.0's FixedDynamicColorTest.java (RemEx-4kv0g.10 review finding 2). Google's
/// version also asserts scheme.getPrimaryFixed() etc. -- the DynamicScheme convenience getters
/// (DynamicScheme.java:107-354) -- which the brief for this task deliberately excluded from the port
/// (Task 5 brief: "NOT lines 107-354, the convenience getters"); those assertions are ported here as the
/// equivalent MaterialDynamicColors role calls (dynamicColors.primaryFixed().GetArgb(scheme)), which is
/// exactly what the excluded getters forward to in the reference.
/// </summary>
public class FixedDynamicColorTests
{
    private static readonly MaterialDynamicColors Colors = new();

    [Fact]
    public void FixedColorsInTonalSpot()
    {
        DynamicScheme scheme = new SchemeTonalSpot(Hct.FromInt(0xFFFF0000u), true, 0.0);

        AssertToneWithin1(Colors.PrimaryFixed(), scheme, 90.0);
        AssertToneWithin1(Colors.PrimaryFixedDim(), scheme, 80.0);
        AssertToneWithin1(Colors.OnPrimaryFixed(), scheme, 10.0);
        AssertToneWithin1(Colors.OnPrimaryFixedVariant(), scheme, 30.0);
        AssertToneWithin1(Colors.SecondaryFixed(), scheme, 90.0);
        AssertToneWithin1(Colors.SecondaryFixedDim(), scheme, 80.0);
        AssertToneWithin1(Colors.OnSecondaryFixed(), scheme, 10.0);
        AssertToneWithin1(Colors.OnSecondaryFixedVariant(), scheme, 30.0);
        AssertToneWithin1(Colors.TertiaryFixed(), scheme, 90.0);
        AssertToneWithin1(Colors.TertiaryFixedDim(), scheme, 80.0);
        AssertToneWithin1(Colors.OnTertiaryFixed(), scheme, 10.0);
        AssertToneWithin1(Colors.OnTertiaryFixedVariant(), scheme, 30.0);
    }

    [Fact]
    public void FixedArgbColorsInTonalSpot()
    {
        DynamicScheme scheme = new SchemeTonalSpot(Hct.FromInt(0xFFFF0000u), true, 0.0);

        Assert.Equal(0xFFFFDAD4u, Colors.PrimaryFixed().GetArgb(scheme));
        Assert.Equal(0xFFFFB4A8u, Colors.PrimaryFixedDim().GetArgb(scheme));
        Assert.Equal(0xFF3A0905u, Colors.OnPrimaryFixed().GetArgb(scheme));
        Assert.Equal(0xFF73342Au, Colors.OnPrimaryFixedVariant().GetArgb(scheme));
        Assert.Equal(0xFFFFDAD4u, Colors.SecondaryFixed().GetArgb(scheme));
        Assert.Equal(0xFFE7BDB6u, Colors.SecondaryFixedDim().GetArgb(scheme));
        Assert.Equal(0xFF2C1512u, Colors.OnSecondaryFixed().GetArgb(scheme));
        Assert.Equal(0xFF5D3F3Bu, Colors.OnSecondaryFixedVariant().GetArgb(scheme));
        Assert.Equal(0xFFFBDFA6u, Colors.TertiaryFixed().GetArgb(scheme));
        Assert.Equal(0xFFDEC48Cu, Colors.TertiaryFixedDim().GetArgb(scheme));
        Assert.Equal(0xFF251A00u, Colors.OnTertiaryFixed().GetArgb(scheme));
        Assert.Equal(0xFF564419u, Colors.OnTertiaryFixedVariant().GetArgb(scheme));
    }

    [Fact]
    public void FixedColorsInLightMonochrome()
    {
        DynamicScheme scheme = new SchemeMonochrome(Hct.FromInt(0xFFFF0000u), false, 0.0);

        AssertToneWithin1(Colors.PrimaryFixed(), scheme, 40.0);
        AssertToneWithin1(Colors.PrimaryFixedDim(), scheme, 30.0);
        AssertToneWithin1(Colors.OnPrimaryFixed(), scheme, 100.0);
        AssertToneWithin1(Colors.OnPrimaryFixedVariant(), scheme, 90.0);
        AssertToneWithin1(Colors.SecondaryFixed(), scheme, 80.0);
        AssertToneWithin1(Colors.SecondaryFixedDim(), scheme, 70.0);
        AssertToneWithin1(Colors.OnSecondaryFixed(), scheme, 10.0);
        AssertToneWithin1(Colors.OnSecondaryFixedVariant(), scheme, 25.0);
        AssertToneWithin1(Colors.TertiaryFixed(), scheme, 40.0);
        AssertToneWithin1(Colors.TertiaryFixedDim(), scheme, 30.0);
        AssertToneWithin1(Colors.OnTertiaryFixed(), scheme, 100.0);
        AssertToneWithin1(Colors.OnTertiaryFixedVariant(), scheme, 90.0);
    }

    [Fact]
    public void FixedColorsInDarkMonochrome()
    {
        DynamicScheme scheme = new SchemeMonochrome(Hct.FromInt(0xFFFF0000u), true, 0.0);

        AssertToneWithin1(Colors.PrimaryFixed(), scheme, 40.0);
        AssertToneWithin1(Colors.PrimaryFixedDim(), scheme, 30.0);
        AssertToneWithin1(Colors.OnPrimaryFixed(), scheme, 100.0);
        AssertToneWithin1(Colors.OnPrimaryFixedVariant(), scheme, 90.0);
        AssertToneWithin1(Colors.SecondaryFixed(), scheme, 80.0);
        AssertToneWithin1(Colors.SecondaryFixedDim(), scheme, 70.0);
        AssertToneWithin1(Colors.OnSecondaryFixed(), scheme, 10.0);
        AssertToneWithin1(Colors.OnSecondaryFixedVariant(), scheme, 25.0);
        AssertToneWithin1(Colors.TertiaryFixed(), scheme, 40.0);
        AssertToneWithin1(Colors.TertiaryFixedDim(), scheme, 30.0);
        AssertToneWithin1(Colors.OnTertiaryFixed(), scheme, 100.0);
        AssertToneWithin1(Colors.OnTertiaryFixedVariant(), scheme, 90.0);
    }

    private static void AssertToneWithin1(DynamicColor color, DynamicScheme scheme, double expected)
    {
        double actual = color.GetHct(scheme).Tone;
        Assert.True(Math.Abs(actual - expected) <= 1.0, $"{color.Name}: expected tone within 1 of {expected}, got {actual}");
    }
}
