using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Moq;
using Remex.Core.Services;
using Remex.Desktop.Services.Backup;
using Remex.Desktop.Services.FileTransfer;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// The Paired Devices card: what each row says, and that the card is actually reachable (RemEx-kirdm).
/// </summary>
/// <remarks>
/// <para>
/// The host-side facts landed in RemEx-nrsv and were consumed by nothing — the third component in
/// this repo found shipped, tested and unused, after PhonePresence and PairedDeviceDisplayName. This
/// is the half that puts them in front of a person.
/// </para>
/// <para>
/// THE FIRST VERSION OF THIS FILE HAD NO VIEW-MODEL TEST AT ALL, and that is why review found the
/// card unreachable in the shipped binary: its visibility flag defaulted to false and was set true
/// only by a refresh whose sole caller was a button INSIDE the hidden card. A closed loop, invisible
/// to five tests that only read axaml text and a pure formatter. Constructing the real view model is
/// the cheapest thing that would have caught it.
/// </para>
/// </remarks>
public class PairedDeviceCardTests
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    // ── The card has to be reachable at all ────────────────────────────────────

    [Fact]
    public void TheCardIsListableWithoutAnyonePressingRefreshFirst()
    {
        // THE TEST THAT WOULD HAVE CAUGHT THE WORST BUG IN THIS BEAD ON ITS FIRST RUN.
        using var _ = new ScopedPairedDeviceSource(new FakePairedDeviceSource([]));

        NewSettingsViewModel().CanListPairedDevices.Should().BeTrue(
            "the card must be visible before anything has loaded — a gate that only opens once the "
            + "user presses a button inside it can never open");
    }

    [Fact]
    public void WithNoHostInThisProcessTheCardIsHiddenRatherThanEmpty()
    {
        using var _ = new ScopedPairedDeviceSource(null);

        NewSettingsViewModel().CanListPairedDevices.Should().BeFalse();
    }

    // ── What a row says ────────────────────────────────────────────────────────

    [Fact]
    public void EachRowCarriesTheNameDatesAndItsOwnOnlineState()
    {
        var seen = new DateTimeOffset(2026, 8, 9, 10, 11, 12, TimeSpan.Zero);
        using var _ = new ScopedPairedDeviceSource(new FakePairedDeviceSource(
        [
            new PairedDeviceRow("phone-a", "Study Pixel", null, seen, IsOnline: true),
            new PairedDeviceRow("phone-b", null, null, null, IsOnline: false),
        ]));

        var vm = NewSettingsViewModel();
        vm.RefreshPairedDevices();

        vm.PairedDevices.Should().HaveCount(2);

        var named = vm.PairedDevices[0];
        named.DisplayName.Should().Be("Study Pixel");
        named.IsOnline.Should().BeTrue();
        named.LastSeenText.Should().NotBeNullOrWhiteSpace();

        // A NAMELESS DEVICE FALLS BACK TO ITS ID, NEVER TO BLANK. PairedDeviceDisplayName owns that
        // rule, and it matters because this row grows an unpair button (RemEx-4gbp2) — a nameless row
        // beside one is a decision the user cannot make safely.
        var unnamed = vm.PairedDevices[1];
        unnamed.DisplayName.Should().Be("phone-b");
        unnamed.IsOnline.Should().BeFalse();

        // Missing dates read as a marker, not as an empty cell that looks like a rendering fault.
        unnamed.FirstPairedText.Should().NotBeNullOrWhiteSpace();
        unnamed.LastSeenText.Should().NotBeNullOrWhiteSpace();

        // The dot is not the only carrier of online state.
        named.StatusAccessibleName.Should().NotBe(unnamed.StatusAccessibleName,
            "a screen reader must be able to tell the two apart without seeing a colour");
    }

    [Fact]
    public void RefreshingReplacesTheRowsRatherThanAppending()
    {
        using var _ = new ScopedPairedDeviceSource(new FakePairedDeviceSource(
            [new PairedDeviceRow("phone-a", null, null, null, IsOnline: false)]));

        var vm = NewSettingsViewModel();
        vm.RefreshPairedDevices();
        vm.RefreshPairedDevices();

        vm.PairedDevices.Should().HaveCount(1, "a refresh must replace the list, not grow it");
    }

    // ── The date rules, pure ───────────────────────────────────────────────────

    [Fact]
    public void AMissingDateSaysUnknownRatherThanRenderingBlank()
    {
        // Every device paired before the activity store existed has no dates, and a blank cell reads
        // as a rendering fault rather than as "we do not know" — on a row the user is being asked to
        // recognise as one of their own phones.
        PairedDeviceRowText.Describe(null, "Unknown", Invariant).Should().Be("Unknown");
    }

    [Fact]
    public void ADateIsShownInTheUsersOwnClock()
    {
        // THE STORES KEEP UTC, DELIBERATELY, because that is the only thing worth persisting. But
        // "last seen 03:14" means nothing to a person unless it is their clock.
        var utcNoon = new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);

        var rendered = PairedDeviceRowText.Describe(utcNoon, "Unknown", Invariant);

        // ASSERTED AGAINST THE UTC RENDERING, NOT AGAINST ToLocalTime() (review). The first version
        // compared the result to the implementation expression character for character, so deleting
        // .ToLocalTime() — the exact regression this test names — changed both sides identically and
        // passed. It also has to say when it CANNOT check: where the local zone IS UTC the two
        // renderings are the same string and there is nothing here to detect.
        if (TimeZoneInfo.Local.BaseUtcOffset != TimeSpan.Zero)
        {
            rendered.Should().NotBe(utcNoon.ToString("g", Invariant),
                "a UTC rendering on a machine that is not on UTC means ToLocalTime() was dropped");
        }

        rendered.Should().NotBe("Unknown");
    }

    [Fact]
    public void TheUnknownMarkerIsSuppliedRatherThanBakedIn()
    {
        // The decision is pure and the lookup is the caller's — the same split RemEx-ivkq settled on
        // and RemEx-0z7w reused. A marker hard-coded here would be one more English string that no
        // localization sweep can see.
        PairedDeviceRowText.Describe(null, "Inconnu", Invariant).Should().Be("Inconnu");
    }

    // ── The view actually binds it ─────────────────────────────────────────────

    [Fact]
    public void TheCardIsBoundInTheView()
    {
        // A VIEW-MODEL TEST PROVES NOTHING ABOUT WHAT THE AXAML BINDS, and Avalonia binding failures
        // are silent. Scoped to the row template rather than the whole 1000-line file, so a binding
        // name appearing somewhere else entirely cannot satisfy it (review).
        var template = RowTemplate();

        Flattened().Should().Contain("ItemsSource=\"{Binding PairedDevices}\"",
            "the card must be bound to the list, or it renders empty forever with no error");
        Flattened().Should().Contain("Command=\"{Binding RefreshPairedDeviceListCommand}\"",
            "the refresh button must reach the command");

        foreach (var binding in new[] { "DisplayName", "FirstPairedText", "LastSeenText", "IsOnline", "StatusAccessibleName" })
        {
            template.Should().Contain($"{{Binding {binding}}}",
                $"the row shows {binding}, and a dropped binding fails silently");
        }
    }

    [Fact]
    public void ThePerDeviceDotIsNotTheWholeAppPresence()
    {
        // THE REGRESSION RemEx-porg AND RemEx-7zzw EXIST FOR, in its newest disguise. This row's dot
        // must follow THIS device; the shell's "is any phone attached" would light every row at once.
        //
        // NOT PRE-FILTERED. The first version selected dots containing "IsOnline" and then asserted
        // they did not mention Presence — an assertion the filter had already made unfailable, so a
        // SECOND dot bound to whole-app presence beside the right one would have passed (review).
        var rowDots = Regex
            .Matches(RowTemplate(), "<Ellipse[^>]*Classes\\.connected[^>]*>")
            .Select(m => m.Value)
            .ToArray();

        rowDots.Should().HaveCount(1,
            "the row carries exactly one dot; none means the scan has stopped looking, and two means "
            + "something else is competing to say whether this device is online");

        rowDots.Single().Should().Contain("{Binding IsOnline}",
            "bound to PhonePresence it would light every row whenever any single phone connected");
    }

    // ── Harness ────────────────────────────────────────────────────────────────

    /// <summary>
    /// A SettingsViewModel with only the dependency that is actually dereferenced during
    /// construction.
    /// </summary>
    /// <remarks>
    /// savefileService is the only Guard.NotNull'd parameter, so it is the only one built for real —
    /// the same shape DestructiveActionFailClosedTests.CreateSettings uses, and for the same reason:
    /// layoutService, shell and fileTransferRootSettings are stored and never touched on this path.
    /// </remarks>
    private static SettingsViewModel NewSettingsViewModel()
    {
        var savefile = new RemexSavefileService(
            new DashboardLayoutService(new ThemeService()),
            Mock.Of<ILauncherStorageService>(),
            new FileTransferRootSettingsService(),
            Mock.Of<IDashboardProfileStorageService>());

        return new SettingsViewModel(null!, new ConnectionViewModel(), null!, null!, savefile);
    }

    private static string Flattened() => Regex.Replace(
        File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "SettingsView.axaml")),
        @"\s+", " ");

    /// <summary>The paired-device row template, so assertions cannot be satisfied from elsewhere.</summary>
    private static string RowTemplate()
    {
        var match = Regex.Match(
            Flattened(),
            "<DataTemplate x:DataType=\"vm:PairedDeviceItem\">.*?</DataTemplate>",
            RegexOptions.Singleline);

        match.Success.Should().BeTrue("the paired-device row template must exist to be checked");
        return match.Value;
    }

    /// <summary>A paired-device source returning exactly what a test hands it.</summary>
    private sealed class FakePairedDeviceSource(IReadOnlyList<PairedDeviceRow> rows) : IPairedDeviceSource
    {
        public IReadOnlyList<PairedDeviceRow> PairedDevices() => rows;
    }

    /// <summary>
    /// Installs a paired-device source for the life of a test, and restores what was there before.
    /// </summary>
    /// <remarks>
    /// The view model resolves through the static containers, so a test that installed one and did
    /// not put it back would change what every later test in the assembly sees — the hazard review
    /// flagged on RemEx-n8xk. Parallel execution is disabled assembly-wide, so save/restore is enough.
    /// </remarks>
    private sealed class ScopedPairedDeviceSource : IDisposable
    {
        private readonly IServiceProvider? _saved = App.EmbeddedHostServices;

        public ScopedPairedDeviceSource(IPairedDeviceSource? source)
            => App.EmbeddedHostServices = source is null ? null : new SingleService(source);

        public void Dispose() => App.EmbeddedHostServices = _saved;

        private sealed class SingleService(IPairedDeviceSource source) : IServiceProvider
        {
            public object? GetService(Type serviceType)
                => serviceType == typeof(IPairedDeviceSource) ? source : null;
        }
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
