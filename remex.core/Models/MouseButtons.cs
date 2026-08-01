namespace Remex.Core.Models;

/// <summary>
/// The protocol's mouse-button indices, as they travel on the wire between the Android client and
/// the PC host.
/// </summary>
/// <remarks>
/// <para>
/// These are the indices carried by pointer messages and by <c>DesktopPointerSample</c>, NOT platform
/// button codes — every backend maps them onto its own numbering. The mapping tables live host-side
/// in <c>MouseButtonCodes</c>; this file is only the shared vocabulary the two ends agree on.
/// </para>
/// <para>
/// WHY THIS EXISTS AT ALL. Android sent index 1 for a plain left click, which is MIDDLE-click — a bug
/// that shipped because "1" is an unremarkable literal at every one of the dozen sites that write or
/// read it, on both sides of the connection, with nothing naming what it means (RemEx-kie3). The
/// Android side gained a <c>MouseButtons</c> object then; this is its counterpart, so the two ends
/// name the same thing rather than agreeing by coincidence.
/// </para>
/// <para>
/// NativeAOT-safe by construction: plain <c>const int</c>s, no reflection, no serialization.
/// </para>
/// </remarks>
public static class MouseButtons
{
    /// <summary>Primary button. The only value a plain tap or click should ever use.</summary>
    public const int Left = 0;

    /// <summary>Middle / wheel button.</summary>
    public const int Middle = 1;

    /// <summary>Secondary button — the context-menu one.</summary>
    public const int Right = 2;

    /// <summary>Back / side button.</summary>
    /// <remarks>See the note on <see cref="Extra"/>: nothing in the shipping client sends this.</remarks>
    public const int Side = 3;

    /// <summary>Forward / extra button.</summary>
    /// <remarks>
    /// NO SHIPPING PATH SENDS 3 OR 4, and it is worth being exact about why, because the obvious
    /// guess is wrong. <c>DesktopPointerSample</c> carries a button MASK rather than an index — the
    /// router reads <c>ButtonMask &amp; 0x02</c> / <c>&amp; 0x04</c> as stylus barrel booleans — so
    /// the pointer path never produces a button index at all, and the click UI only ever offers
    /// left, middle and right. These two exist so a host that does receive a stray index maps it to
    /// the button it names instead of silently performing a left click.
    /// </remarks>
    public const int Extra = 4;
}
