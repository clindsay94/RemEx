using Remex.Core.Theming.Mcu;

namespace Remex.Core.Theming;

/// <summary>
/// Black-or-white ink for a chip painted in an arbitrary fill colour (spec 2026-09-13
/// personalize-tabs, §3, RemEx-9ql7v). <c>Remex.Desktop.Controls.PaletteChip</c> uses this to
/// label a palette chip whose primary swatch can be anything a seed or a saved palette produces.
/// </summary>
/// <remarks>
/// Tone, not WCAG relative luminance: this repo's palette engine (<c>DynamicColorGenerator</c> /
/// the MCU port) already decides on-colour roles by HCT tone — Dark mode's accent roles sit at
/// tone 80, Light mode's at tone 40 — and <see cref="Hct.Tone"/> is exactly the same L* the engine
/// used to make that call. Picking WCAG luminance instead would give ChipInk its own, independent
/// opinion of what's "light enough for black text", which can disagree with the palette engine at
/// the margins and read as a chip whose ink and whose fill were decided by two different rules. Tone
/// keeps this function in agreement with the surfaces it labels: a Dark tone-80 primary reads as
/// light (ink → black) exactly because the engine already treats tone 80 as light-on-dark, and a
/// Light tone-40 primary reads as dark (ink → white) for the matching reason.
/// </remarks>
public static class ChipInk
{
    /// <summary>
    /// Black (<c>0xFF000000</c>) for a fill whose HCT tone is 50 or above, white
    /// (<c>0xFFFFFFFF</c>) below. The boundary is inclusive of black so a fill that lands exactly on
    /// tone 50 — the midpoint, where either reads as roughly equal contrast — resolves the same way
    /// every time rather than by which side of a floating-point comparison it happens to fall on.
    /// </summary>
    /// <param name="argb">The fill colour, as a 32-bit ARGB value.</param>
    /// <returns><c>0xFF000000</c> (black) or <c>0xFFFFFFFF</c> (white).</returns>
    public static uint For(uint argb)
        => Hct.FromInt(argb).Tone >= 50 ? 0xFF000000u : 0xFFFFFFFFu;
}
