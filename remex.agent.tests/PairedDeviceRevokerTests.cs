using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Remex.Agent.Services;
using Remex.Agent.Services.FileTransfer;
using Remex.Agent.Services.Security;
using Remex.Core.Services.FileTransfer;
using Remex.Core.Models;
using Remex.Desktop.Services;
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
        FileTrustService Trust,
        RecordingDisconnector Disconnector);

    /// <summary>Records who was cut, so the revoker's own tests need no sockets.</summary>
    private sealed class RecordingDisconnector : IPairedDeviceDisconnector
    {
        public List<string> Disconnected { get; } = [];

        public Task DisconnectAsync(string clientId)
        {
            Disconnected.Add(clientId);
            return Task.CompletedTask;
        }
    }

    private Fixture NewRevoker(string? registryPath = null, IFileTrustService? trustOverride = null)
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

        var disconnector = new RecordingDisconnector();
        var revoker = new PairedDeviceRevoker(
            registry, names, overrides, activity, trustOverride ?? trust, disconnector,
            NullLogger<PairedDeviceRevoker>.Instance);

        registry.RegisterClient(Phone, [1, 2, 3, 4]);
        names.Remember(Phone, "Pixel 9");
        overrides.Set(Phone, "Study Phone");
        activity.RecordPaired(Phone, DateTimeOffset.UtcNow);
        trust.SetFullBrowseGrantedAsync(Phone, true, CancellationToken.None).GetAwaiter().GetResult();

        return new Fixture(revoker, registry, names, overrides, activity, trust, disconnector);
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
    public async Task RevokingAlsoCutsTheDeviceOffRIGHTNOW()
    {
        // THE JOIN to RemEx-6nkht. Clearing the credential only decides the NEXT connection —
        // IsClientPaired is consulted when one is established and nowhere afterwards — so without
        // this call a phone already mirroring the desktop carried on mirroring it after being
        // unpaired. What the three channels do about it is PairedDeviceDisconnectorTests' subject;
        // what this holds is that the revocation asks at all.
        var f = NewRevoker();

        await f.Revoker.RevokeAsync(Phone, CancellationToken.None);

        Assert.Equal([Phone], f.Disconnector.Disconnected);
    }

    [Fact]
    public async Task AFailedTeardownStillCutsTheDeviceOff()
    {
        // The disconnect sits OUTSIDE the failure list on purpose, and this is the reason: a
        // revocation that could not finish clearing its records is exactly the one where leaving the
        // phone connected would be worst.
        var blocked = Path.Combine(_root.FullName, "blocked-disconnect");
        Directory.CreateDirectory(blocked);
        var f = NewRevoker(Path.Combine(blocked, "paired.json"));

        Directory.Delete(blocked, recursive: true);
        File.WriteAllText(blocked, "not a directory");

        await Assert.ThrowsAsync<PairedDeviceRevocationException>(
            () => f.Revoker.RevokeAsync(Phone, CancellationToken.None));

        Assert.Equal([Phone], f.Disconnector.Disconnected);
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
        // production can reach, so asserting against it would prove nothing. That assertion now lives
        // in AFailedCredentialWriteLEAVESThePairingOnDiskAndSaysSo, which pays the price of a
        // per-OS injection to reach the stale state; this one stays as it is because collect-and-
        // continue is a different property and this is the cheapest way to hold it.
        var blocked = Path.Combine(_root.FullName, "blocked");
        Directory.CreateDirectory(blocked);
        var f = NewRevoker(Path.Combine(blocked, "paired.json"));

        Directory.Delete(blocked, recursive: true);
        File.WriteAllText(blocked, "not a directory");

        await Assert.ThrowsAsync<PairedDeviceRevocationException>(
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
    public async Task AFailedCredentialWriteLEAVESThePairingOnDiskAndSaysSo()
    {
        // THE DIVERGENCE, held at last (RemEx-pynli). UnregisterClient removes from the in-memory
        // dictionary and THEN persists, so a failed write leaves the device unpaired for this run and
        // untouched on disk — it is paired again the next time the host loads the store. The previous
        // failure test could not hold this: its injection took the store FILE away with the directory,
        // so the disk state was absent rather than stale, which production cannot reach.
        //
        // TWO INJECTIONS BECAUSE ONE WILL NOT DO IT ON BOTH (the parity trap the bead named). On
        // Windows a read-only destination makes the atomic replace fail. On Unix it would not: rename
        // consults the DIRECTORY's write permission, not the target's, so the same setup would
        // silently succeed and this test would prove nothing. There the write permission comes off the
        // containing directory instead, which fails the staging write a step earlier. Both leave the
        // existing file byte-for-byte intact, which is the whole point.
        var credentials = Directory.CreateDirectory(Path.Combine(_root.FullName, "credentials"));
        var storePath = Path.Combine(credentials.FullName, "paired.json");
        var f = NewRevoker(storePath);
        var before = File.ReadAllText(storePath);
        Assert.Contains(QuotedPhone, before);

        MakeWritesFail(credentials, storePath);
        try
        {
            AssertTheInjectionActuallyTook(credentials, storePath);

            var failure = await Assert.ThrowsAsync<PairedDeviceRevocationException>(
                () => f.Revoker.RevokeAsync(Phone, CancellationToken.None));

            Assert.True(failure.PairingMayReturn,
                "a credential write that failed is the one failure the user can act on");
            Assert.False(f.Registry.IsClientPaired(Phone), "in memory the device is unpaired");
            Assert.Equal(before, File.ReadAllText(storePath));
        }
        finally
        {
            AllowWritesAgain(credentials, storePath);
        }

        // THE RESTART, which is what the user actually experiences: a fresh registry over the same
        // file finds the device paired again.
        var afterRestart = new PairedClientRegistry(
            NullLogger<PairedClientRegistry>.Instance, storePath);
        Assert.True(afterRestart.IsClientPaired(Phone),
            "the pairing survived on disk, which is exactly what the message must warn about");
    }

    [Fact]
    public async Task AFailureTHATSPAREDTheCredentialDoesNotClaimThePairingIsComingBack()
    {
        // THE OTHER SIDE OF THE FLAG, and it needs a seam rather than a filesystem trick for a reason
        // worth writing down: of the five teardowns, the REGISTRY is currently the only one that can
        // propagate at all — the three record stores and the trust store each swallow their own IO
        // failures and log. So with real types every observable failure is a credential failure, and
        // PairingMayReturn could be hardcoded true with nothing going red. The trust store reaches the
        // revoker through IFileTrustService, which is the seam that lets the false branch exist.
        var f = NewRevoker(trustOverride: new ThrowingTrustService());

        var failure = await Assert.ThrowsAsync<PairedDeviceRevocationException>(
            () => f.Revoker.RevokeAsync(Phone, CancellationToken.None));

        Assert.False(failure.PairingMayReturn,
            "the credential went, so this pairing is not coming back — whatever else failed");
        Assert.False(f.Registry.IsClientPaired(Phone));
        Assert.Equal([Phone], f.Disconnector.Disconnected);
    }

    /// <summary>Fails the one teardown that reaches the revoker through an interface.</summary>
    private sealed class ThrowingTrustService : IFileTrustService
    {
        public Task RevokeAsync(string clientId, CancellationToken ct)
            => Task.FromException(new IOException("the trust store is locked"));

        public Task<FileTrustRecord?> GetTrustAsync(string clientId, CancellationToken ct)
            => Task.FromResult<FileTrustRecord?>(null);
        public Task<IReadOnlyList<FileTrustRecord>> GetAllAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<FileTrustRecord>>([]);
        public Task<bool> IsFullBrowseGrantedAsync(string clientId, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> IsAutoAcceptIncomingAsync(string clientId, CancellationToken ct) => Task.FromResult(false);
        public Task SetFullBrowseGrantedAsync(string clientId, bool granted, CancellationToken ct) => Task.CompletedTask;
        public Task SetAutoAcceptIncomingAsync(string clientId, bool autoAccept, CancellationToken ct) => Task.CompletedTask;
        public Task<FileConsentDecision> RequestConsentAsync(
            string clientId, FileConsentRequest request, CancellationToken ct)
            => throw new NotSupportedException("nothing here prompts for consent");
        public bool TryResolveRemoteConsent(string? clientId, string? consentId, bool granted, bool remember) => false;
        public void ResolveConsent(string consentId, bool granted, bool remember)
            => throw new NotSupportedException("nothing here prompts for consent");
        public event Action<FileConsentPrompt>? ConsentRequested { add { } remove { } }
    }

    /// <summary>
    /// Fails loudly, and for the right reason, when the sandbox can write anyway.
    /// </summary>
    /// <remarks>
    /// ROOT IGNORES THE MODE BITS (CAP_DAC_OVERRIDE), and Docker CI containers default to root
    /// (review). Without this the persist would simply succeed and the test would die at
    /// <c>Assert.ThrowsAsync</c> pointing at the revoker instead of at the sandbox. NOT skipped:
    /// turning a loud failure into a silent pass is the exact thing this suite keeps being burnt by.
    /// </remarks>
    private static void AssertTheInjectionActuallyTook(DirectoryInfo directory, string storePath)
    {
        // PROBED THE SAME WAY THE INJECTION BITES, per platform: on Windows the read-only attribute
        // is on the FILE and blocks the atomic replace, so the probe opens it for writing (and never
        // writes, so the content this test compares against is untouched). On Unix the mode bits are
        // on the DIRECTORY and block the staging write, so the probe creates a file there.
        var probe = Path.Combine(directory.FullName, "probe.tmp");
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var _ = new FileStream(storePath, FileMode.Open, FileAccess.Write, FileShare.None);
            }
            else
            {
                File.WriteAllText(probe, "x");
                File.Delete(probe);
            }
        }
        catch (UnauthorizedAccessException)
        {
            // NARROWED TO THE ONE THIS INJECTION PRODUCES (review). Both permission mechanisms surface
            // as UnauthorizedAccessException; treating any IOException as "the injection took" would
            // let a sharing violation or a missing file read as a successful setup, which is a pass
            // path answering a question nobody asked.
            return;
        }

        Assert.Fail(
            "This test injects a write failure with permission bits, which an elevated or root "
            + "process can ignore. Run the suite as an ordinary user; the revoker is not what is "
            + "broken here.");
    }

    private static void MakeWritesFail(DirectoryInfo directory, string storePath)
    {
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(storePath, File.GetAttributes(storePath) | FileAttributes.ReadOnly);
        }
        else
        {
            File.SetUnixFileMode(
                directory.FullName, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }
    }

    private static void AllowWritesAgain(DirectoryInfo directory, string storePath)
    {
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(storePath, File.GetAttributes(storePath) & ~FileAttributes.ReadOnly);
        }
        else
        {
            File.SetUnixFileMode(
                directory.FullName,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void LoadingTheRegistryCollectsAnAbandonedCopyOfTheSecrets()
    {
        // THE JOIN to RemEx-jegp, which the sweep's own unit tests cannot make: they prove the sweep
        // works, not that anything calls it. A killed process leaves a staging sibling holding every
        // reconnect secret in this store, carrying the inherited ProgramData ACL rather than the one
        // RestrictStorePermissions applies to the final path — so it must not survive a startup.
        //
        // Backdated, because the sweep deliberately spares anything younger than its threshold: a
        // file that age IS an in-flight write, and deleting one would break it on Linux.
        // AND NO STORE ON DISK, which is what pins the PLACEMENT rather than merely the call
        // (review). The first version created the store, so File.Exists was true and moving the sweep
        // below LoadFromDisk's early return left this green — while the store-absent case, the one
        // the comment calls most likely, would be walked past every startup for the life of the
        // machine. A first write that died between staging and rename leaves exactly this: an orphan
        // and nothing else.
        var storePath = Path.Combine(_root.FullName, "abandoned.json");
        var orphan = Path.Combine(_root.FullName, ".abandoned.json.deadbeef.tmp");
        File.WriteAllText(orphan, "{\"phone-a\":\"AQIDBA==\"}");
        File.SetLastWriteTimeUtc(orphan, DateTime.UtcNow.AddHours(-1));

        _ = new PairedClientRegistry(NullLogger<PairedClientRegistry>.Instance, storePath);

        Assert.False(File.Exists(orphan), "a startup must not walk past a stray copy of the secrets");
        Assert.False(File.Exists(storePath), "nothing here should have created a store");
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
