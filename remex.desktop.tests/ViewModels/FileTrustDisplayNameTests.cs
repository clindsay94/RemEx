using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Moq;
using Remex.Core.Services;
using Remex.Core.Services.FileTransfer;
using Remex.Desktop.Services;
using Remex.Desktop.Services.Backup;
using Remex.Desktop.Services.FileTransfer;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// The File-Sharing Trust card names its devices the way the rest of Settings does (RemEx-9me77).
/// </summary>
/// <remarks>
/// <para>
/// Connor, on seeing the shipped card: <i>"it's named '07ca4e9d5383…' which means absolutely nothing
/// to anyone. Why doesn't it have the same configurable name that the paired devices show?"</i> The
/// helper that answers this — <see cref="PairedDeviceDisplayName"/> — already existed and was
/// already used by the Paired Devices card directly above. It had simply never been wired in here.
/// </para>
/// <para>
/// THE HELPER'S OWN DOCUMENTATION CITES THIS LIST AS ITS BAD EXAMPLE. <c>PairedDeviceDisplayName.cs</c>
/// explains its never-blank contract by saying "the existing File-Sharing Trust list already renders
/// raw ShortIds and is described as opaque, and a blank row is strictly worse than opaque". A
/// component written with a named counterexample, shipped next to that counterexample, for two beads.
/// These tests exist so the wiring cannot quietly come out again.
/// </para>
/// <para>
/// Row rendering is asserted against the axaml text, the idiom this test project already uses for
/// bindings (<c>PairedDeviceCardTests</c>, <c>StatusDotPresenceBindingTests</c>): Avalonia binding
/// failures are silent and there is no headless render here.
/// </para>
/// </remarks>
public class FileTrustDisplayNameTests
{
    // ── The bug itself ─────────────────────────────────────────────────────────

    [Fact]
    public void ATrustRowShowsTheUsersConfiguredName()
    {
        using var _ = new ScopedPairedDeviceSource(new FakePairedDeviceSource(
            [new PairedDeviceRow("07ca4e9d5383aabbcc", "Study Pixel", null, null, null, IsOnline: true)]));

        var vm = SettingsWithTrustRecords(new FileTrustRecord { ClientId = "07ca4e9d5383aabbcc", FullBrowseGranted = true, AutoAcceptIncoming = false });

        vm.TrustedDevices.Should().ContainSingle();
        vm.TrustedDevices[0].DisplayName.Should().Be("Study Pixel",
            "this is the whole bead — the card showed a raw client id while the card directly above "
            + "it showed the name the user had set for the same device");
    }

    [Fact]
    public void TheNameOverrideOutranksTheDeviceReportedName()
    {
        // Same precedence the paired card uses. A user who renamed a device expects that name
        // everywhere, not just on the card where they typed it.
        using var _ = new ScopedPairedDeviceSource(new FakePairedDeviceSource(
            [new PairedDeviceRow("phone-a", "Galaxy S26", "Connor's phone", null, null, IsOnline: true)]));

        var vm = SettingsWithTrustRecords(new FileTrustRecord { ClientId = "phone-a", FullBrowseGranted = false, AutoAcceptIncoming = false });

        vm.TrustedDevices[0].DisplayName.Should().Be("Connor's phone");
    }

    [Fact]
    public void ATrustEntryWithNoMatchingPairedDeviceFallsBackToTheId()
    {
        // A trust record can outlive its pairing — trust and pairing are separate stores on purpose.
        // Resolve's contract is that it NEVER returns blank, because a nameless row beside a Revoke
        // button is a decision the user cannot make safely.
        using var _ = new ScopedPairedDeviceSource(new FakePairedDeviceSource([]));

        var vm = SettingsWithTrustRecords(new FileTrustRecord { ClientId = "orphaned-client-id", FullBrowseGranted = false, AutoAcceptIncoming = false });

        vm.TrustedDevices[0].DisplayName.Should().Be("orphaned-client-id");
        vm.TrustedDevices[0].DisplayName.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void TheShortIdSurvivesAsWellAsTheName()
    {
        // The id is demoted, not deleted. It is the one thing on this card comparable against what
        // the phone shows, which is how a user tells two identically-named devices apart.
        using var _ = new ScopedPairedDeviceSource(new FakePairedDeviceSource(
            [new PairedDeviceRow("07ca4e9d5383aabbcc", "Study Pixel", null, null, null, IsOnline: true)]));

        var vm = SettingsWithTrustRecords(new FileTrustRecord { ClientId = "07ca4e9d5383aabbcc", FullBrowseGranted = true, AutoAcceptIncoming = false });

        vm.TrustedDevices[0].ShortId.Should().Be("07ca4e9d5383…");
    }

    // ── The two cards must not disagree ────────────────────────────────────────

    [Fact]
    public void RenamingADeviceThroughTheCommandRefreshesTheTrustRowToo()
    {
        // WITHOUT THIS THE PAGE CONTRADICTS ITSELF. Both cards are on screen at once; a rename that
        // refreshed only the list above would leave the one below showing the old name until the
        // user pressed Refresh, which reads as the rename having half-failed.
        //
        // DRIVEN THROUGH ApplyPairedDeviceRenameCommand, NOT BY CALLING THE REFRESH DIRECTLY, and
        // that distinction is the whole value of this test. The first version called
        // RefreshTrustedDeviceNames() itself — so deleting the call site in ApplyPairedDeviceRename
        // left it green while the two cards drifted apart in the real app. Measured, not assumed:
        // that injection passed 38/38. Proving a method exists is not proving anything calls it,
        // which is the same shape as the dead Fix button in RemEx-tb0a.
        var source = new MutableSource(Row("phone-a", "Old Name"));
        var writer = new StoreWritingRenamer(source);
        using var _ = new ScopedPairedDeviceServices(source, writer);

        var vm = SettingsWithTrustRecords(new FileTrustRecord { ClientId = "phone-a", FullBrowseGranted = false, AutoAcceptIncoming = false });
        vm.RefreshPairedDevices();
        vm.TrustedDevices[0].DisplayName.Should().Be("Old Name");

        vm.PairedDevices[0].PendingName = "New Name";
        vm.ApplyPairedDeviceRenameCommand.Execute(vm.PairedDevices[0]);

        vm.PairedDevices[0].DisplayName.Should().Be("New Name", "the card the user typed into");
        vm.TrustedDevices[0].DisplayName.Should().Be("New Name",
            "and the card below it, without the user pressing Refresh — both read the same name map, "
            + "so they must never show different names for one device");
    }

    [Fact]
    public void RefreshingTrustNamesRaisesChangeNotification()
    {
        // The row is already on screen when a rename lands, so the update has to be observable —
        // a silently-mutated property leaves the old text rendered.
        using var _ = new ScopedPairedDeviceSource(new FakePairedDeviceSource(
            [new PairedDeviceRow("phone-a", "Study Pixel", null, null, null, IsOnline: true)]));

        var vm = SettingsWithTrustRecords(new FileTrustRecord { ClientId = "phone-a", FullBrowseGranted = false, AutoAcceptIncoming = false });
        var item = vm.TrustedDevices[0];

        var raised = new List<string?>();
        item.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        item.DisplayName = "Something Else";

        raised.Should().Contain(nameof(FileTrustDeviceItem.DisplayName));
    }

    // ── The revoke confirmation ────────────────────────────────────────────────

    [Fact]
    public async Task TheRevokeConfirmationNamesTheDeviceRatherThanItsId()
    {
        // THE ASSERTION THAT ACTUALLY PINS FIX POINT 2 (review). The equivalent check in
        // DestructiveActionFailClosedTests builds its item with no name map, so DisplayName falls
        // back to ClientId and `Contain(device.DisplayName)` is `Contain(device.ClientId)` in
        // disguise — it passes just as happily if someone reverts the dialog to the raw id.
        //
        // MEASURED: injecting `item.ClientId` at the revoke call site compiles and passes 38/38.
        // The lesson generalises — when a property has a fallback, inject THE FALLBACK, because that
        // is the wrong value an assertion cannot see.
        using var _ = new ScopedPairedDeviceServices(
            new MutableSource(Row("07ca4e9d5383aabbcc", "Study Pixel")), writer: null);

        var vm = SettingsWithTrustRecords(new FileTrustRecord { ClientId = "07ca4e9d5383aabbcc", FullBrowseGranted = true, AutoAcceptIncoming = false });
        vm.FileTrustServiceForTests = Mock.Of<IFileTrustService>();

        string? body = null;
        vm.OnConfirmationRequested = (_, message, _) => { body = message; return Task.FromResult(false); };

        await vm.RevokeTrustAsync(vm.TrustedDevices[0]);

        body.Should().NotBeNull();
        body.Should().Contain("Study Pixel",
            "the dialog has to say WHICH device loses access, in the name the user gave it");
        body.Should().NotContain("07ca4e9d5383",
            "and not in a raw client id — that is this bead's entire complaint, and it is the "
            + "assertion that fails on both ClientId and ShortId");
    }

    // ── The row template actually renders both ─────────────────────────────────

    [Fact]
    public void TheTrustRowTemplateBindsTheNameAndTheId()
    {
        var template = TrustRowTemplate();

        template.Should().Contain("{Binding DisplayName}",
            "the name is the row's headline — binding only ShortId is the bug this bead fixed");
        template.Should().Contain("{Binding ShortId}",
            "the id stays as a second line; it is what a user compares against the phone");
    }

    [Fact]
    public void TheNameIsTheHeadlineAndTheIdIsTheSecondLine()
    {
        // PRESENCE IS NOT HIERARCHY, and the bead was never about presence (review). The complaint
        // was that the id was the headline. A restyle that swapped the two — id at 14pt bold on top,
        // name at 11pt muted underneath — keeps both bindings, passes a presence check, and
        // reproduces Connor's original report exactly.
        var template = TrustRowTemplate();

        template.IndexOf("{Binding DisplayName}", StringComparison.Ordinal)
            .Should().BeLessThan(template.IndexOf("{Binding ShortId}", StringComparison.Ordinal),
                "the name comes first in the row; the id sits under it");

        // The name carries the row's weight, the id does not.
        template.Should().MatchRegex(
            @"Text=""\{Binding DisplayName\}"" FontSize=""14"" FontWeight=""Bold""",
            "the name is the 14pt bold line — demoting it to the small muted style is the bug");
        template.Should().MatchRegex(
            @"Text=""\{Binding ShortId\}"" FontSize=""11""",
            "the id is the small secondary line");
    }

    [Fact]
    public void TheTrustRowStillOffersRevoke()
    {
        // Guards the rewrite of this row: the Revoke button sits in the same Grid the name moved
        // inside, and losing it while restyling would be silent.
        TrustRowTemplate().Should().Contain("{Binding RevokeCommand}");
    }

    // ── plumbing ───────────────────────────────────────────────────────────────

    /// <summary>
    /// A view model whose trust list holds <paramref name="records"/>.
    /// </summary>
    /// <remarks>
    /// Calls <see cref="SettingsViewModel.ReplaceTrustedDevices"/> directly rather than driving the
    /// async loader: that path hops through <c>Dispatcher.UIThread.Post</c>, which never runs
    /// headless, so the list would stay empty and every assertion here would pass vacuously against
    /// index 0 of nothing. This is the same method the loader calls.
    /// </remarks>
    private static SettingsViewModel SettingsWithTrustRecords(params FileTrustRecord[] records)
    {
        var vm = NewSettingsViewModel();
        vm.ReplaceTrustedDevices(records);
        return vm;
    }

    private static SettingsViewModel NewSettingsViewModel()
    {
        var savefile = new RemexSavefileService(
            new DashboardLayoutService(new ThemeService()),
            Mock.Of<ILauncherStorageService>(),
            new FileTransferRootSettingsService(),
            Mock.Of<IDashboardProfileStorageService>());

        return new SettingsViewModel(null!, new ConnectionViewModel(), null!, null!, savefile);
    }

    private sealed class FakePairedDeviceSource(IReadOnlyList<PairedDeviceRow> rows) : IPairedDeviceSource
    {
        public IReadOnlyList<PairedDeviceRow> PairedDevices() => rows;
    }

    private static PairedDeviceRow Row(string id, string? name = null)
        => new(id, DeviceName: null, NameOverride: name, null, null, IsOnline: false);

    /// <summary>A source whose rows can change, so a rename can be observed end to end.</summary>
    private sealed class MutableSource(params PairedDeviceRow[] rows) : IPairedDeviceSource
    {
        private PairedDeviceRow[] _rows = rows;

        public IReadOnlyList<PairedDeviceRow> PairedDevices() => _rows;

        public void Apply(string clientId, string? name)
        {
            for (var i = 0; i < _rows.Length; i++)
            {
                if (_rows[i].ClientId == clientId)
                    _rows[i] = _rows[i] with { NameOverride = name };
            }
        }
    }

    /// <summary>
    /// A name writer that actually updates the source, so the refresh has something new to read.
    /// </summary>
    /// <remarks>
    /// <c>PairedDeviceRenameTests</c>'s writer only records calls, which is right for asserting what
    /// was asked of the store. Here the store's new state is the subject: the trust row has to pick
    /// the name up, so something must have put it there.
    /// </remarks>
    private sealed class StoreWritingRenamer(MutableSource source) : IPairedDeviceNameWriter
    {
        public void Rename(string clientId, string? typedName) => source.Apply(clientId, typedName);
    }

    /// <summary>Installs both a source and a name writer for the life of a test.</summary>
    private sealed class ScopedPairedDeviceServices : IDisposable
    {
        private readonly IServiceProvider? _saved = App.EmbeddedHostServices;

        public ScopedPairedDeviceServices(IPairedDeviceSource source, IPairedDeviceNameWriter? writer)
            => App.EmbeddedHostServices = new TwoServices(source, writer);

        public void Dispose() => App.EmbeddedHostServices = _saved;

        private sealed class TwoServices(IPairedDeviceSource source, IPairedDeviceNameWriter? writer)
            : IServiceProvider
        {
            public object? GetService(Type serviceType)
            {
                if (serviceType == typeof(IPairedDeviceSource)) return source;
                if (serviceType == typeof(IPairedDeviceNameWriter)) return writer;
                return null;
            }
        }
    }

    /// <summary>
    /// Installs a paired-device source for the life of a test, and restores what was there before.
    /// </summary>
    /// <remarks>
    /// The view model resolves through the static containers, so a test that installed one and did
    /// not put it back would change what every later test in the assembly sees. Parallel execution is
    /// disabled assembly-wide, so save/restore is enough.
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

    /// <summary>The trust-row template alone, so assertions cannot be satisfied from elsewhere.</summary>
    private static string TrustRowTemplate()
    {
        var flattened = Regex.Replace(
            File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "SettingsView.axaml")),
            @"\s+", " ");

        var match = Regex.Match(
            flattened,
            "<DataTemplate x:DataType=\"vm:FileTrustDeviceItem\">.*?</DataTemplate>");

        match.Success.Should().BeTrue("SettingsView should still have a FileTrustDeviceItem row template");
        return match.Value;
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
