using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Moq;
using Remex.Core.Services;
using Remex.Desktop.Services;
using Remex.Desktop.Services.Backup;
using Remex.Desktop.Services.FileTransfer;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// After a phone pairs, Settings › Connection &amp; pairing says which phone it was (RemEx-172ba). UI
/// state only: success is read off the host's paired list when the PIN goes away, because PinCleared
/// does not say whether the PIN was used, ran out or was cancelled.
/// </summary>
public sealed class PairingSuccessLineTests : IDisposable
{
    private static readonly DateTimeOffset Earlier = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly IServiceProvider? _savedHost = App.EmbeddedHostServices;
    private readonly FakePairedDeviceSource _source = new();
    private DateTimeOffset _clock = Now;
    private DashboardLayoutService? _layoutService;

    public PairingSuccessLineTests() => App.EmbeddedHostServices = new SingleService(_source);

    public void Dispose()
    {
        App.EmbeddedHostServices = _savedHost;
        _layoutService?.Dispose();
    }

    private ConnectionViewModel NewConnection() => new() { PairingClock = () => _clock };

    private SettingsViewModel NewSettings(ConnectionViewModel connection, Func<TimeSpan, Task>? delay = null)
    {
        var savefile = new RemexSavefileService(
            _layoutService = new DashboardLayoutService(new ThemeService()),
            Mock.Of<ILauncherStorageService>(),
            new FileTransferRootSettingsService());
        return new SettingsViewModel(null!, connection, null!, null!, savefile,
            new SensorAlertStore(), new SensorAlertTracker(), null!)
        {
            PairingWatchDelay = delay ?? (_ => Task.CompletedTask),
        };
    }

    private void ShowPin(ConnectionViewModel connection)
    {
        connection.ActivePairingPin = "482913";
        connection.ActivePairingExpiresAt = _clock.AddMinutes(3);
        connection.ShowPairingPin = true;
        connection.ShowQrCode = true;
    }

    [Fact]
    public void ANewPhoneAppearingWhenThePinIsUsed_IsNamed()
    {
        _source.Rows = [new PairedDeviceRow("phone-a", "Work Pixel", null, Earlier, Earlier, IsOnline: false)];
        var connection = NewConnection();
        var vm = NewSettings(connection);

        ShowPin(connection);
        _source.Rows = [.. _source.Rows, new PairedDeviceRow("phone-b", "Study Galaxy", null, Now, Now, IsOnline: true)];
        connection.ClearActivePairingPinForTests();

        vm.JustPairedDeviceName.Should().Be("Study Galaxy");
        vm.HasJustPairedDevice.Should().BeTrue();
        connection.ShowQrCode.Should().BeFalse("the QR closes when the PIN is used");
        connection.ShowPairingPin.Should().BeFalse("and so does the PIN panel");
        vm.PairedDevices.Select(d => d.ClientId).Should().Contain("phone-b", "the paired list refreshes too");
    }

    [Fact]
    public void AKnownPhonePairingAgain_IsNamed()
    {
        _source.Rows = [new PairedDeviceRow("phone-a", "Work Pixel", "Desk phone", Earlier, Earlier, IsOnline: false, Earlier)];
        var connection = NewConnection();
        var vm = NewSettings(connection);

        ShowPin(connection);
        _source.Rows = [new PairedDeviceRow("phone-a", "Work Pixel", "Desk phone", Earlier, Now, IsOnline: true, Now)];
        connection.ClearActivePairingPinForTests();

        vm.JustPairedDeviceName.Should().Be("Desk phone", "the person's own name for it wins, as on the paired list");
    }

    [Fact]
    public void ACancelledPin_ThenAKnownPhoneReconnecting_ShowsNoLine()
    {
        // R1: the PIN goes away without a pairing (cancelled, or the phone gave up), and inside the watch
        // window a phone that was already paired reconnects. Its last-seen moves; its last-paired does not.
        _source.Rows = [new PairedDeviceRow("phone-a", "Work Pixel", null, Earlier, Earlier, IsOnline: false, Earlier)];
        var connection = NewConnection();
        var looks = 0;
        var vm = NewSettings(connection, _ =>
        {
            if (++looks == 2)
                _source.Rows = [new PairedDeviceRow("phone-a", "Work Pixel", null, Earlier, Now, IsOnline: true, Earlier)];
            return Task.CompletedTask;
        });

        ShowPin(connection);
        connection.ClearActivePairingPinForTests();

        vm.HasJustPairedDevice.Should().BeFalse("a reconnect is not a pairing");
        looks.Should().Be(SettingsViewModel.PairingWatchAttempts, "precondition: the reconnect landed inside the watch");
    }

    [Fact]
    public void APhoneThatShowsUpOnALaterLook_IsStillNamed()
    {
        // PinCleared reaches the UI before the host has recorded the device.
        _source.Rows = [];
        var connection = NewConnection();
        var looks = 0;
        var vm = NewSettings(connection, _ =>
        {
            if (++looks == 3)
                _source.Rows = [new PairedDeviceRow("phone-b", "Study Galaxy", null, Now, Now, IsOnline: true)];
            return Task.CompletedTask;
        });

        ShowPin(connection);
        connection.ClearActivePairingPinForTests();

        vm.JustPairedDeviceName.Should().Be("Study Galaxy");
    }

    [Fact]
    public void APinThatClearsWithNothingNew_ShowsNoLine()
    {
        _source.Rows = [new PairedDeviceRow("phone-a", "Work Pixel", null, Earlier, Earlier, IsOnline: false)];
        var connection = NewConnection();
        var looks = 0;
        var vm = NewSettings(connection, _ => { looks++; return Task.CompletedTask; });

        ShowPin(connection);
        connection.ClearActivePairingPinForTests();

        vm.HasJustPairedDevice.Should().BeFalse();
        looks.Should().Be(SettingsViewModel.PairingWatchAttempts, "it gives up after its window rather than polling forever");
    }

    [Fact]
    public void AnExpiredPin_ShowsNoLine_EvenIfAPhoneReconnects()
    {
        _source.Rows = [new PairedDeviceRow("phone-a", "Work Pixel", null, Earlier, Earlier, IsOnline: false)];
        var connection = NewConnection();
        var looks = 0;
        var vm = NewSettings(connection, _ => { looks++; return Task.CompletedTask; });

        ShowPin(connection);
        _clock = _clock.AddMinutes(4);
        connection.OnPairingExpiryTick();
        connection.IsPairingPinExpired.Should().BeTrue("precondition: the PIN ran out");
        _source.Rows = [new PairedDeviceRow("phone-a", "Work Pixel", null, Earlier, _clock, IsOnline: true)];
        connection.ClearActivePairingPinForTests();

        vm.HasJustPairedDevice.Should().BeFalse("an expired PIN cannot have paired anything");
        looks.Should().Be(0, "nothing is watched for");
    }

    [Fact]
    public void ANewPin_ClearsTheLastSuccessLine()
    {
        _source.Rows = [];
        var connection = NewConnection();
        var vm = NewSettings(connection);
        ShowPin(connection);
        _source.Rows = [new PairedDeviceRow("phone-b", "Study Galaxy", null, Now, Now, IsOnline: true)];
        connection.ClearActivePairingPinForTests();
        vm.HasJustPairedDevice.Should().BeTrue("precondition");

        ShowPin(connection);

        vm.HasJustPairedDevice.Should().BeFalse();
    }

    [Fact]
    public void WithNoHost_NothingIsArmed_SoNoPhoneIsCalledNew()
    {
        App.EmbeddedHostServices = null;
        var connection = NewConnection();
        var vm = NewSettings(connection);

        ShowPin(connection);
        App.EmbeddedHostServices = new SingleService(_source);
        _source.Rows = [new PairedDeviceRow("phone-a", "Work Pixel", null, Earlier, Earlier, IsOnline: true)];
        connection.ClearActivePairingPinForTests();

        vm.HasJustPairedDevice.Should().BeFalse("an empty baseline would make every already-paired phone look new");
    }

    [Fact]
    public void FindNewlyPaired_PrefersANewIdOverALastPairedBump_AndIgnoresALastSeenBump()
    {
        var before = new[]
        {
            new PairedDeviceRow("phone-a", null, null, Earlier, Earlier, false, Earlier),
        };
        var after = new[]
        {
            new PairedDeviceRow("phone-a", null, null, Earlier, Now, true, Now),
            new PairedDeviceRow("phone-b", null, null, Now, Now, true, Now),
        };
        var reconnectedOnly = new[]
        {
            new PairedDeviceRow("phone-a", null, null, Earlier, Now, true, Earlier),
        };

        SettingsViewModel.FindNewlyPaired(before, after)!.Value.ClientId.Should().Be("phone-b");
        SettingsViewModel.FindNewlyPaired(before, before).Should().BeNull();
        SettingsViewModel.FindNewlyPaired(before, reconnectedOnly).Should().BeNull();
    }

    [Fact]
    public void TheLineSitsUnderThePinPanel_AndIsLocalizedThroughAFormatBinding()
    {
        var markup = Regex.Replace(File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "SettingsView.axaml")), @"\s+", " ");

        var pin = markup.IndexOf("<views:PairingPinPanelView", StringComparison.Ordinal);
        var line = markup.IndexOf(@"IsVisible=""{Binding HasJustPairedDevice}""", StringComparison.Ordinal);
        pin.Should().BeGreaterThan(0);
        line.Should().BeGreaterThan(pin, "the success line takes the place the panels just left");
        markup.Should().Contain(@"<Binding Path=""[Settings_PairingSucceeded]"" Source=""{x:Static svc:LocalizationService.Instance}""/> <Binding Path=""JustPairedDeviceName""/>",
            "a format binding, not string.Format in a bound property, so a language switch re-renders it");
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));

    private sealed class FakePairedDeviceSource : IPairedDeviceSource
    {
        public IReadOnlyList<PairedDeviceRow> Rows { get; set; } = [];
        public IReadOnlyList<PairedDeviceRow> PairedDevices() => Rows;
    }

    private sealed class SingleService(IPairedDeviceSource source) : IServiceProvider
    {
        public object? GetService(Type serviceType)
            => serviceType == typeof(IPairedDeviceSource) ? source : null;
    }
}
