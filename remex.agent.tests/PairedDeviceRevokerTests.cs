using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Remex.Agent.Services;
using Remex.Agent.Services.FileTransfer;
using Remex.Agent.Services.Security;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Ending a pairing clears the credential AND every record of the device (RemEx-5lb90).
/// </summary>
/// <remarks>
/// <para>
/// This is the first production caller of <see cref="PairedClientRegistry.UnregisterClient"/>, on the
/// class <c>docs/REGRESSION-GUARDS.md</c> names as the only authentication path in production.
/// </para>
/// <para>
/// FIVE STORES, AND MISSING ONE IS INVISIBLE UNTIL THE DEVICE PAIRS AGAIN. That delay is what makes
/// it worth a test per store rather than one that checks the happy path: a leftover row does nothing
/// at all until a new pairing picks it up, at which point the device appears wearing the identity of
/// a relationship the user deliberately ended. The file-trust row is the one that arrives carrying
/// privilege rather than a name, and the first version of this suite missed it.
/// </para>
/// </remarks>
public sealed class PairedDeviceRevokerTests : IDisposable
{
    private const string Phone = "phone-a";

    // MATCHED WITH ITS QUOTES when asserting against the raw trust file: a bare "phone-a" is a
    // substring of "phone-a2", so a second fixture client would mask a failed teardown of the first
    // (review).
    private const string QuotedPhone = "\"phone-a\"";
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory();

    private sealed record Fixture(
        PairedDeviceRevoker Revoker,
        PairedClientRegistry Registry,
        PairedClientNameStore Names,
        PairedDeviceNameOverrideStore Overrides,
        PairedDeviceActivityStore Activity,
        FileTrustService Trust);

    private Fixture NewRevoker(string? registryPath = null)
    {
        var registry = new PairedClientRegistry(
            NullLogger<PairedClientRegistry>.Instance,
            registryPath ?? Path.Combine(_root.FullName, "paired.json"));
        var names = new PairedClientNameStore(
            NullLogger<PairedClientNameStore>.Instance, Path.Combine(_root.FullName, "names.json"));
        var overrides = new PairedDeviceNameOverrideStore(
            NullLogger<PairedDeviceNameOverrideStore>.Instance, Path.Combine(_root.FullName, "overrides.json"));
        var activity = new PairedDeviceActivityStore(
            NullLogger<PairedDeviceActivityStore>.Instance, Path.Combine(_root.FullName, "activity.json"));
        var trust = new FileTrustService(
            NullLogger<FileTrustService>.Instance, registry, new ClientSessionRegistry(),
            Path.Combine(_root.FullName, "trust.json"), TimeSpan.FromSeconds(1));

        var revoker = new PairedDeviceRevoker(
            registry, names, overrides, activity, trust, NullLogger<PairedDeviceRevoker>.Instance);

        registry.RegisterClient(Phone, [1, 2, 3, 4]);
        names.Remember(Phone, "Pixel 9");
        overrides.Set(Phone, "Study Phone");
        activity.RecordPaired(Phone, DateTimeOffset.UtcNow);
        trust.SetFullBrowseGrantedAsync(Phone, true, CancellationToken.None).GetAwaiter().GetResult();

        return new Fixture(revoker, registry, names, overrides, activity, trust);
    }

    public void Dispose()
    {
        _root.Delete(recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task TheCredentialIsGone()
    {
        var f = NewRevoker();

        await f.Revoker.RevokeAsync(Phone, CancellationToken.None);

        Assert.False(f.Registry.IsClientPaired(Phone));
        Assert.False(f.Registry.TryGetReconnectSecret(Phone, out _));
    }

    [Fact]
    public async Task TheNameTheDeviceReportedIsGone()
    {
        var f = NewRevoker();

        await f.Revoker.RevokeAsync(Phone, CancellationToken.None);

        Assert.Null(f.Names.Resolve(Phone));
    }

    [Fact]
    public async Task TheNameTheUserChoseIsGone()
    {
        // The override outlives a re-pair by design (RemEx-4gbp2), which is exactly why it must NOT
        // outlive a revocation: a device that pairs again would silently arrive already wearing the
        // name from the pairing the user ended.
        var f = NewRevoker();

        await f.Revoker.RevokeAsync(Phone, CancellationToken.None);

        Assert.False(f.Overrides.Snapshot().ContainsKey(Phone));
    }

    [Fact]
    public async Task TheDatesAreGone()
    {
        // A surviving first-paired date is the most misleading of the name-ish four: a freshly
        // re-paired device would claim a relationship going back months.
        var f = NewRevoker();

        await f.Revoker.RevokeAsync(Phone, CancellationToken.None);

        Assert.Null(f.Activity.Resolve(Phone));
    }

    [Fact]
    public async Task TheFileAccessGrantIsGone()
    {
        // THE ONE THAT CARRIES PRIVILEGE, not a label. The trust store prunes itself only for clients
        // that are NOT paired, so a grant that survives a revocation stops being prunable the instant
        // the device pairs again — see the re-pair test below for what that costs.
        //
        // ASSERTED AGAINST THE FILE, NOT GetTrustAsync, AND THAT DISTINCTION IS THE WHOLE TEST. The
        // first version of this asked the service and passed with the teardown deleted: reading trust
        // for an unpaired client prunes it on the way through, so the API answered with the prune's
        // null rather than the revoker's. Only what is on disk between the unpair and the next read
        // distinguishes the two — and that gap is exactly the window the re-pair closes.
        var f = NewRevoker();
        var trustFile = Path.Combine(_root.FullName, "trust.json");
        Assert.Contains(QuotedPhone, File.ReadAllText(trustFile));

        await f.Revoker.RevokeAsync(Phone, CancellationToken.None);

        Assert.DoesNotContain(QuotedPhone, File.ReadAllText(trustFile));
    }

    [Fact]
    public async Task ARevokedDeviceDoesNotComeBackWhenItPairsAgain()
    {
        // THE PROPERTY ALL FIVE ADD UP TO, asserted end to end rather than left implied. This is what
        // the user actually cares about: unpair, pair again, and it is a new device. The trust
        // assertion is the load-bearing one — with the row left behind, re-pairing hands the phone
        // full-device browse with no consent prompt, inherited from a pairing the user ended.
        var f = NewRevoker();
        await f.Revoker.RevokeAsync(Phone, CancellationToken.None);

        f.Registry.RegisterClient(Phone, [9, 9, 9, 9]);

        Assert.True(f.Registry.IsClientPaired(Phone));
        Assert.Null(f.Names.Resolve(Phone));
        Assert.False(f.Overrides.Snapshot().ContainsKey(Phone));
        Assert.Null(f.Activity.Resolve(Phone));
        Assert.False(await f.Trust.IsFullBrowseGrantedAsync(Phone, CancellationToken.None));
    }

    [Fact]
    public async Task RevokingAnUnknownDeviceIsHarmless()
    {
        // A double-click, or a revoke racing a refresh, must not throw or disturb another device.
        var f = NewRevoker();

        await f.Revoker.RevokeAsync("never-paired", CancellationToken.None);

        Assert.True(f.Registry.IsClientPaired(Phone), "revoking one device must not touch another");
    }

    [Fact]
    public async Task ABlankClientIdRevokesNothing()
    {
        var f = NewRevoker();

        await f.Revoker.RevokeAsync("   ", CancellationToken.None);

        Assert.True(f.Registry.IsClientPaired(Phone));
    }

    [Fact]
    public async Task AFailedCredentialWriteStillClearsEveryRecordAndSaysSo()
    {
        // THE ORDERING CLAIM, EXERCISED RATHER THAN ASSERTED IN A COMMENT (review). The registry is
        // the FIRST teardown and the only one that propagates an IO failure — the record stores all
        // swallow theirs — so a short-circuit here would have left the device unpaired in memory with
        // every record intact, and nothing cleared at all after a restart.
        //
        // The failure is real, not mocked: the registry's persist path calls Directory.CreateDirectory
        // on its parent, and a FILE sitting where that directory belongs makes it throw.
        //
        // WHAT THIS DELIBERATELY DOES NOT HOLD (review): the surviving-credential half. The registry
        // removes from memory and then persists, so a real persist failure leaves the pairing gone
        // here and still on disk, and the device returns on the next start. This injection takes the
        // store file with the directory, so the disk state is ABSENT rather than STALE — not a state
        // production can reach, so asserting against it would prove nothing. RemEx-pynli carries both
        // that assertion and the error message that should go with it; an injection that fails the
        // WRITE has a parity trap, because an exclusive handle blocks the atomic replace on Windows
        // and a rename over an open file succeeds on Linux.
        var blocked = Path.Combine(_root.FullName, "blocked");
        Directory.CreateDirectory(blocked);
        var f = NewRevoker(Path.Combine(blocked, "paired.json"));

        Directory.Delete(blocked, recursive: true);
        File.WriteAllText(blocked, "not a directory");

        await Assert.ThrowsAsync<AggregateException>(
            () => f.Revoker.RevokeAsync(Phone, CancellationToken.None));

        Assert.False(f.Registry.IsClientPaired(Phone));
        Assert.Null(f.Names.Resolve(Phone));
        Assert.False(f.Overrides.Snapshot().ContainsKey(Phone));
        Assert.Null(f.Activity.Resolve(Phone));
        // The file again, for the reason TheFileAccessGrantIsGone gives: the service's own lazy prune
        // would answer this for an unpaired client whether or not the revoker did anything.
        Assert.DoesNotContain(QuotedPhone, File.ReadAllText(Path.Combine(_root.FullName, "trust.json")));
    }

    [Fact]
    public void TheRenamerAndTheListCannotReachTheRegistryCredentials()
    {
        // The three seams are deliberately narrow, and only this one may end a pairing. Asserted
        // against the types so the separation cannot be quietly undone by widening a constructor.
        //
        // NARROWED TO WHAT IS TRUE (review): the read-only directory DOES take the registry, because
        // listing paired devices means reading the pairing store. What must not spread is the ability
        // to REVOKE, so the claim is about the renamer — the other mutating seam — and about the
        // revoker being the only holder of the revocation method.
        static string[] Parameters(Type t) =>
            [.. t.GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.ParameterType.Name)];

        Assert.Contains("PairedClientRegistry", Parameters(typeof(PairedDeviceRevoker)));
        Assert.DoesNotContain("PairedClientRegistry", Parameters(typeof(PairedDeviceRenamer)));

        var revokers = typeof(PairedDeviceRevoker).Assembly.GetTypes()
            .Where(t => typeof(Remex.Desktop.Services.IPairedDeviceRevoker).IsAssignableFrom(t) && !t.IsInterface)
            .ToArray();
        Assert.Equal([typeof(PairedDeviceRevoker)], revokers);
    }
}
