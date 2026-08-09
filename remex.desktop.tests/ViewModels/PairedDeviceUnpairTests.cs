using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using Remex.Core.Models;
using Remex.Core.Services;
using Remex.Core.Services.FileTransfer;
using Remex.Desktop.Services;
using Remex.Desktop.Services.Backup;
using Remex.Desktop.Services.FileTransfer;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// Unpairing a device from the Settings card, and what has to be true before it happens
/// (RemEx-5lb90).
/// </summary>
/// <remarks>
/// This is the only action on the card that cannot be undone — the phone must pair again with a new
/// PIN, because the credential is gone. So the interesting tests are the ones about NOT doing it.
/// </remarks>
public class PairedDeviceUnpairTests
{
    [Fact]
    public async Task ConfirmingActuallyRevokes()
    {
        // THE JOIN. A command that resolves nothing produces a button that appears to work and
        // changes nothing, and Avalonia reports neither.
        var revoker = new RecordingRevoker();
        using var _ = new ScopedServices(new FakeSource([Row("phone-a")]), revoker);

        var vm = NewSettingsViewModel();
        vm.OnConfirmationRequested = (_, _, _) => Task.FromResult(true);
        vm.RefreshPairedDevices();

        await vm.UnpairDeviceCommand.ExecuteAsync(vm.PairedDevices[0]);

        revoker.Revoked.Should().ContainSingle().Which.Should().Be("phone-a");
    }

    [Fact]
    public async Task DecliningTheConfirmationRevokesNothing()
    {
        var revoker = new RecordingRevoker();
        using var _ = new ScopedServices(new FakeSource([Row("phone-a")]), revoker);

        var vm = NewSettingsViewModel();
        vm.OnConfirmationRequested = (_, _, _) => Task.FromResult(false);
        vm.RefreshPairedDevices();

        await vm.UnpairDeviceCommand.ExecuteAsync(vm.PairedDevices[0]);

        revoker.Revoked.Should().BeEmpty();
    }

    [Fact]
    public async Task WithNoDialogWiredItFailsCLOSED()
    {
        // FAILS CLOSED, matching every other confirmed action in this class. An unwired view model,
        // or a view with no visible parent window, must DECLINE rather than revoke unconfirmed —
        // this is the one action here with no way back.
        var revoker = new RecordingRevoker();
        using var _ = new ScopedServices(new FakeSource([Row("phone-a")]), revoker);

        var vm = NewSettingsViewModel();
        vm.OnConfirmationRequested = null;
        vm.RefreshPairedDevices();

        await vm.UnpairDeviceCommand.ExecuteAsync(vm.PairedDevices[0]);

        revoker.Revoked.Should().BeEmpty(
            "an unconfirmed revocation is the worst failure mode this card has");
    }

    [Fact]
    public async Task TheConfirmationNamesTheDeviceAndSaysWhatItCosts()
    {
        // "Remove" on its own reads like tidying a list. A user who reads it that way is surprised
        // later when their phone stops connecting, and by then there is nothing to undo.
        var seen = new List<(string Title, string Message, string Button)>();
        using var _ = new ScopedServices(new FakeSource([Row("phone-a", "Study Phone")]), new RecordingRevoker());

        var vm = NewSettingsViewModel();
        vm.OnConfirmationRequested = (title, message, button) =>
        {
            seen.Add((title, message, button));
            return Task.FromResult(false);
        };
        vm.RefreshPairedDevices();

        await vm.UnpairDeviceCommand.ExecuteAsync(vm.PairedDevices[0]);

        seen.Should().ContainSingle();
        seen[0].Message.Should().Contain("Study Phone",
            "a confirmation that does not name the device invites unpairing the wrong one");
        seen[0].Message.Should().NotContain("{0}", "the device name must be formatted in, not left as a placeholder");
    }

    [Fact]
    public async Task WithNoRevokerNothingHappensAndNothingThrows()
    {
        using var _ = new ScopedServices(new FakeSource([Row("phone-a")]), revoker: null);

        var vm = NewSettingsViewModel();
        vm.OnConfirmationRequested = (_, _, _) => Task.FromResult(true);
        vm.RefreshPairedDevices();

        vm.CanUnpairDevices.Should().BeFalse();
        var act = async () => await vm.UnpairDeviceCommand.ExecuteAsync(vm.PairedDevices.FirstOrDefault());
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task AFailedRevocationIsREPORTED_NotSwallowedAndNotFatal()
    {
        // THE HALF-REVOCATION. The revoker throws when a teardown failed, and there are two ways to
        // get this wrong: let it escape the AsyncRelayCommand, which kills the app on the dispatcher,
        // or swallow it, which is worse — the row vanishes from the rebuilt list, the user reads that
        // as success, and the pairing is still on disk after a restart. Both sibling confirmed
        // actions in this class report; this one has to as well.
        var revoker = new RecordingRevoker(new InvalidOperationException("the store is locked"));
        using var _ = new ScopedServices(new FakeSource([Row("phone-a")]), revoker);

        var vm = NewSettingsViewModel();
        vm.OnConfirmationRequested = (_, _, _) => Task.FromResult(true);
        vm.RefreshPairedDevices();

        var act = async () => await vm.UnpairDeviceCommand.ExecuteAsync(vm.PairedDevices[0]);

        await act.Should().NotThrowAsync("an unhandled exception here takes the whole app down");
        vm.SavedStatus.Should().Contain("the store is locked",
            "a revocation that only half-happened must say so, not vanish a row and look like success");
    }

    [Fact]
    public async Task ASuccessfulRevocationSaysSo()
    {
        var revoker = new RecordingRevoker();
        using var _ = new ScopedServices(new FakeSource([Row("phone-a")]), revoker);

        var vm = NewSettingsViewModel();
        vm.OnConfirmationRequested = (_, _, _) => Task.FromResult(true);
        vm.RefreshPairedDevices();

        await vm.UnpairDeviceCommand.ExecuteAsync(vm.PairedDevices[0]);

        vm.SavedStatus.Should().Be(
            Remex.Desktop.Services.LocalizationService.Instance["Settings_DeviceUnpaired"]);
    }

    [Fact]
    public async Task AFailureThatLEAVESTHEPAIRINGSaysSomethingDifferent()
    {
        // TWO FAILURES, TWO ACTIONS (RemEx-pynli). A record store that could not be written leaves a
        // stale name behind: invisible, nothing to do. The credential store failing removes the client
        // from memory and then fails to persist, so the device is unpaired now and still on disk — it
        // is back the next time RemEx starts, and the user needs to know to unpair it again. Reporting
        // both as "something went wrong" tells the second user nothing they can act on.
        var statuses = new List<string>();

        foreach (var mayReturn in new[] { false, true })
        {
            // CONSTRUCTED EXACTLY AS PairedDeviceRevoker DOES, which is the whole difference between
            // this guard and the inert one it replaces (review). The first version passed the IO
            // reason as the OUTER message too — something production never does — so the
            // "reason survives" assertion below was checking the test's own fixture. Production's
            // outer message is a fixed summary; the reason exists only in Failures.
            var revoker = new RecordingRevoker(new PairedDeviceRevocationException(
                "One or more paired-device teardowns failed; the revocation is incomplete.",
                mayReturn, [new IOException("the store is locked")]));
            using var scope = new ScopedServices(new FakeSource([Row("phone-a")]), revoker);

            var vm = NewSettingsViewModel();
            vm.OnConfirmationRequested = (_, _, _) => Task.FromResult(true);
            vm.RefreshPairedDevices();

            await vm.UnpairDeviceCommand.ExecuteAsync(vm.PairedDevices[0]);
            statuses.Add(vm.SavedStatus);
        }

        // DIRECTIONAL, because every earlier version of these assertions was symmetric (review).
        // "They differ" and "both carry the reason" are satisfied just as happily by the two messages
        // SWAPPED — which is the single most plausible mutation of a resource key chosen inline in a
        // ternary, and the outcome it produces is the exact one this bead exists to prevent: the user
        // whose phone will authenticate again after a restart is told nothing to do, and the user
        // whose pairing is gone is sent to unpair a device that no longer exists.
        var reason = "the store is locked";
        statuses[1].Should().Be(
            string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                Remex.Desktop.Services.LocalizationService.Instance["Settings_UnpairFailedPairingReturns"],
                reason),
            "the pairing survives a restart, so the message must say so and say what to do");
        statuses[0].Should().Be(
            string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                Remex.Desktop.Services.LocalizationService.Instance["Status_ErrorFormat"],
                reason),
            "the pairing is gone; sending this user to unpair again would be a wild goose chase");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheTrustedDevicesCardIsRebuiltToo(bool revokeFails)
    {
        // THE SAME DEVICE APPEARS TWICE ON THIS PAGE — once as a pairing, once under Trusted Devices
        // if it has file-access grants — and revoking clears both stores. Rebuilding only the first
        // leaves the phone listed below with a "revoke trust" button for a pairing that is gone.
        //
        // COUNTED AT THE SEAM, because without a trust service wired the whole refresh returns at its
        // first line and the call could be deleted with all eight other tests still green (review).
        // The failure case matters more than the success one: a partial teardown is exactly when the
        // two cards can disagree.
        var revoker = new RecordingRevoker(
            revokeFails ? new InvalidOperationException("the store is locked") : null);
        using var _ = new ScopedServices(new FakeSource([Row("phone-a")]), revoker);

        var vm = NewSettingsViewModel();
        var trust = new CountingTrustService();
        vm.FileTrustServiceForTests = trust;
        vm.OnConfirmationRequested = (_, _, _) => Task.FromResult(true);
        vm.RefreshPairedDevices();
        trust.GetAllCalls.Should().Be(0, "nothing has asked for the trust list yet");

        await vm.UnpairDeviceCommand.ExecuteAsync(vm.PairedDevices[0]);

        trust.GetAllCalls.Should().Be(1,
            "the trusted-devices card must be rebuilt after a revoke, including one that failed");
    }

    [Fact]
    public void TheUnpairButtonIsBoundInTheRowTemplate()
    {
        // Avalonia binding failures are silent, so a view-model test says nothing about the button.
        var flattened = Regex.Replace(
            File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "SettingsView.axaml")),
            @"\s+", " ");

        var template = Regex.Match(
            flattened, "<DataTemplate x:DataType=\"vm:PairedDeviceItem\">.*?</DataTemplate>",
            RegexOptions.Singleline);
        template.Success.Should().BeTrue("the paired-device row template must exist to be checked");

        var buttons = Regex.Matches(template.Value, "<Button[^>]*UnpairDeviceCommand[^>]*>")
            .Select(m => m.Value)
            .ToArray();

        buttons.Should().HaveCount(1,
            "the row carries exactly one unpair button; none means the scan has stopped looking");
        buttons[0].Should().Contain("CommandParameter=\"{Binding}\"",
            "without the row as the parameter the command has no device to revoke");
        buttons[0].Should().Contain("CanUnpairDevices",
            "the button must be gated on the revoker resolving, or it is offered where it cannot work");
    }

    // ── Harness ────────────────────────────────────────────────────────────────

    private static PairedDeviceRow Row(string id, string? name = null)
        => new(id, DeviceName: null, NameOverride: name, null, null, IsOnline: false);

    private static SettingsViewModel NewSettingsViewModel()
    {
        var savefile = new RemexSavefileService(
            new DashboardLayoutService(new ThemeService()),
            Mock.Of<ILauncherStorageService>(),
            new FileTransferRootSettingsService(),
            Mock.Of<IDashboardProfileStorageService>());

        return new SettingsViewModel(null!, new ConnectionViewModel(), null!, null!, savefile);
    }

    private sealed class FakeSource(IReadOnlyList<PairedDeviceRow> rows) : IPairedDeviceSource
    {
        public IReadOnlyList<PairedDeviceRow> PairedDevices() => rows;
    }

    private sealed class RecordingRevoker(Exception? throws = null) : IPairedDeviceRevoker
    {
        public List<string> Revoked { get; } = [];

        public Task RevokeAsync(string clientId, System.Threading.CancellationToken ct)
        {
            Revoked.Add(clientId);
            return throws is null ? Task.CompletedTask : Task.FromException(throws);
        }
    }

    /// <summary>Counts the one call the unpair path is supposed to make; everything else is inert.</summary>
    private sealed class CountingTrustService : IFileTrustService
    {
        public int GetAllCalls { get; private set; }

        public Task<IReadOnlyList<FileTrustRecord>> GetAllAsync(CancellationToken ct)
        {
            GetAllCalls++;
            return Task.FromResult<IReadOnlyList<FileTrustRecord>>([]);
        }

        public Task<FileTrustRecord?> GetTrustAsync(string clientId, CancellationToken ct)
            => Task.FromResult<FileTrustRecord?>(null);

        public Task<bool> IsFullBrowseGrantedAsync(string clientId, CancellationToken ct) => Task.FromResult(false);

        public Task<bool> IsAutoAcceptIncomingAsync(string clientId, CancellationToken ct) => Task.FromResult(false);

        public Task SetFullBrowseGrantedAsync(string clientId, bool granted, CancellationToken ct) => Task.CompletedTask;

        public Task SetAutoAcceptIncomingAsync(string clientId, bool autoAccept, CancellationToken ct) => Task.CompletedTask;

        public Task RevokeAsync(string clientId, CancellationToken ct) => Task.CompletedTask;

        public Task<FileConsentDecision> RequestConsentAsync(
            string clientId, FileConsentRequest request, CancellationToken ct)
            => throw new NotSupportedException("the unpair tests never prompt for consent");

        public bool TryResolveRemoteConsent(string? clientId, string? consentId, bool granted, bool remember) => false;

        public void ResolveConsent(string consentId, bool granted, bool remember)
            => throw new NotSupportedException("the unpair tests never prompt for consent");

        public event Action<FileConsentPrompt>? ConsentRequested { add { } remove { } }
    }

    private sealed class ScopedServices : IDisposable
    {
        private readonly IServiceProvider? _saved = App.EmbeddedHostServices;

        public ScopedServices(IPairedDeviceSource source, IPairedDeviceRevoker? revoker)
            => App.EmbeddedHostServices = new Provider(source, revoker);

        public void Dispose() => App.EmbeddedHostServices = _saved;

        private sealed class Provider(IPairedDeviceSource source, IPairedDeviceRevoker? revoker)
            : IServiceProvider
        {
            public object? GetService(Type serviceType)
            {
                if (serviceType == typeof(IPairedDeviceSource)) return source;
                if (serviceType == typeof(IPairedDeviceRevoker)) return revoker;
                return null;
            }
        }
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
