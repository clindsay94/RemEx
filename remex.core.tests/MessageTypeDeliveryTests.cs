using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Remex.Core.Tests;

/// <summary>
/// Everything the host SENDS has something on the client that receives it (RemEx-dd8dp).
/// </summary>
/// <remarks>
/// <para>
/// **A HOST → CLIENT TYPE NOBODY RECEIVES IS DROPPED IN SILENCE.** The send succeeds and the phone
/// never hears. That is how RemEx-y6x6 bricked all of v3 file transfer, presenting as "peer did not
/// respond" while the host looked perfectly healthy, and it is why RemEx-hgqs deliberately shipped
/// the clipboard push with no host → client message rather than risk it. Nothing fails, nothing
/// logs, and no other test in this repo can see it.
/// </para>
/// <para>
/// **THE DIRECTION IS THE WHOLE POINT, AND TWO EARLIER VERSIONS OF THIS TEST MISSED IT.** The first
/// counted a type as safe if the HOST had a <c>case</c> for it. The second asked only whether
/// SOMETHING received it. Both absolve <c>launcher_sync</c>, which the host receives from the phone
/// AND sends to it: delete its client-side receiver and the Android launcher silently stops
/// updating, while a test asking "is it received anywhere?" happily finds the host's own case. For a
/// bidirectional type, "somebody receives this" and "the peer receives this" are different claims,
/// and only the second one is about delivery.
/// </para>
/// <para>
/// So the rule is directional: for every type the host builds a message with, some client-side
/// dispatcher must match on it. Client → host is not checked here — the host's own switch has a
/// <c>default:</c> that logs, so an unhandled request is visible rather than silent, which is the
/// opposite of the failure this guards.
/// </para>
/// <para>
/// **WHAT IT STILL CANNOT SEE, MEASURED RATHER THAN GUESSED.** "The client" is four things, and this
/// cannot tell which one a type is bound for. <c>launcher_sync</c> is received by BOTH the Android
/// control client and the PC's own UI — delete the Android one and this test still passes, because a
/// client receiver remains. I mutated exactly that to check, and it did not go red. Closing it needs
/// per-type audience metadata the code does not carry today, so the honest statement is: this
/// catches a host → client type received by NOTHING, which is the RemEx-y6x6 shape, and does not
/// catch one that lost only its Android receiver. Filed separately rather than left implied.
/// </para>
/// <para>
/// **NO EXEMPTION LIST, ON PURPOSE.** A version with one needed 25 entries; modelling the receivers
/// properly brings it to zero. AGENTS.md warns that a list which can absorb a false positive will
/// absorb a real one, and the way to honour that is no list rather than a better one.
/// </para>
/// <para>
/// Source-scanned because delivery crosses a JNI boundary and three transports no managed test can
/// drive together. The limit is real and worth stating: this proves a receiver EXISTS, not that a
/// message survives the trip. The trip is proved from a device.
/// </para>
/// </remarks>
public class MessageTypeDeliveryTests
{
    /// <summary>Where a message the host sent is dispatched on the client side.</summary>
    /// <remarks>
    /// Four, because the client has four: the control socket, the pairing handshake, the
    /// remote-desktop stream, and the PC's own UI talking to its embedded host over loopback. Which
    /// one owns a type is what tells you where to look when it does not arrive.
    /// </remarks>
    private static readonly (string Path, string Role)[] ClientReceivers =
    [
        ("remex.core/Native/RemexNativeClient.cs", "the client's control-socket dispatcher"),
        ("remex.core/Native/PairingClient.cs", "the pairing handshake client"),
        ("remex.core/Native/RemexDesktopClient.cs", "the client's desktop-stream dispatcher"),
        ("remex.desktop/ViewModels/ConnectionViewModel.cs", "the PC's own UI, over loopback"),
    ];

    /// <summary>Shapes that mean "this code RECEIVES the type".</summary>
    /// <remarks>
    /// <c>Type = MessageTypes.X</c> is deliberately absent: that is a message being BUILT to send,
    /// and counting it would let a type be excused by the very code that emits it into the void.
    /// </remarks>
    private const string ReceivePattern =
        @"(?:case MessageTypes\.(\w+)|==\s*MessageTypes\.(\w+)|MessageTypes\.(\w+)\s*=>|is MessageTypes\.(\w+))";

    /// <summary>The shape that means "this code SENDS the type".</summary>
    private const string SendPattern = @"Type\s*=\s*MessageTypes\.(\w+)";

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, ".."));

    private static string Read(string relativePath)
    {
        var full = Path.Combine([RepoRoot(), .. relativePath.Split('/')]);
        Assert.True(File.Exists(full), $"{relativePath} moved or was renamed");
        return File.ReadAllText(full);
    }

    private static Dictionary<string, string> AllTypes()
    {
        var source = Read("remex.core/Messages/RemexMessage.cs");
        var matched = Regex.Matches(source, """public const string (\w+)\s*=\s*"([^"]+)"\s*;""")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value, StringComparer.Ordinal);

        // FAILS CLOSED ON AN UNEXPECTED WIRE VALUE. An earlier version matched only [a-z0-9_]+, so a
        // type spelled "file_transferV2" would have sat outside the check entirely while NotEmpty
        // stayed satisfied by the other seventy-five.
        Assert.Equal(Regex.Matches(source, @"public const string \w+\s*=").Count, matched.Count);
        return matched;
    }

    private static string RouterBody()
    {
        var source = Read("remex.core/Native/AndroidNativeExports.cs");
        var start = source.IndexOf("private static void OnNativeMessageReceived", StringComparison.Ordinal);
        Assert.True(start >= 0, "OnNativeMessageReceived was renamed - the router has moved");

        // ASSERTED, NOT DEFAULTED TO EOF. Swallowing the rest of the file would count types it merely
        // SENDS as routed, widening what passes exactly when the scan has stopped understanding it.
        var end = source.IndexOf("\n    private static ", start + 1, StringComparison.Ordinal);
        Assert.True(end > start, "could not find the end of OnNativeMessageReceived - has its shape changed?");
        return source[start..end];
    }

    private static IEnumerable<string> Matches(string source, string pattern) =>
        Regex.Matches(source, pattern).Select(m => m.Groups.Values.Skip(1).First(g => g.Success).Value);

    [Fact]
    public void EveryTypeTheHostSendsHasAClientSideReceiver()
    {
        var all = AllTypes();

        var hostSends = Directory
            .EnumerateFiles(Path.Combine(RepoRoot(), "remex.agent"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(f => Matches(File.ReadAllText(f), SendPattern))
            .Where(all.ContainsKey)
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(hostSends.Count > 10, $"only {hostSends.Count} host sends found - has the send shape changed?");

        var clientReceives = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, role) in ClientReceivers)
        {
            var found = Matches(Read(path), ReceivePattern).ToList();

            // PER-RECEIVER ANTI-VACUITY. A receiver that has been gutted, renamed, or whose dispatch
            // shape has changed contributes nothing, and the types it covered would silently need
            // covering by something else. A total-only check would not notice.
            Assert.True(found.Count > 0, $"{path} ({role}) dispatches no message type at all");
            foreach (var name in found) clientReceives.TryAdd(name, role);
        }

        var router = RouterBody();
        foreach (var name in Matches(router, @"MessageTypes\.(\w+)")) clientReceives.TryAdd(name, "the JNI router, by name");

        var prefixes = Matches(router, @"StartsWith\(""([a-z_]+)""").ToList();
        Assert.NotEmpty(prefixes);

        var undelivered = hostSends
            .Where(t => !clientReceives.ContainsKey(t))
            .Where(t => !prefixes.Any(p => all[t].StartsWith(p, StringComparison.Ordinal)))
            .OrderBy(t => t, StringComparer.Ordinal)
            .Select(t => $"{t} (\"{all[t]}\")")
            .ToList();

        Assert.True(
            undelivered.Count == 0,
            "the host sends these types and NOTHING on the client receives them:\n  " +
            string.Join("\n  ", undelivered) +
            "\n\nThey are being dropped in silence - the send succeeds and the peer never hears " +
            "(RemEx-y6x6). Add the client-side dispatch, or the routing that forwards them. Do not " +
            "add an exemption; this test has none on purpose.");
    }

    [Fact]
    public void TheClipboardFamilyIsDeliveredByThePrefixForward()
    {
        // The types this bead was filed from. clipboard_content and clipboard_push_result are
        // host -> client and reach the phone ONLY through the prefix forward - no named case
        // anywhere covers them - so pinning the prefix pins their delivery.
        var prefixes = Matches(RouterBody(), @"StartsWith\(""([a-z_]+)""").ToList();

        Assert.Contains("clipboard_", prefixes);
        Assert.Contains("file_", prefixes);
    }
}
