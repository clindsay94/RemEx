using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Remex.Desktop.Services;

namespace Remex.Desktop.ViewModels;

/// <summary>
/// One row of the Paired Devices card (RemEx-kirdm).
/// </summary>
/// <remarks>
/// A projection of <see cref="PairedDeviceRow"/> with the display decisions already made, so the
/// axaml binds plain strings and a bool rather than formatting anything. The decisions themselves
/// are in <see cref="PairedDeviceRowText"/>, which is pure and tested.
/// </remarks>
public sealed partial class PairedDeviceItem : ObservableObject
{
    /// <summary>The opaque pairing id, kept for the rename and unpair work (RemEx-4gbp2).</summary>
    public required string ClientId { get; init; }

    /// <summary>What to call this device. Never blank.</summary>
    [ObservableProperty]
    private string _displayName = string.Empty;

    /// <summary>When it first paired, already formatted, or the "unknown" marker.</summary>
    [ObservableProperty]
    private string _firstPairedText = string.Empty;

    /// <summary>When it was last connected, already formatted, or the "unknown" marker.</summary>
    [ObservableProperty]
    private string _lastSeenText = string.Empty;

    /// <summary>Whether THIS device is connected right now.</summary>
    [ObservableProperty]
    private bool _isOnline;

    /// <summary>
    /// What the user has typed into the rename field, before it is applied.
    /// </summary>
    /// <remarks>
    /// SEPARATE FROM <see cref="DisplayName"/>, which is what the row SHOWS. Binding the field
    /// straight to DisplayName would rewrite the visible label on every keystroke and, worse, would
    /// leave a half-typed name on screen if the user wandered off without applying — a device
    /// labelled "Conn" beside a button that unpairs it (RemEx-5lb90) is exactly the row you do not
    /// want to be confident about.
    /// <para>
    /// It is seeded EMPTY rather than with the current name, so the field reads as "type a new name"
    /// rather than as an edit of something already stored — and so clearing it is a deliberate act
    /// (blank clears the override) rather than the accidental result of selecting all and typing.
    /// </para>
    /// </remarks>
    [ObservableProperty]
    private string _pendingName = string.Empty;

    /// <summary>
    /// The accessible name for this row's dot: two states, naming what the control is.
    /// </summary>
    /// <remarks>
    /// WITHOUT THIS THE DOT IS COLOUR ALONE (review). It was the only status dot in the codebase with
    /// no accessible name — the six others all carry one — so a screen-reader user, or anyone who
    /// cannot separate red from green, could read the device name and both dates and still not learn
    /// which phone was connected. On a card that grows an unpair button next (RemEx-4gbp2), that is
    /// the wrong fact to leave encoded in a colour.
    /// </remarks>
    [ObservableProperty]
    private string _statusAccessibleName = string.Empty;
}

/// <summary>
/// The display decisions for a paired-device row: what to call it, and how to say when.
/// </summary>
/// <remarks>
/// <para>
/// PURE, SO THE DECISIONS ARE TESTABLE WITHOUT A RESOURCE SYSTEM OR A CLOCK — the same split
/// RemEx-ivkq settled on for Android and RemEx-0z7w reused for phone presence. The caller supplies
/// the localized "unknown" marker and the culture; nothing here reads
/// <c>LocalizationService.Instance</c>.
/// </para>
/// <para>
/// THE NAME NEVER COMES BACK BLANK, and that rule is not cosmetic: this row carries an unpair button
/// (RemEx-4gbp2), and a nameless row beside one is a decision the user cannot make safely.
/// <see cref="PairedDeviceDisplayName"/> already owns that rule and is reused rather than
/// re-derived — it prefers a friendly name and falls back to the opaque id.
/// </para>
/// </remarks>
public static class PairedDeviceRowText
{
    /// <summary>
    /// Formats an absolute moment for a row, or returns <paramref name="unknownMarker"/>.
    /// </summary>
    /// <remarks>
    /// LOCAL TIME, NOT UTC. The stores keep UTC because that is the only thing worth persisting, but
    /// "last seen 03:14" is meaningless to a person unless it is their own clock. A short date and
    /// time, because a row is one line.
    /// <para>
    /// A DATE FROM THE FUTURE IS STILL SHOWN. The file is editable and a clock can be wrong, but the
    /// alternative — hiding it as implausible — would leave the row saying "unknown" for a device
    /// that plainly has a record, and there is nothing a person can do with that either. Showing the
    /// odd value is at least something they can recognise as odd.
    /// </para>
    /// </remarks>
    public static string Describe(DateTimeOffset? moment, string unknownMarker, CultureInfo culture)
        => moment is null
            ? unknownMarker
            : moment.Value.ToLocalTime().ToString("g", culture);
}
