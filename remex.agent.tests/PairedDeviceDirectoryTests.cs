using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Remex.Agent.Services.Security;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// The read-only paired-device list the desktop shows (RemEx-nrsv).
/// </summary>
/// <remarks>
/// <para>
/// THE SAFETY PROPERTY IS THE POINT OF THESE, not the join. <c>PairedClientRegistry</c> is named in
/// <c>docs/REGRESSION-GUARDS.md</c> as the ONLY authentication path in production, where a break
/// silently bricks every device pairing. Listing devices is a read, and it has to stay one: the
/// registry record must remain byte-identical, no secret may reach the row type, and the list must
/// be driven BY the registry rather than by the cosmetic stores beside it.
/// </para>
/// <para>
/// Timestamps live in their own file for the same reason. Two more fields on the registry's map
/// would have changed the shape of the one file that must not change shape, to carry values nothing
/// authenticates with.
/// </para>
/// </remarks>
public sealed class PairedDeviceDirectoryTests
{
    private static (PairedDeviceDirectory Directory, PairedClientRegistry Registry,
                    PairedClientNameStore Names, PairedDeviceActivityStore Activity) NewDirectory(string root)
    {
        var registry = new PairedClientRegistry(
            NullLogger<PairedClientRegistry>.Instance, Path.Combine(root, "paired.json"));
        var names = new PairedClientNameStore(
            NullLogger<PairedClientNameStore>.Instance, Path.Combine(root, "names.json"));
        var activity = new PairedDeviceActivityStore(
            NullLogger<PairedDeviceActivityStore>.Instance, Path.Combine(root, "activity.json"));

        // A real ClientSessionRegistry with nothing registered: every device reads offline, which is
        // the honest answer for a test that starts no connections. The per-device online state has
        // its own coverage in the desktop card tests (RemEx-kirdm).
        var sessions = new Remex.Agent.Services.ClientSessionRegistry();
        var overrides = new PairedDeviceNameOverrideStore(
            NullLogger<PairedDeviceNameOverrideStore>.Instance, Path.Combine(root, "overrides.json"));

        return (new PairedDeviceDirectory(registry, names, activity, sessions, overrides),
                registry, names, activity);
    }

    [Fact]
    public void ADeviceWithNoNameAndNoDatesStillLists()
    {
        // THE FAIL-VISIBLE DIRECTION. Every device paired before the activity store existed has no
        // row in it, and a device need never send a name at all. Dropping those from the list would
        // hide real pairings — including, on the screen this feeds, the unpair button for them.
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var (directory, registry, _, _) = NewDirectory(root.FullName);
            registry.RegisterClient("phone-a", [1, 2, 3, 4]);

            var row = Assert.Single(directory.PairedDevices());

            Assert.Equal("phone-a", row.ClientId);
            Assert.Null(row.DeviceName);
            Assert.Null(row.FirstPairedUtc);
            Assert.Null(row.LastSeenUtc);
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public void ANameAndDatesAreJoinedOntoTheRow()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var (directory, registry, names, activity) = NewDirectory(root.FullName);
            registry.RegisterClient("phone-a", [1, 2, 3, 4]);
            names.Remember("phone-a", "Connor's Pixel");
            activity.RecordPaired("phone-a", new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
            activity.RecordSeen("phone-a", new DateTimeOffset(2026, 8, 9, 10, 11, 12, TimeSpan.Zero));

            var row = Assert.Single(directory.PairedDevices());

            Assert.Equal("Connor's Pixel", row.DeviceName);
            Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), row.FirstPairedUtc);
            Assert.Equal(new DateTimeOffset(2026, 8, 9, 10, 11, 12, TimeSpan.Zero), row.LastSeenUtc);
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public void ANameOrDateLeftBehindAfterAnUnpairDoesNotResurrectTheDevice()
    {
        // THE DIRECTION THAT MATTERS. The registry is the spine; the cosmetic stores are joined onto
        // it and never drive it. If a leftover row could put an unpaired device back on the list, the
        // user would be offered an unpair button for a pairing that no longer exists — and would
        // reasonably conclude the button does not work.
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var (directory, registry, names, activity) = NewDirectory(root.FullName);
            registry.RegisterClient("phone-a", [1, 2, 3, 4]);
            names.Remember("phone-a", "Connor's Pixel");
            activity.RecordPaired("phone-a", DateTimeOffset.UtcNow);

            registry.UnregisterClient("phone-a");

            Assert.Empty(directory.PairedDevices());
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public void TheListIsStablyOrdered()
    {
        // A list that reshuffles between reads is a list whose unpair button moves under the pointer.
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var (directory, registry, _, _) = NewDirectory(root.FullName);
            foreach (var id in new[] { "zeta", "alpha", "mike" }) registry.RegisterClient(id, [9]);

            var first = directory.PairedDevices().Select(r => r.ClientId).ToArray();
            var second = directory.PairedDevices().Select(r => r.ClientId).ToArray();

            Assert.Equal(new[] { "alpha", "mike", "zeta" }, first);
            Assert.Equal(first, second);
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public void NoSecretCanReachTheRowType()
    {
        // IsOnline joined the row for the Paired Devices card (RemEx-kirdm) — a bool derived from the
        // session registry, carrying nothing from the secret map. Updating this list is the point: it
        // forces anyone widening the row to say so here.
        //
        // A SHAPE LOCK, not the compile-time guarantee an earlier version of this comment claimed
        // (review). It is a runtime reflection check over the row's PROPERTIES, so it is blind to
        // fields — what it does do is fail the moment anyone widens the row, which is the realistic
        // way a reconnect secret ends up in a view model, a log line or a diagnostics export.
        var properties = typeof(Remex.Desktop.Services.PairedDeviceRow)
            .GetProperties()
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[] { "ClientId", "DeviceName", "FirstPairedUtc", "IsOnline", "LastSeenUtc", "NameOverride" },
            properties);
    }

    [Fact]
    public void TheRegistryNeverHandsOutTheMapItKeepsSecretsIn()
    {
        // REFLECTED, NOT TEXT-MATCHED (review). The first version was a regex over the source, and it
        // caught only the exact overload I had injected to test it — `IReadOnlyDictionary<string,
        // string> Pairings()` sitting right beside it would have walked straight past, and a pure
        // reformat of the signature would have failed it. The values in that map ARE reconnect
        // secrets, so what needs pinning is the type's public shape, not how it is spelled.
        var offenders = typeof(PairedClientRegistry)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => LeaksThePairs(m.ReturnType) || m.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase))
            .Select(m => $"{m.ReturnType.Name} {m.Name}")
            .Where(signature => signature != "Boolean TryGetReconnectSecret")
            .ToArray();

        Assert.Empty(offenders);
    }

    /// <summary>Whether a return type could carry the id→secret pairs out of the registry.</summary>
    private static bool LeaksThePairs(Type returnType)
    {
        if (typeof(System.Collections.IDictionary).IsAssignableFrom(returnType)) return true;
        if (!returnType.IsGenericType) return false;

        var definition = returnType.GetGenericTypeDefinition();
        if (definition == typeof(IReadOnlyDictionary<,>) || definition == typeof(IDictionary<,>)) return true;

        // IEnumerable<KeyValuePair<,>> covers the LINQ-shaped leak that is not a dictionary type.
        return returnType.GetInterfaces().Append(returnType).Any(i =>
            i.IsGenericType
            && i.GetGenericTypeDefinition() == typeof(IEnumerable<>)
            && i.GetGenericArguments()[0].IsGenericType
            && i.GetGenericArguments()[0].GetGenericTypeDefinition() == typeof(KeyValuePair<,>));
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, ".."));
}
