using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Remex.Agent.Services;
using Remex.Agent.Services.FileTransfer;
using Remex.Agent.Services.Security;
using Remex.Agent.Services.Session;
using Remex.Core.Services;

namespace Remex.Agent.Tests;

/// <summary>
/// Proves that no test in this assembly can reach the machine-wide host-state store (RemEx-4u29).
///
/// <para>
/// The bead was filed after the suite wrote into the developer's live security state: a fixture's
/// full-browse grant landed in <c>C:\ProgramData\RemEx\file_transfer_trust.json</c>, and seven
/// fixture identities — attacker-phone, victim-phone, bodyless-phone, probe-phone, volumes-phone,
/// reconnect-name-client, integration-test-client-1 — were found in the real
/// <c>paired_clients.json</c> beside four genuine client ids. (Six by the time this landed; a later
/// test run deleted <c>probe-phone</c>.) A pairing entry is a credential record
/// and a full-browse grant is standing authorisation to browse the PC's filesystem, so neither is
/// something a fixture may create; it also leaves the stores useless as evidence, because after a
/// run you cannot tell a real pairing from a fixture by inspection.
/// </para>
///
/// <para>
/// The redirect itself is <c>build/TestHostStateRedirect.cs</c>, compiled into every <c>*.tests</c>
/// project by <c>Directory.Build.props</c>. These tests are what stops it being removed or
/// half-applied without anyone noticing: they assert on the DEFAULT paths, the ones a test gets when
/// it injects nothing, since injecting a temp path was never the failing case.
/// </para>
/// </summary>
public sealed class HostStateRedirectionTests
{
    private static string OverrideDirectory =>
        RemexDataPaths.HostStateDirectoryOverride
        ?? throw new InvalidOperationException(
            "The host-state redirect is not active. build/TestHostStateRedirect.cs should have run as "
            + "a module initializer before any test; without it this assembly writes to the real store.");

    /// <summary>
    /// The machine-wide directory the redirect exists to keep tests out of. Resolved the same way
    /// <see cref="RemexDataPaths.WindowsMachineWideDirectory"/> does, but computed here so a change
    /// that redefines that property cannot quietly make the negative assertions below vacuous.
    /// </summary>
    private static string MachineWideDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "RemEx");

    private static void AssertRedirected(string resolvedPath, string what)
    {
        Assert.True(
            resolvedPath.StartsWith(OverrideDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal),
            $"{what} resolved to '{resolvedPath}', which is outside the per-run test directory '{OverrideDirectory}'.");

        // Belt and braces: the positive assertion above already implies this, but it is the negative
        // one that names the actual failure, and it survives someone redefining the override.
        // The trailing separator matters — without it a sibling directory called RemExFoo would trip
        // this and report a machine-wide leak that had not happened.
        Assert.False(
            resolvedPath.StartsWith(
                MachineWideDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            $"{what} resolved into the machine-wide store at '{MachineWideDirectory}'.");
    }

    [Fact]
    public void ModuleInitializer_RedirectsHostStateToAnExistingTemporaryDirectory()
    {
        var directory = OverrideDirectory;

        Assert.True(Path.IsPathFullyQualified(directory));
        Assert.True(Directory.Exists(directory), $"Redirect directory '{directory}' was not created.");
        Assert.StartsWith(Path.GetTempPath(), directory, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(MachineWideDirectory, directory.TrimEnd(Path.DirectorySeparatorChar));
    }

    [Fact]
    public void PairingStore_DefaultPath_IsRedirected()
        => AssertRedirected(PairedClientRegistry.DefaultStorePathForTests, "The pairing registry");

    [Fact]
    public void PairedClientNameStore_DefaultPath_IsRedirected()
        => AssertRedirected(PairedClientNameStore.DefaultStorePathForTests, "The paired-client name store");

    [Fact]
    public void SessionGuardFlag_DefaultPath_IsRedirected()
        => AssertRedirected(SessionGuardSettings.FlagPathForTests, "The keep-session-unlocked flag");

    /// <summary>
    /// The certificate is the sharpest edge here: resolving the production path does not merely read
    /// the machine's TLS identity, it CREATES one when the file is absent, and a regenerated cert.pfx
    /// is a new SPKI that unpairs every pinned client.
    /// </summary>
    [Fact]
    public void CertificatePath_Default_IsRedirected()
    {
        var service = new CertificateService(NullLogger<CertificateService>.Instance);
        AssertRedirected(service.CertificatePath, "The TLS certificate");
    }

    [Fact]
    public void ResolveDirectory_IgnoresTheCallersLegacyFolder_OnEveryPlatform()
    {
        // The legacy per-user folder is what the redirect has to beat off Windows, where these stores
        // live under LocalApplicationData rather than in ProgramData.
        var legacy = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Remex");

        Assert.Equal(OverrideDirectory, RemexDataPaths.ResolveDirectory(legacy));
    }

    /// <summary>
    /// <see cref="RemexDataPaths.TryMigrateWindowsFile"/> is the last code path in the redirect that
    /// still WRITES into the machine-wide directory, and its override clause is the only thing
    /// stopping a test run from copying the developer's real <c>%LOCALAPPDATA%\Remex</c> state into
    /// <c>C:\ProgramData\RemEx</c>. This pins that clause.
    /// </summary>
    /// <remarks>
    /// A SOURCE ASSERTION, AND THE TWO DRAFTS BEFORE IT WERE BOTH WORSE. The first asserted on
    /// paired_clients.json by name and was order-dependent — the assembly shares one redirect
    /// directory and RemexHostFactory boots a real host that legitimately creates that file. The
    /// second swapped in a GUID probe name and was VACUOUS, which is worse, because it still read as
    /// evidence: with no migration source for the probe the method returns false at the
    /// <c>File.Exists(legacyPath)</c> check whether or not the guard is there, and the second
    /// assertion looked in the override directory, which this method never writes to under any
    /// mutation. Measured: deleting the override clause outright left all nine tests in this class
    /// green. Its contribution to the "8 of 9 fail" injection result was only that
    /// <see cref="OverrideDirectory"/> throws once the override is gone.
    ///
    /// A behavioural test is not available without a seam, and the seam is the thing being avoided:
    /// the migration's source is <c>%LOCALAPPDATA%</c>, which is not redirectable, and fabricating a
    /// file there to drive the test would be the very pollution this bead exists to stop — worse, if
    /// the guard were then removed the test would itself write into ProgramData. So this pins the
    /// condition, exactly as <see cref="PairingRegistry_LegacyMigration_IsGuardedByTheRedirect"/>
    /// does for the sibling migration. It catches deletion of the guard, which is the regression that
    /// actually happens; it would not catch the guard being inverted. Adding the seam is RemEx-9lbg.
    /// </remarks>
    [Fact]
    public void WindowsFileMigration_IsGuardedByTheRedirect()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "remex.core", "Services", "RemexDataPaths.cs"));

        // Comments carry the word too, and a comment is not a guard. One pass is enough: `//[^\n]*`
        // already swallows `///` doc comments, since the third slash matches `[^\n]`. Review checked
        // that a second `///` pass changed zero characters before it was removed from here.
        var code = Regex.Replace(source, @"//[^\n]*", string.Empty);

        // The early-return block between the method signature and its first `return false;` — which
        // is the guard, and nothing else fits between the two.
        var guard = Regex.Match(
            code,
            @"bool\s+TryMigrateWindowsFile\s*\([^)]*\)\s*\{(?<guard>.*?)return\s+false\s*;",
            RegexOptions.Singleline);

        Assert.True(guard.Success, "TryMigrateWindowsFile no longer opens with an early-return guard.");
        Assert.Contains(
            nameof(RemexDataPaths.HostStateDirectoryOverride),
            guard.Groups["guard"].Value);
    }

    /// <summary>
    /// The behavioural half, kept separate because it is a safety net rather than the guard's proof:
    /// it cannot fail when the guard is deleted (see
    /// <see cref="WindowsFileMigration_IsGuardedByTheRedirect"/>). What it does catch is a migration
    /// taught to fabricate its target — which would write into the machine-wide directory for real.
    /// </summary>
    [Fact]
    public void WindowsFileMigration_CreatesNothing_ForAFileWithNoMigrationSource()
    {
        var probeFileName = $"redirect-migration-probe-{Guid.NewGuid():N}.json";

        Assert.False(RemexDataPaths.TryMigrateWindowsFile(probeFileName));

        // The machine-wide directory, NOT the override directory: this method only ever writes to the
        // former, so asserting on the latter would be asserting on a path it cannot produce.
        Assert.False(File.Exists(Path.Combine(MachineWideDirectory, probeFileName)));
    }

    /// <summary>
    /// The end-to-end case the bead was actually filed over, driven through the real service with no
    /// injected path: granting full browse must land in the redirected store. This is the write that
    /// put a fixture's <c>fullBrowseGranted</c> record into the developer's machine-wide file.
    /// </summary>
    [Fact]
    public async Task FullBrowseGrant_WithNoInjectedPath_WritesIntoTheRedirectedStore()
    {
        var storePath = Path.Combine(OverrideDirectory, "file_transfer_trust.json");
        if (File.Exists(storePath))
        {
            File.Delete(storePath);
        }

        var registry = new PairedClientRegistry(
            NullLogger<PairedClientRegistry>.Instance,
            Path.Combine(OverrideDirectory, "redirection-test-pairings.json"));
        registry.RegisterClient("redirection-test-client");

        var service = new FileTrustService(
            NullLogger<FileTrustService>.Instance,
            registry,
            new Remex.Agent.Services.ClientSessionRegistry(),
            storePath: null,
            consentTimeout: TimeSpan.FromSeconds(5));

        await service.SetFullBrowseGrantedAsync("redirection-test-client", true, default);

        Assert.True(
            File.Exists(storePath),
            $"The trust store was not written to '{storePath}'; the default path is not redirected.");
        Assert.Contains("redirection-test-client", await File.ReadAllTextAsync(storePath));
    }

    /// <summary>
    /// The pairing registry runs its OWN legacy migration, separate from
    /// <see cref="RemexDataPaths.TryMigrateWindowsFile"/>, and it must be suppressed under the
    /// redirect for the same reason: the source is the developer's real
    /// <c>%LOCALAPPDATA%\Remex\paired_clients.json</c>, which the move to ProgramData copied rather
    /// than deleted, so it still exists on any machine that ran an older build. Review found the
    /// first draft of this change had closed the hole in one migration and left it open in the other
    /// — on the very store the bead was filed over.
    /// </summary>
    /// <remarks>
    /// A SOURCE ASSERTION, WHICH IS WEAKER THAN A BEHAVIOURAL ONE, AND THE REASON IS WORTH STATING.
    /// The guard's live branch cannot be reached from a test at all: the redirect is unconditional in
    /// every test assembly, so no test can ever observe the constructor with the override unset, and
    /// the migration's source directory is <c>%LOCALAPPDATA%</c> — not redirectable, and fabricating
    /// a file there would be the very pollution this bead exists to stop. So this pins the condition
    /// rather than the behaviour. It catches the regression that actually happened, which is someone
    /// simplifying the boolean back to <c>storePath is null</c>; it would not catch the migration
    /// being made to ignore the flag from somewhere else. Same shape as
    /// <c>DxgiVtableCacheGuardTests</c>, which this borrows its source lookup from.
    /// </remarks>
    [Fact]
    public void PairingRegistry_LegacyMigration_IsGuardedByTheRedirect()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "remex.agent", "Services", "Security", "PairedClientRegistry.cs"));

        // Comments carry the word too, and a comment is not a guard.
        var code = Regex.Replace(source, @"//[^\n]*", string.Empty);
        var guard = Regex.Match(
            code, @"shouldMigrateLegacyStore\s*=\s*(?<condition>[^;]+);", RegexOptions.Singleline);

        Assert.True(guard.Success, "The constructor no longer computes a shouldMigrateLegacyStore flag.");
        Assert.Contains(
            nameof(RemexDataPaths.HostStateDirectoryOverride),
            guard.Groups["condition"].Value);
    }

    /// <summary>
    /// Autostart registration is the one piece of user state here that is not a file RemEx owns, and
    /// on Linux it is still a file: <c>~/.config/autostart/remex-agent.desktop</c>. The redirect
    /// moves the directory it hangs off, so a test that registered autostart registers it in the
    /// per-run temp directory instead of the developer's session. (RemEx-ln0k)
    /// </summary>
    [Fact]
    public void LinuxAutostartDirectory_IsRedirected()
        => AssertRedirected(
            StartupRegistrationService.LinuxAutostartDirectoryForTests, "The XDG autostart directory");

    /// <summary>
    /// Windows autostart has no directory to redirect — it is a Task Scheduler logon task plus an
    /// HKCU <c>Run</c> value — so the redirect suppresses it instead, reads included.
    /// </summary>
    /// <remarks>
    /// THE READ IS SUPPRESSED WITH THE WRITES, and this is the test that pins it. Without the guard
    /// the query returns the developer's real answer: <c>true</c> on a machine where autostart is
    /// registered and <c>false</c> on one where it is not, but never <c>null</c> — so this fails on
    /// either machine. Off Windows it exercises the Linux branch instead, which answers from the
    /// redirected directory and therefore says "not registered" in a fresh per-run one.
    /// </remarks>
    [Fact]
    public void AutostartQuery_AnswersFromTheRedirect_NotTheRealMachine()
    {
        var service = new StartupRegistrationService();

        if (OperatingSystem.IsWindows())
        {
            Assert.Null(service.TryIsEnabled());
        }
        else
        {
            Assert.False(service.TryIsEnabled());
        }
    }

    /// <summary>
    /// The three Windows code paths that MUTATE the real registration must each refuse to run under
    /// the redirect.
    /// </summary>
    /// <remarks>
    /// A SOURCE ASSERTION, FOR THE SAME REASON AS THE TWO ABOVE IT, AND HERE THE REASON IS SHARPEST.
    /// Observing these behaviourally means letting them run: the injection that proves the test can
    /// fail would then register a logon task pointing at the test host, or delete the developer's
    /// real one — and nothing would put that back. There is no seam that avoids it either, because
    /// the thing being tested is precisely that no seam is used. So this pins the condition. It
    /// catches deletion of a guard, which is the regression that actually happens; it would not
    /// catch one being inverted.
    /// </remarks>
    [Theory]
    [InlineData("RemoveLegacyWindowsRunKey")]
    [InlineData("RegisterWindowsLogonTask")]
    [InlineData("RemoveWindowsLogonTask")]
    public void WindowsAutostartMutation_IsGuardedByTheRedirect(string methodName)
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "remex.agent", "Services", "StartupRegistrationService.cs"));

        // Comments carry the word too, and a comment is not a guard.
        var code = Regex.Replace(source, @"//[^\n]*", string.Empty);

        // Everything between the method's opening brace and its first `return;` — which is the
        // early-return guard, and nothing else fits between the two.
        var guard = Regex.Match(
            code,
            $@"void\s+{methodName}\s*\([^)]*\)\s*\{{(?<guard>.*?)return\s*;",
            RegexOptions.Singleline);

        Assert.True(guard.Success, $"{methodName} no longer opens with an early-return guard.");
        Assert.Contains("IsHostStateRedirected", guard.Groups["guard"].Value);
    }

    // [CallerFilePath] rather than walking up from the assembly, so building with --artifacts-path
    // outside the repo does not break this with an unrelated-looking error (RemEx-6i1l).
    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, ".."));
}
