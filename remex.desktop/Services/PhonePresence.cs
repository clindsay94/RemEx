using System.Globalization;
using System.Net;

namespace Remex.Desktop.Services;

/// <summary>One live client session on the host's <c>/ws</c> endpoint.</summary>
/// <param name="RemoteAddress">The peer's address as the host sees it.</param>
/// <param name="DeviceName">The paired device's name, or null/blank if it has not identified.</param>
public readonly record struct ClientSession(string? RemoteAddress, string? DeviceName);

/// <summary>What the shell should say about phones being attached.</summary>
public enum PhonePresenceState
{
    /// <summary>No phone is attached. The loopback link may still be up, and does not count.</summary>
    NoPhone,

    /// <summary>Exactly one phone is attached.</summary>
    OnePhone,

    /// <summary>More than one phone is attached.</summary>
    SeveralPhones
}

/// <summary>The presence picture the shell renders.</summary>
/// <param name="State">Whether any phone is attached.</param>
/// <param name="PhoneCount">How many, excluding loopback.</param>
/// <param name="FirstDeviceName">
/// The name of a connected phone when exactly one is attached and it has identified itself;
/// otherwise null.
/// </param>
public readonly record struct PhonePresenceStatus(
    PhonePresenceState State,
    int PhoneCount,
    string? FirstDeviceName);

/// <summary>
/// Separates "a phone is attached" from "the loopback link is up" (RemEx-porg).
/// </summary>
/// <remarks>
/// <para>
/// **ONE BOOLEAN WAS DOING TWO JOBS, AND THAT IS THE WHOLE BUG.** Every status dot on the PC binds
/// <c>Connection.IsConnected</c>, which is the UI's own WebSocket to its embedded host — so a user
/// with ZERO phones paired sees a green "Connected", and the one fact the PC UI exists to convey is
/// displayed nowhere. The two states are not merely different, they are almost uncorrelated: the
/// loopback link is up essentially always.
/// </para>
/// <para>
/// The rule is a single predicate — a session counts as a phone only if it is NOT loopback — and it
/// lives here rather than in a view model so it can be stated once and tested, instead of being
/// re-derived at each of the three binding sites that currently get it wrong.
/// </para>
/// </remarks>
public static class PhonePresence
{
    /// <summary>
    /// Reduces the live session list to what the shell should display.
    /// </summary>
    public static PhonePresenceStatus Evaluate(IEnumerable<ClientSession>? sessions)
    {
        if (sessions is null) return new PhonePresenceStatus(PhonePresenceState.NoPhone, 0, null);

        var phones = new List<ClientSession>();
        foreach (var session in sessions)
        {
            if (IsPhone(session)) phones.Add(session);
        }

        var state = phones.Count switch
        {
            0 => PhonePresenceState.NoPhone,
            1 => PhonePresenceState.OnePhone,
            _ => PhonePresenceState.SeveralPhones
        };

        // The name is offered only for the single-phone case. With several attached, naming one of
        // them is arbitrary and reads as though it is the only one.
        var name = phones.Count == 1 && !string.IsNullOrWhiteSpace(phones[0].DeviceName)
            ? phones[0].DeviceName
            : null;

        return new PhonePresenceStatus(state, phones.Count, name);
    }

    /// <summary>
    /// Whether a session is a phone rather than the UI's own loopback connection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// CLAUDE.md fixes the architecture as always non-loopback Android-to-PC, so a loopback peer is
    /// by definition NOT a phone — it is the desktop UI talking to its own embedded host.
    /// </para>
    /// <para>
    /// AN UNPARSEABLE OR MISSING ADDRESS IS NOT A PHONE. That is the fail-closed direction and it is
    /// the one that matters: counting an unknown session as a phone would reproduce the original
    /// bug — a confident "1 phone connected" with nothing attached — whereas failing the other way
    /// merely under-reports, which the user can see is wrong because their phone is in their hand.
    /// </para>
    /// </remarks>
    internal static bool IsPhone(ClientSession session)
    {
        if (string.IsNullOrWhiteSpace(session.RemoteAddress)) return false;

        var address = session.RemoteAddress.Trim();

        // Strip an IPv6 bracket form and any port, so "[::1]:5005" and "127.0.0.1:5005" are both
        // recognised - a peer that arrives with its port attached must not slip through as a phone.
        if (address.StartsWith('['))
        {
            var close = address.IndexOf(']');
            if (close > 0) address = address[1..close];
        }
        else
        {
            var colon = address.LastIndexOf(':');
            // Only strip a trailing :port from an IPv4-or-hostname form. A bare IPv6 literal has
            // several colons and no brackets, and chopping at the last one would corrupt it.
            if (colon > 0 && address.IndexOf(':') == colon) address = address[..colon];
        }

        return IPAddress.TryParse(address, out var parsed) && !IPAddress.IsLoopback(parsed);
    }

    /// <summary>
    /// The localization key the shell should show for <paramref name="status"/>, and the argument to
    /// format it with, or null when the string takes none.
    /// </summary>
    /// <remarks>
    /// KEPT SEPARATE FROM THE LOOKUP so the choice can be tested without a resource system, which is
    /// the same split RemEx-ivkq settled on for the Android side: the decision is pure, only the
    /// lookup is not. The caller does <c>LocalizationService.Instance[key]</c> and, when an argument
    /// comes back, formats it in.
    /// <para>
    /// THE ONE-PHONE CASE SPLITS ON WHETHER THE DEVICE NAMED ITSELF. A phone reaches the registry
    /// authenticated but not necessarily named — a client id rides on <c>ping</c> and a device name
    /// need never arrive — so "Galaxy S26 connected" and "1 phone connected" are both real states and
    /// the second is not an error. Formatting a null name into the first would render "  connected"
    /// with a hole in it, on the row whose entire job is to say what is attached.
    /// </para>
    /// </remarks>
    public static (string Key, string? Argument) Describe(PhonePresenceStatus status) => status.State switch
    {
        PhonePresenceState.OnePhone when !string.IsNullOrWhiteSpace(status.FirstDeviceName)
            => ("Shell_PhoneConnectedNamed", status.FirstDeviceName),
        PhonePresenceState.OnePhone => ("Shell_PhoneConnectedUnnamed", null),
        PhonePresenceState.SeveralPhones
            => ("Shell_PhonesConnectedSeveral", status.PhoneCount.ToString(CultureInfo.CurrentCulture)),
        _ => ("Shell_NoPhoneConnected", null),
    };
}
