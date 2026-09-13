namespace Remex.Core.Models;

/// <summary>
/// Keeps per-card colour presets alive across layouts that do not carry them (RemEx-4kv0g.3, the
/// 2026-09-13 preset wipe).
/// </summary>
/// <remarks>
/// A <see cref="CardState.CardTheme"/> of <c>null</c> means "this source carries no colour
/// information" — the phone's copy of the layout has no card themes at all, and it pushes that copy
/// to the host with <c>LayoutUpdate</c>; the host then hands it back to the PC with
/// <c>LayoutSync</c>. Anywhere such a layout is written over one that does carry themes, the themes
/// must be carried forward or they are gone for good: the host store on a phone save, and the PC's
/// per-user file when a sync lands. A card is matched by <see cref="CardState.CardId"/> first and by
/// <see cref="CardState.SensorId"/> as the fallback (the phone can re-key a card). No reflection, no
/// JSON — safe for the NativeAOT core. <c>docs/REGRESSION-GUARDS.md</c> pins this.
/// </remarks>
public static class CardThemeMerge
{
    /// <summary>
    /// Returns <paramref name="incoming"/> with every card whose <c>CardTheme</c> is null taking the
    /// theme the same card carries in <paramref name="existing"/> (by <c>CardId</c>, then by
    /// <c>SensorId</c>). Cards that carry a theme, and cards with no counterpart, are returned as-is.
    /// </summary>
    public static List<CardState> PreserveThemes(IReadOnlyList<CardState>? incoming, IReadOnlyList<CardState>? existing)
    {
        if (incoming is null) return new List<CardState>();
        if (existing is null || existing.Count == 0) return new List<CardState>(incoming);

        var byId = new Dictionary<string, SensorCardTheme>(StringComparer.Ordinal);
        var bySensor = new Dictionary<string, SensorCardTheme>(StringComparer.Ordinal);
        foreach (var card in existing)
        {
            if (card.CardTheme is null) continue;
            if (card.CardId.Length > 0) byId.TryAdd(card.CardId, card.CardTheme);
            if (!string.IsNullOrEmpty(card.SensorId)) bySensor.TryAdd(card.SensorId, card.CardTheme);
        }

        var merged = new List<CardState>(incoming.Count);
        foreach (var card in incoming)
        {
            if (card.CardTheme is not null)
            {
                merged.Add(card);
                continue;
            }

            SensorCardTheme? kept = null;
            if (card.CardId.Length > 0) byId.TryGetValue(card.CardId, out kept);
            if (kept is null && !string.IsNullOrEmpty(card.SensorId)) bySensor.TryGetValue(card.SensorId, out kept);
            merged.Add(kept is null ? card : card with { CardTheme = kept });
        }

        return merged;
    }
}
