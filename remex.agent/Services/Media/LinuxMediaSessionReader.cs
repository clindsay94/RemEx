using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Remex.Core.Models;
using Tmds.DBus.Protocol;

namespace Remex.Agent.Services.Media;

/// <summary>
/// Reads the MPRIS media players on the session bus (RemEx-xx6xf).
/// </summary>
/// <remarks>
/// <para>
/// LINUX HAS NO "CURRENT SESSION", WHICH IS THE REAL DIFFERENCE FROM WINDOWS AND IS WORTH STATING
/// PLAINLY. Windows hands back the one session its own transport controls act on, so the reported
/// state and the key press provably concern the same player. MPRIS is a list of equals: every player
/// exports its own bus name and nobody arbitrates. Which one a media key actually reaches is decided
/// by the desktop environment, using rules this process cannot see. So the player chosen here is a
/// PREFERENCE — playing beats paused beats anything — and with two players open it can disagree with
/// whichever one the key lands on. That is a smaller wrong than reporting nothing, and it is the same
/// answer a user would give if asked "what is your PC playing".
/// </para>
/// <para>
/// EVERY READ RE-ENUMERATES THE BUS rather than caching player names. Players appear and vanish as
/// they are opened and closed, a cached name outlives its owner, and a request to a departed name is
/// an error that would read as "nothing is playing" — which is a wrong answer rather than a missing
/// one.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed class LinuxMediaSessionReader(ILogger<LinuxMediaSessionReader> logger) : IMediaSessionReader, IAsyncDisposable
{
    private const string PlayerInterface = "org.mpris.MediaPlayer2.Player";
    private const string PlayerPath = "/org/mpris/MediaPlayer2";
    private const string MprisPrefix = "org.mpris.MediaPlayer2.";

    private DBusConnection? _conn;

    /// <summary>
    /// Whether a session bus exists to ask at all.
    /// </summary>
    /// <remarks>
    /// READ FROM THE ENVIRONMENT RATHER THAN FROM A PROBE, because this is consulted when
    /// <c>HostCapabilities</c> is built and must not block a connecting client on a D-Bus round trip.
    /// It answers "could this host ever report" — a headless service account with no session bus
    /// never can — not "is a player running right now", which is what <see cref="ReadAsync"/> is for.
    /// </remarks>
    public bool IsSupported => !string.IsNullOrEmpty(DBusAddress.Session);

    public async Task<MediaPlaybackState> ReadAsync(CancellationToken ct)
    {
        try
        {
            var conn = await EnsureConnectionAsync(ct);
            if (conn is null)
            {
                return new MediaPlaybackState { Status = MediaPlaybackStatus.Unknown };
            }

            var players = await ListPlayersAsync(conn, ct);
            if (players.Count == 0)
            {
                return new MediaPlaybackState { Status = MediaPlaybackStatus.None };
            }

            MediaPlaybackState? best = null;

            foreach (var player in players)
            {
                var state = await ReadPlayerAsync(conn, player, ct);
                if (state is null)
                {
                    continue;
                }

                // FIRST PLAYING WINS OUTRIGHT — there is nothing a later player could say that would
                // be a better answer to "what is this PC playing".
                if (state.Status == MediaPlaybackStatus.Playing)
                {
                    return state;
                }

                best ??= state;

                // A paused player beats a stopped or unreadable one, but keep looking for a playing
                // one. Ordering the preference here rather than sorting the list keeps this a single
                // pass over a bus that can change under it.
                if (state.Status == MediaPlaybackStatus.Paused && best.Status != MediaPlaybackStatus.Paused)
                {
                    best = state;
                }
            }

            return best ?? new MediaPlaybackState { Status = MediaPlaybackStatus.None };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The contract is "never throws". D-Bus surfaces a departed peer, a refused connection and
            // a malformed reply as different exception types, and this call reaches whatever media
            // player the user happens to have open, so the honest guard is the broad one.
            logger.LogDebug(ex, "Could not read MPRIS media state.");
            return new MediaPlaybackState { Status = MediaPlaybackStatus.Unknown };
        }
    }

    /// <summary>Every well-known name on the session bus that belongs to an MPRIS player.</summary>
    private static async Task<List<string>> ListPlayersAsync(DBusConnection conn, CancellationToken ct)
    {
        MessageBuffer buf;
        {
            var writer = conn.GetMessageWriter();
            writer.WriteMethodCallHeader(
                destination: "org.freedesktop.DBus",
                path: "/org/freedesktop/DBus",
                @interface: "org.freedesktop.DBus",
                member: "ListNames");
            buf = writer.CreateMessage();
        }

        var names = await conn.CallMethodAsync(
            buf,
            static (Message msg, object? state) => msg.GetBodyReader().ReadArrayOfString());

        ct.ThrowIfCancellationRequested();

        return [.. names.Where(n => n.StartsWith(MprisPrefix, StringComparison.Ordinal))];
    }

    /// <summary>
    /// One player's reading, or null when it will not answer.
    /// </summary>
    /// <remarks>
    /// NULL RATHER THAN AN UNKNOWN STATE, so that one uncooperative player cannot outrank a healthy
    /// one in the preference above. A player that has just quit is the common case here, and its
    /// silence should not become the PC's answer.
    /// </remarks>
    private async Task<MediaPlaybackState?> ReadPlayerAsync(DBusConnection conn, string busName, CancellationToken ct)
    {
        try
        {
            MessageBuffer buf;
            {
                var writer = conn.GetMessageWriter();
                writer.WriteMethodCallHeader(
                    destination: busName,
                    path: PlayerPath,
                    @interface: "org.freedesktop.DBus.Properties",
                    member: "GetAll",
                    signature: "s");
                writer.WriteString(PlayerInterface);
                buf = writer.CreateMessage();
            }

            var properties = await conn.CallMethodAsync(
                buf,
                static (Message msg, object? state) => msg.GetBodyReader().ReadDictionaryOfStringToVariantValue());

            ct.ThrowIfCancellationRequested();

            if (!properties.TryGetValue("PlaybackStatus", out var rawStatus))
            {
                return null;
            }

            var status = TryGetString(rawStatus) switch
            {
                "Playing" => MediaPlaybackStatus.Playing,
                "Paused" => MediaPlaybackStatus.Paused,
                "Stopped" => MediaPlaybackStatus.Stopped,
                _ => MediaPlaybackStatus.Unknown,
            };

            var (title, artist) = ReadMetadata(properties);

            return new MediaPlaybackState
            {
                Status = status,
                Title = title,
                Artist = artist,
                SourceApp = busName[MprisPrefix.Length..],
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogTrace(ex, "MPRIS player {BusName} did not answer.", busName);
            return null;
        }
    }

    /// <summary>Title and artist out of the <c>Metadata</c> dictionary, if it has them.</summary>
    /// <remarks>
    /// <c>xesam:artist</c> IS AN ARRAY, unlike <c>xesam:title</c>, and joining rather than taking the
    /// first is the difference between "Simon &amp; Garfunkel" and half of it.
    /// </remarks>
    private (string? Title, string? Artist) ReadMetadata(Dictionary<string, VariantValue> properties)
    {
        if (!properties.TryGetValue("Metadata", out var rawMetadata))
        {
            return (null, null);
        }

        try
        {
            var metadata = rawMetadata.GetDictionary<string, VariantValue>();

            var title = metadata.TryGetValue("xesam:title", out var rawTitle) ? TryGetString(rawTitle) : null;

            string? artist = null;
            if (metadata.TryGetValue("xesam:artist", out var rawArtist))
            {
                var names = rawArtist.GetArray<string>().Where(n => !string.IsNullOrWhiteSpace(n)).ToArray();
                artist = names.Length > 0 ? string.Join(", ", names) : null;
            }

            return (Blank(title), Blank(artist));
        }
        catch (Exception ex)
        {
            // Metadata is free-form: players ship keys with the wrong type, and a track's title is not
            // worth an exception escaping into the sampler loop.
            logger.LogTrace(ex, "MPRIS metadata was not in the expected shape.");
            return (null, null);
        }
    }

    private static string? TryGetString(VariantValue value)
    {
        try
        {
            return value.GetString();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// The session-bus connection, opened once and kept.
    /// </summary>
    /// <remarks>
    /// KEPT RATHER THAN OPENED PER READ, because this runs once a second for the life of the process
    /// and a D-Bus connect is a handshake, not a socket open. Null means the bus is unreachable, which
    /// <see cref="ReadAsync"/> reports as Unknown rather than as "nothing playing".
    /// </remarks>
    private async Task<DBusConnection?> EnsureConnectionAsync(CancellationToken ct)
    {
        if (_conn is not null)
        {
            return _conn;
        }

        var address = DBusAddress.Session;
        if (string.IsNullOrEmpty(address))
        {
            return null;
        }

        try
        {
            var conn = new DBusConnection(address);
            await conn.ConnectAsync();
            ct.ThrowIfCancellationRequested();
            _conn = conn;
            return _conn;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not connect to the D-Bus session bus for MPRIS.");
            return null;
        }
    }

    public ValueTask DisposeAsync()
    {
        _conn?.Dispose();
        _conn = null;
        return ValueTask.CompletedTask;
    }
}
