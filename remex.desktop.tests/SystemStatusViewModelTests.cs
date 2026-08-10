using Remex.Core.Services.Readiness;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests;

/// <summary>
/// Pins what the System status card does with a report, and what it does without one (RemEx-id37).
/// </summary>
public class SystemStatusViewModelTests
{
    private sealed class FakeReadiness(SystemReadinessReport report) : ISystemReadinessService
    {
        public int Runs { get; private set; }

        public SystemReadinessReport Run()
        {
            Runs++;
            return report;
        }
    }

    private static SystemReadinessReport Report(params ReadinessCheck[] checks) => new(checks);
    private static ReadinessCheck Check(ReadinessCheckId id, ReadinessState state) => new(id, state, "developer detail");

    /// <summary>Runs the work inline, and records that the off-thread seam was used at all.</summary>
    private static Func<Func<SystemReadinessReport?>, Task<SystemReadinessReport?>> Inline(List<string> log) =>
        work =>
        {
            log.Add("off-ui-thread");
            return Task.FromResult(work());
        };

    [Fact]
    public async Task TheProbeRunsOffTheUiThread_BecauseItLaunchesAProcess()
    {
        // NOT A STYLE POINT. ISystemReadinessService.Run() shells out for the firewall check and
        // blocks; on the UI thread that freezes the window for as long as the probe takes. The seam
        // exists so this is assertable rather than hoped for.
        var log = new List<string>();
        var vm = new SystemStatusViewModel(
            () => new FakeReadiness(Report(Check(ReadinessCheckId.Firewall, ReadinessState.Ok))),
            Inline(log));

        await vm.RefreshAsync();

        Assert.Equal(["off-ui-thread"], log);
    }

    [Fact]
    public async Task RowsComeBackWorstFirst()
    {
        var vm = new SystemStatusViewModel(
            () => new FakeReadiness(Report(
                Check(ReadinessCheckId.Autostart, ReadinessState.Ok),
                Check(ReadinessCheckId.Firewall, ReadinessState.Problem),
                Check(ReadinessCheckId.Elevation, ReadinessState.Unknown))),
            Inline([]));

        await vm.RefreshAsync();

        Assert.Equal(
            [ReadinessState.Problem, ReadinessState.Unknown, ReadinessState.Ok],
            vm.Rows.Select(r => r.State));
    }

    [Fact]
    public async Task TheCardCollapsesONLYWhenEveryApplicableRowIsOk()
    {
        // Deliberately not "nothing is a Problem". A card that collapsed on a Warning or an Unknown
        // would hide the two states that most need a second look, which is the same reason
        // IsFullyReady refuses to treat them as passing.
        var allOk = new SystemStatusViewModel(
            () => new FakeReadiness(Report(
                Check(ReadinessCheckId.Firewall, ReadinessState.Ok),
                Check(ReadinessCheckId.Autostart, ReadinessState.Ok))),
            Inline([]));
        await allOk.RefreshAsync();
        Assert.True(allOk.IsFullyReady);

        foreach (var notOk in new[] { ReadinessState.Warning, ReadinessState.Unknown, ReadinessState.Problem })
        {
            var vm = new SystemStatusViewModel(
                () => new FakeReadiness(Report(
                    Check(ReadinessCheckId.Firewall, ReadinessState.Ok),
                    Check(ReadinessCheckId.Autostart, notOk))),
                Inline([]));
            await vm.RefreshAsync();

            Assert.False(vm.IsFullyReady, $"the card collapsed while a row was {notOk}");
        }
    }

    [Fact]
    public async Task NoHostMeansTheCardSaysNOTHING_RatherThanSayingEverythingIsFine()
    {
        // THE WORST AVAILABLE OUTCOME would be an empty card rendering as green: it would assert that
        // everything is fine using no information at all, on the one surface a user opens precisely
        // to find out that the host is down. EmbeddedHostServiceLocator documents that degraded mode
        // as real, so this is a state the card will actually reach.
        var vm = new SystemStatusViewModel(() => null, Inline([]));

        await vm.RefreshAsync();

        Assert.True(vm.IsUnavailable);
        Assert.False(vm.HasReport);
        Assert.Empty(vm.Rows);
        Assert.False(vm.IsFullyReady);
    }

    [Fact]
    public async Task ASecondRefreshWhileOneIsRunningIsIgnored()
    {
        // The card has a "Check again" button and the probe is slow, so double-clicking it is the
        // normal case rather than the odd one.
        var service = new FakeReadiness(Report(Check(ReadinessCheckId.Firewall, ReadinessState.Ok)));
        SystemStatusViewModel? vm = null;
        Task? reentrant = null;

        vm = new SystemStatusViewModel(() => service, work =>
        {
            reentrant ??= vm!.RefreshAsync();
            return Task.FromResult(work());
        });

        await vm.RefreshAsync();

        // UNCONDITIONAL: guarded, a refactor that stopped calling _runOffUiThread would leave this
        // green while testing nothing at all.
        Assert.NotNull(reentrant);
        await reentrant;

        Assert.Equal(1, service.Runs);
        Assert.False(vm.IsChecking);
    }

    [Fact]
    public async Task RowsCarryKEYS_NotJustEnglish()
    {
        // Asserting on resolved English would pass just as happily against a row that resolved to the
        // wrong sentence in the other eight languages. The key is the thing that is actually chosen.
        var vm = new SystemStatusViewModel(
            () => new FakeReadiness(Report(Check(ReadinessCheckId.Certificate, ReadinessState.Problem))),
            Inline([]));

        await vm.RefreshAsync();

        var row = Assert.Single(vm.Rows);
        Assert.Equal("SystemStatus_Certificate_Title", row.TitleKey);
        Assert.Equal("SystemStatus_Certificate_Problem", row.SentenceKey);

        // And the certificate row never offers a FIX, whatever state it is in - regenerating a
        // certificate un-pairs every phone at once. It does now offer Explain (RemEx-tb0a), which
        // regenerates nothing and says to restart RemEx as administrator, so the assertion is on the
        // rule rather than on "no button at all", which was only ever a proxy for it.
        Assert.False(row.ShowsFix);
        Assert.True(row.ShowsExplain);
        Assert.Equal("SystemStatus_Help_Certificate", row.HelpBodyKey);
    }

    [Fact]
    public async Task NoRowEVERShowsTheDeveloperFacingDetail()
    {
        // Detail is assembled for logs and can carry a path or an exception message. The card must
        // not be able to put it on screen - here demonstrated end to end, with an alarming Detail on
        // every row and an assertion that none of it reached any user-facing property.
        var vm = new SystemStatusViewModel(
            () => new FakeReadiness(new SystemReadinessReport(
            [
                new ReadinessCheck(ReadinessCheckId.Firewall, ReadinessState.Problem,
                    @"Access denied: C:\ProgramData\RemEx\cert.pfx"),
                new ReadinessCheck(ReadinessCheckId.Autostart, ReadinessState.Unknown,
                    "schtasks exited 0x80070005"),
            ])),
            Inline([]));

        await vm.RefreshAsync();

        foreach (var row in vm.Rows)
        {
            foreach (var shown in new[] { row.Title, row.Sentence, row.TitleKey, row.SentenceKey })
            {
                Assert.DoesNotContain("cert.pfx", shown, StringComparison.Ordinal);
                Assert.DoesNotContain("schtasks", shown, StringComparison.Ordinal);
                Assert.DoesNotContain("0x8007", shown, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task ChangingLanguageReresolvesEveryRow()
    {
        // The app switches language live. A row that resolved once at construction would sit there in
        // the old language while everything around it changed.
        var vm = new SystemStatusViewModel(
            () => new FakeReadiness(Report(Check(ReadinessCheckId.Firewall, ReadinessState.Problem))),
            Inline([]));
        await vm.RefreshAsync();

        var original = LocalizationService.Instance.CultureTag;
        try
        {
            LocalizationService.Instance.SetCulture("fr");
            var french = vm.Rows[0].Sentence;

            LocalizationService.Instance.SetCulture("es");
            var spanish = vm.Rows[0].Sentence;

            Assert.NotEqual(french, spanish);
            Assert.False(string.IsNullOrWhiteSpace(french));
            Assert.False(string.IsNullOrWhiteSpace(spanish));

            // And neither is the raw key, which is what an unresolved lookup returns.
            Assert.NotEqual(vm.Rows[0].SentenceKey, french);
            Assert.NotEqual(vm.Rows[0].SentenceKey, spanish);
        }
        finally
        {
            LocalizationService.Instance.SetCulture(original);
        }
    }

    [Fact]
    public void DisposeUnsubscribesFromTheLanguageService()
    {
        // LocalizationService.Instance is a process-wide singleton, so a card that never unsubscribed
        // would keep every view model it was ever built with alive for the life of the app.
        var vm = new SystemStatusViewModel(() => null, Inline([]));
        vm.Dispose();
        vm.Dispose();
    }

    // ── The one repair the card offers ────────────────────────────────────────────────────────

    private sealed class FakeStartup(bool supported = true, Exception? throws = null)
        : IStartupRegistrationService
    {
        public int Enabled { get; private set; }
        public bool IsSupported => supported;
        public bool IsEnabled() => Enabled > 0;
        public bool? TryIsEnabled() => Enabled > 0;

        public void SetEnabled(bool enabled)
        {
            if (throws is not null) throw throws;
            if (enabled) Enabled++;
        }
    }

    private static SystemStatusViewModel WithFix(
        FakeStartup startup, FakeReadiness readiness, List<string>? log = null) =>
        new(() => readiness, Inline(log ?? []), () => startup, work => { work(); return Task.CompletedTask; });

    [Fact]
    public async Task FixingAutostartRegistersIt_AndThenRECHECKSRatherThanAssuming()
    {
        // A repair that silently failed but repainted the row green would be worse than no button.
        var startup = new FakeStartup();
        var readiness = new FakeReadiness(Report(Check(ReadinessCheckId.Autostart, ReadinessState.Warning)));
        var vm = WithFix(startup, readiness);
        await vm.RefreshAsync();

        await vm.FixCommand.ExecuteAsync(vm.Rows[0]);

        Assert.Equal(1, startup.Enabled);
        Assert.Equal(2, readiness.Runs);
    }

    [Fact]
    public async Task NOTHINGIsRepairedWithoutTheButton()
    {
        // The card reports; it never changes the machine to make its own report look better. A plain
        // refresh must leave the startup registration exactly as it found it.
        var startup = new FakeStartup();
        var vm = WithFix(startup, new FakeReadiness(Report(Check(ReadinessCheckId.Autostart, ReadinessState.Problem))));

        await vm.RefreshAsync();
        await vm.RefreshAsync();

        Assert.Equal(0, startup.Enabled);
    }

    [Fact]
    public async Task TheFixDoesNothingForTheCertificateRow()
    {
        // HONEST ABOUT WHAT THIS REACHES, after review pointed out the first version claimed more.
        // The command has an id guard as well, but this test cannot exercise it: the certificate row
        // has ShowsFix false, so FixAsync exits one clause earlier and the id check is never reached.
        // Since ShowsFix is derived FROM the id, no row can be built that passes one and fails the
        // other - the id guard is belt and braces against a future refactor, and is untested by
        // construction. What this does prove is the outcome that matters: the certificate row cannot
        // trigger a repair.
        var startup = new FakeStartup();
        var vm = WithFix(startup, new FakeReadiness(Report(Check(ReadinessCheckId.Certificate, ReadinessState.Problem))));
        await vm.RefreshAsync();

        await vm.FixCommand.ExecuteAsync(vm.Rows[0]);

        Assert.Equal(0, startup.Enabled);
    }

    [Fact]
    public async Task AFailedRepairIsReportedByTheRECHECK_NotByAnUnhandledException()
    {
        // Registering a logon task can genuinely fail - an RPC endpoint that is not there, a denied
        // write. Rethrowing would put an unhandled exception on the UI thread; swallowing silently
        // would leave the user believing it worked. The re-check is what tells them the truth.
        var startup = new FakeStartup(throws: new UnauthorizedAccessException("denied"));
        var readiness = new FakeReadiness(Report(Check(ReadinessCheckId.Autostart, ReadinessState.Warning)));
        var vm = WithFix(startup, readiness);
        await vm.RefreshAsync();

        await vm.FixCommand.ExecuteAsync(vm.Rows[0]);

        Assert.Equal(2, readiness.Runs);
        Assert.False(vm.IsChecking);
    }

    [Fact]
    public async Task AnUnsupportedPlatformIsLeftAlone()
    {
        var startup = new FakeStartup(supported: false);
        var vm = WithFix(startup, new FakeReadiness(Report(Check(ReadinessCheckId.Autostart, ReadinessState.Warning))));
        await vm.RefreshAsync();

        await vm.FixCommand.ExecuteAsync(vm.Rows[0]);

        Assert.Equal(0, startup.Enabled);
    }

    [Theory]
    [InlineData("unauthorized")]
    [InlineData("security")]
    public async Task ANYRepairFailureIsContained_NotJustTheOnesSomebodyListed(string kind)
    {
        // THE DEFECT REVIEW FOUND. The catch used to name three exception types, and the only test
        // for it threw one of those three - so it could not detect the hole. StartupRegistrationService
        // calls WindowsIdentity.GetCurrent().Name OUTSIDE its own try, and on a domain-joined PC with
        // the domain controller unreachable that throws IdentityNotMappedException, which matched
        // none of them: it would escape Task.Run, be rethrown at the await, and AsyncRelayCommand
        // would repost it to the UI context as unhandled. RemEx would die on a button press.
        Exception thrown = kind switch
        {
            "unauthorized" => new UnauthorizedAccessException("denied"),
            _ => new System.Security.SecurityException("no temp path"),
        };

        await AssertRepairFailureIsContainedAsync(thrown);
    }

    /// <summary>
    /// The identity-not-mapped row, split out of the theory above (RemEx-vh62).
    /// </summary>
    /// <remarks>
    /// THIS ROW IS THE WHOLE POINT OF THAT TEST — it is the exception the old catch list missed — so it
    /// is emphatically not dropped, only moved somewhere it can be skipped honestly on Linux.
    ///
    /// Constructing <c>IdentityNotMappedException</c> touches Windows Principal APIs, which throw
    /// PlatformNotSupportedException on Linux. Only the test DATA is Windows-bound; the product code is
    /// not implicated at all, which is why the assertion is unchanged rather than relaxed.
    ///
    /// SPLIT RATHER THAN MARKING THE WHOLE THEORY, and that is not a style choice: xUnit v2's
    /// TheoryDiscoverer short-circuits on a non-null Skip and emits ONE test case instead of one per
    /// [InlineData] row, so a "WindowsOnlyTheory" would have silently taken the two rows that DO work
    /// on Linux down with it. See the note in WindowsOnlyAttributes.cs.
    /// </remarks>
    [WindowsOnlyFact("constructing IdentityNotMappedException touches Windows Principal APIs")]
    public async Task ARepairFailureFromAnUnreachableDomainControllerIsContained()
    {
        await AssertRepairFailureIsContainedAsync(
            new System.Security.Principal.IdentityNotMappedException("no DC"));
    }

    /// <summary>
    /// Shared body, so the split above cannot let the two paths drift into asserting different things.
    /// </summary>
    private static async Task AssertRepairFailureIsContainedAsync(Exception thrown)
    {
        // Thrown through a REAL Task.Run so the wrapped-and-rethrown path is the one under test,
        // rather than a synchronous throw that never crosses a task boundary.
        var startup = new FakeStartup(throws: thrown);
        var readiness = new FakeReadiness(Report(Check(ReadinessCheckId.Autostart, ReadinessState.Warning)));
        var vm = new SystemStatusViewModel(
            () => readiness, Inline([]), () => startup, work => Task.Run(work));
        await vm.RefreshAsync();

        await vm.FixCommand.ExecuteAsync(vm.Rows[0]);

        // Survived, and the re-check ran so the row reports what the machine now says.
        Assert.Equal(2, readiness.Runs);
        Assert.False(vm.IsChecking);
    }
}
