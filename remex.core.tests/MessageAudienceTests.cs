using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Remex.Core.Messages;

namespace Remex.Core.Tests;

/// <summary>
/// Every host → client type reaches the client it is FOR, not merely some client (RemEx-y94aa).
/// </summary>
/// <remarks>
/// <para>
/// **THIS IS THE HALF <c>MessageTypeDeliveryTests</c> CANNOT SEE, AND THAT GAP WAS MEASURED.** That
/// test asks whether anything on any client receives a type, which catches the RemEx-y6x6 shape — a
/// type nobody receives at all. It cannot ask whether the RIGHT surface receives it, because "the
/// client" is four separate dispatchers. <c>launcher_sync</c> is received by both the Android control
/// client and the PC's own UI: delete the Android case and that test stays green while the Android
/// launcher screen silently stops updating. The mutation was run and did not go red. This one does.
/// </para>
/// <para>
/// **THE COMPLETENESS ASSERTION IS THE LOAD-BEARING ONE.** Correctness alone — "everything declared
/// is really received" — would be satisfiable by declaring nothing, which is exactly how an exemption
/// list rots. Requiring that every type the host sends appears in <see cref="MessageAudience"/> means
/// the table cannot quietly stop covering something; the only way to add a host → client type is to
/// say who it is for.
/// </para>
/// <para>
/// Source-scanned for the same reason its sibling is: delivery crosses a JNI boundary and three
/// transports that no managed test can drive together. It proves the right receiver EXISTS, not that
/// a message survives the trip.
/// </para>
/// </remarks>
public class MessageAudienceTests
{
    /// <summary>Where each surface dispatches what it receives.</summary>
    /// <remarks>
    /// **TWO OF THE FOUR HAVE MORE THAN ONE DOOR, AND MODELLING THEM AS ONE FILE PUBLISHED A FALSE
    /// STATEMENT.** The sibling test takes the union across every client, so an unmodelled dispatcher
    /// costs it nothing. This test makes a positive claim per surface, so the same four-file model
    /// that was fine there asserted here that the PC's own remote-desktop viewer does not exist. It
    /// does — <c>RemoteDesktopService</c> is a second desktop-stream client, and
    /// <c>FileTransferClient</c> a second PC-UI one.
    ///
    /// **WHAT LISTING THEM BUYS, STATED HONESTLY: LESS THAN IT LOOKS.** A surface's receive set is the
    /// union over its files, so gutting one case in the second file is still covered by the first.
    /// What it does buy is the per-file anti-vacuity below — a dispatcher that is emptied or renamed
    /// goes red — and a model that no longer says something untrue about the shipped app.
    /// </remarks>
    private static readonly (ClientSurface Surface, string[] Paths, string Role)[] Dispatchers =
    [
        (ClientSurface.AndroidControl, ["remex.core/Native/RemexNativeClient.cs"], "the phone's control socket"),
        (ClientSurface.Pairing, ["remex.core/Native/PairingClient.cs"], "the pairing handshake client"),
        (ClientSurface.DesktopStream,
            ["remex.core/Native/RemexDesktopClient.cs", "remex.desktop/Services/Network/RemoteDesktopService.cs"],
            "the desktop-stream clients - the phone's, and the PC's own viewer"),
        (ClientSurface.PcUi,
            ["remex.desktop/ViewModels/ConnectionViewModel.cs", "remex.desktop/Services/FileTransfer/FileTransferClient.cs"],
            "the PC's own UI, over loopback"),
    ];

    /// <remarks>
    /// <para>
    /// <c>Type = MessageTypes.X</c> is deliberately absent, exactly as in the sibling test: that is a
    /// message being BUILT, and counting it would let a type be excused by the code that emits it.
    /// </para>
    /// <para>
    /// **<c>!=</c> IS A RECEIVE POSITION AND LEAVING IT OUT COST A SECURITY PATH.** The first version
    /// of this pattern matched <c>==</c> only. <c>RemexDesktopClient</c> guards the reconnect
    /// challenge with <c>msg?.Type != MessageTypes.ReconnectChallenge</c> — the one client dispatcher
    /// in the repo that tests the negative — so the seeding scan could not see it and its audience was
    /// written down as Android-only. The table then certified that the desktop stream is not an
    /// audience for the message its proof-of-possession depends on, which is worse than having no
    /// table: delete that branch and every non-loopback desktop connect is closed by the host before
    /// capture starts, and this guard would have said the arrangement was correct.
    /// </para>
    /// </remarks>
    private const string ReceivePattern =
        @"(?:case MessageTypes\.(\w+)|[=!]=\s*MessageTypes\.(\w+)|MessageTypes\.(\w+)\s*=>|is MessageTypes\.(\w+))";

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, ".."));

    private static string Read(string relativePath)
    {
        var full = Path.Combine([RepoRoot(), .. relativePath.Split('/')]);
        Assert.True(File.Exists(full), $"{relativePath} moved or was renamed");
        return File.ReadAllText(full);
    }

    private static IEnumerable<string> Matches(string source, string pattern) =>
        Regex.Matches(source, pattern).Select(m => m.Groups.Values.Skip(1).First(g => g.Success).Value);

    /// <summary>Constant name → wire value, for every type there is.</summary>
    private static Dictionary<string, string> AllTypes()
    {
        var source = Read("remex.core/Messages/RemexMessage.cs");
        var matched = Regex.Matches(source, """public const string (\w+)\s*=\s*"([^"]+)"\s*;""")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value, StringComparer.Ordinal);

        Assert.Equal(Regex.Matches(source, @"public const string \w+\s*=").Count, matched.Count);
        return matched;
    }

    /// <summary>Wire values the host sends to a client.</summary>
    /// <remarks>
    /// **ONE SEND SHAPE IS RECOGNISED, AND THE COMPLETENESS ASSERTION RESTS ENTIRELY ON IT.** Every
    /// host → client send today is <c>Type = MessageTypes.X</c>; a helper such as
    /// <c>SendAsync(MessageTypes.Foo, …)</c> would make a new type invisible here, and an invisible
    /// type is one the table is never required to declare — which is exactly the exemption the
    /// completeness test exists to forbid. Inherited from the sibling test rather than introduced
    /// here, and worth knowing before adding such a helper.
    /// </remarks>
    private static HashSet<string> HostSends(Dictionary<string, string> all) =>
        Directory
            .EnumerateFiles(Path.Combine(RepoRoot(), "remex.agent"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(f => Matches(File.ReadAllText(f), @"Type\s*=\s*MessageTypes\.(\w+)"))
            .Where(all.ContainsKey)
            .Select(name => all[name])
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>The body of the JNI router, which is a second way onto the Android surface.</summary>
    private static string RouterBody()
    {
        var source = Read("remex.core/Native/AndroidNativeExports.cs");
        var start = source.IndexOf("private static void OnNativeMessageReceived", StringComparison.Ordinal);
        Assert.True(start >= 0, "OnNativeMessageReceived was renamed - the router has moved");

        var end = source.IndexOf("\n    private static ", start + 1, StringComparison.Ordinal);
        Assert.True(end > start, "could not find the end of OnNativeMessageReceived - has its shape changed?");
        return source[start..end];
    }

    /// <summary>Which wire values each surface actually receives, as the code stands.</summary>
    private static Dictionary<ClientSurface, HashSet<string>> ActualReceivers(Dictionary<string, string> all)
    {
        var actual = new Dictionary<ClientSurface, HashSet<string>>();
        foreach (var (surface, paths, role) in Dispatchers)
        {
            actual[surface] = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in paths)
            {
                var names = Matches(Read(path), ReceivePattern).ToList();

                // PER-FILE ANTI-VACUITY, NOT PER-SURFACE. A dispatcher that was gutted or whose shape
                // changed contributes nothing, and every type it covered would then look mis-declared
                // rather than un-received - which sends the reader to the wrong file. Checked per file
                // because a surface with two doors would otherwise let one be emptied unnoticed.
                Assert.True(names.Count > 0, $"{path} ({role}) dispatches no message type at all");
                foreach (var wire in names.Where(all.ContainsKey).Select(n => all[n])) actual[surface].Add(wire);
            }
        }

        // THE ANDROID SURFACE HAS A SECOND DOOR. Types reach the phone either by a named case in
        // RemexNativeClient or through the JNI router - by name, or by a wire-value PREFIX that
        // forwards a whole family. The file_ and clipboard_ families arrive only that way, so a check
        // that knew about named cases alone would call every one of them undelivered.
        var router = RouterBody();
        foreach (var name in Matches(router, @"MessageTypes\.(\w+)").Where(all.ContainsKey))
        {
            actual[ClientSurface.AndroidControl].Add(all[name]);
        }

        // **WHAT A PREFIX CLAIM IS AND IS NOT WORTH.** Twenty of the entries have their Android
        // audience satisfied by a prefix rather than by a named case, so those rows are falsifiable at
        // FAMILY granularity - remove the file_ prefix and eighteen go red at once - but not per type.
        // That is honest rather than a hole: the router forwards the whole family deliberately and
        // says not to narrow it back into a type list, so there is no per-type C# fact left to assert.
        // The residual is Kotlin-side, where a collector dropping a case is invisible to any scanner
        // on this side of the JNI boundary.
        var prefixes = Matches(router, @"StartsWith\(""([a-z_]+)""").ToList();
        Assert.NotEmpty(prefixes);
        foreach (var wire in all.Values.Where(w => prefixes.Any(p => w.StartsWith(p, StringComparison.Ordinal))))
        {
            actual[ClientSurface.AndroidControl].Add(wire);
        }

        return actual;
    }

    [Fact]
    public void EveryTypeTheHostSendsDeclaresItsAudience()
    {
        // **WITHOUT THIS, THE TABLE IS AN EXEMPTION LIST.** A type left out would be checked against
        // nothing and pass, which is precisely the failure mode AGENTS.md warns about. There is no
        // way to add a host -> client type without saying who it is for.
        var all = AllTypes();
        var undeclared = HostSends(all)
            .Where(w => !MessageAudience.HostToClient.ContainsKey(w))
            .OrderBy(w => w, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            undeclared.Count == 0,
            "the host sends these types and MessageAudience does not say who they are for:\n  " +
            string.Join("\n  ", undeclared) +
            "\n\nAdd an entry naming the surface that consumes it. Do not skip it: an undeclared type " +
            "is checked against nothing, and this guard is only worth having while that is impossible.");
    }

    [Fact]
    public void EveryDeclaredAudienceReallyReceivesIt()
    {
        // THE ASSERTION THE SIBLING TEST CANNOT MAKE. Deleting launcher_sync's Android case leaves a
        // PC-UI receiver standing, so "somebody receives it" still holds; "the phone receives it"
        // does not, and that is the claim this checks.
        var all = AllTypes();
        var actual = ActualReceivers(all);

        var broken = new List<string>();
        foreach (var (wire, audience) in MessageAudience.HostToClient.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            foreach (var (surface, _, role) in Dispatchers)
            {
                if (audience.HasFlag(surface) && !actual[surface].Contains(wire))
                {
                    broken.Add($"{wire} is declared for {surface} ({role}) and nothing there receives it");
                }
            }
        }

        Assert.True(
            broken.Count == 0,
            "these types are declared for a surface that does not receive them:\n  " +
            string.Join("\n  ", broken) +
            "\n\nEither the dispatch was removed - in which case the messages are being dropped in " +
            "silence on that surface - or the audience changed and MessageAudience was not told. " +
            "Both are worth stopping for; neither is fixed by editing the table to match.");
    }

    [Fact]
    public void EverySurfaceTheEnumOffersIsActuallyScanned()
    {
        // **THIS IS THE ASSERTION THAT WOULD HAVE CAUGHT THE OTHERS AT AUTHORING TIME, AND IT IS THE
        // ONE WORTH KEEPING.** The correctness loop walks Dispatchers, not the enum - so a surface
        // that exists as a flag but has no file listed is declared, satisfied by nothing, and checked
        // by nothing. That is the same vacuity ClientSurface.None has, wearing a value that looks
        // deliberate. It is not hypothetical: the phone's binary file channel is a real client this
        // enum cannot yet name, and adding it as a flag without a scanner would silently create an
        // unverifiable half to every entry that used it.
        var offered = Enum.GetValues<ClientSurface>().Where(v => v != ClientSurface.None).ToHashSet();
        var scanned = Dispatchers.Select(d => d.Surface).ToHashSet();

        Assert.True(offered.SetEquals(scanned),
            "these surfaces can be declared but nothing scans them: " +
            string.Join(", ", offered.Except(scanned)) +
            "\nand these are scanned but cannot be declared: " + string.Join(", ", scanned.Except(offered)) +
            "\n\nA surface with no dispatcher listed makes every entry that names it unfalsifiable.");
    }

    [Fact]
    public void NoTypeIsDeclaredForNobody()
    {
        // ClientSurface.None would satisfy the loop above by having nothing to check, so it is the
        // one value that turns an entry into the exemption the completeness test exists to forbid.
        var orphans = MessageAudience.HostToClient
            .Where(e => e.Value == ClientSurface.None)
            .Select(e => e.Key)
            .OrderBy(w => w, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            orphans.Count == 0,
            "these types are declared as reaching no client surface at all:\n  " + string.Join("\n  ", orphans) +
            "\n\nA host -> client type with no audience is a message being sent into the void. If it " +
            "genuinely has no reader, stop sending it.");
    }

    [Fact]
    public void TheTableDescribesThisProtocolAndNotAForgottenOne()
    {
        // ANTI-VACUITY, AND IT GUARDS TWO DIFFERENT SILENCES. An empty table would satisfy both the
        // correctness loop and the None check; a table full of wire values no type has any more would
        // satisfy them just as well while describing nothing.
        var all = AllTypes();
        var wireValues = all.Values.ToHashSet(StringComparer.Ordinal);

        Assert.True(MessageAudience.HostToClient.Count > 20,
            $"only {MessageAudience.HostToClient.Count} declared - has the table been emptied?");

        var unknown = MessageAudience.HostToClient.Keys
            .Where(w => !wireValues.Contains(w))
            .OrderBy(w => w, StringComparer.Ordinal)
            .ToList();

        Assert.True(unknown.Count == 0,
            "these declared wire values match no MessageTypes constant:\n  " + string.Join("\n  ", unknown) +
            "\n\nA renamed type leaves its old entry behind, and the entry then guards nothing.");
    }

    [Fact]
    public void ThePhoneAndThePcUiBothStillReceiveWhatTheyShare()
    {
        // **THE WORKED EXAMPLE FROM THE BEAD, PINNED BY NAME.** These five are the types both surfaces
        // consume, and they are the entire reason the union check was not enough. Stated separately
        // from the table-driven test above so the specific regression that motivated all of this
        // fails with a message that names it, rather than as one line in a list.
        var all = AllTypes();
        var actual = ActualReceivers(all);

        // DERIVED FROM THE TABLE, NOT HARDCODED. A sixth dual-audience type must join this check by
        // existing, not by somebody remembering to add it here - the whole point of the five is that
        // they are the set the union guard cannot see, and a stale copy of that set guards the past.
        var shared = MessageAudience.HostToClient
            .Where(e => e.Value == (ClientSurface.AndroidControl | ClientSurface.PcUi))
            .Select(e => e.Key)
            .OrderBy(w => w, StringComparer.Ordinal)
            .ToList();

        Assert.True(shared.Count >= 5, $"only {shared.Count} types are shared between the phone and the PC UI - has the table shrunk?");

        foreach (var wire in shared)
        {
            Assert.True(actual[ClientSurface.AndroidControl].Contains(wire),
                $"the phone stopped receiving {wire} - the PC's own UI still does, so the delivery guard " +
                "will not notice, and the screen that shows it silently stops updating");
            Assert.True(actual[ClientSurface.PcUi].Contains(wire),
                $"the PC's own UI stopped receiving {wire}");
        }
    }
}
