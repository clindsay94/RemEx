using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
/// The shared busy affordance's skeleton-rows mode needs a flag to bind to over the File-Sharing
/// Trust card's first fill and every manual refresh (RemEx-kjdi §2) — before this bead, a refresh
/// gave no feedback at all between the click and the list repainting.
/// </summary>
public sealed class SettingsTrustListBusyFlagTests : IDisposable
{
    // DISPOSED IN Dispose() — see FileTrustDisplayNameTests' NewSettingsViewModel for why: the
    // redirected dashboard_layout.json is shared across this test assembly (RemEx-8y3qy).
    private DashboardLayoutService? _layoutService;

    public void Dispose() => _layoutService?.Dispose();

    private SettingsViewModel NewSettingsViewModel()
    {
        var savefile = new RemexSavefileService(
            _layoutService = new DashboardLayoutService(new ThemeService()),
            Mock.Of<ILauncherStorageService>(),
            new FileTransferRootSettingsService(),
            Mock.Of<IDashboardProfileStorageService>());

        return new SettingsViewModel(
            null!, new ConnectionViewModel(), null!, null!, savefile,
            new SensorAlertStore(), new SensorAlertTracker(), null!);
    }

    [Fact]
    public async Task IsLoadingTrustedDevices_IsTrueDuringTheRefreshAndFalseAfter()
    {
        var vm = NewSettingsViewModel();
        var gate = new TaskCompletionSource<IReadOnlyList<FileTrustRecord>>();
        var mock = new Mock<IFileTrustService>();
        mock.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>())).Returns(gate.Task);
        vm.FileTrustServiceForTests = mock.Object;

        var refresh = vm.RefreshTrustedDevicesCommand.ExecuteAsync(null);
        vm.IsLoadingTrustedDevices.Should().BeTrue("the flag flips before the service call is even awaited");

        gate.SetResult(Array.Empty<FileTrustRecord>());
        await refresh;

        vm.IsLoadingTrustedDevices.Should().BeFalse();
    }

    /// <summary>
    /// Forces the one failure path this VM's own seam makes reachable: <see cref="IFileTrustService.GetAllAsync"/>
    /// throwing. Proves the flag resets in the <c>finally</c>, not only when the try body succeeds.
    /// </summary>
    [Fact]
    public async Task IsLoadingTrustedDevices_IsFalseAfterTheServiceThrows()
    {
        var vm = NewSettingsViewModel();
        var mock = new Mock<IFileTrustService>();
        mock.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("simulated trust-service failure"));
        vm.FileTrustServiceForTests = mock.Object;

        await vm.RefreshTrustedDevicesCommand.ExecuteAsync(null);

        vm.IsLoadingTrustedDevices.Should().BeFalse("a thrown exception must still clear the busy flag");
    }
}
