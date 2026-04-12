using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Remex.Client.ViewModels;
using Remex.Core;
using Remex.Core.Models;
using Remex.Core.Services.Network;
using Xunit;

namespace Remex.Client.Tests.ViewModels;

public class ConnectionViewModelTests : IDisposable
{
    private readonly Mock<IMdnsDiscoveryService> _mockDiscoveryService;
    private readonly Mock<ILogger<ConnectionViewModel>> _mockLogger;
    private ConnectionViewModel _viewModel;

    public ConnectionViewModelTests()
    {
        _mockDiscoveryService = new Mock<IMdnsDiscoveryService>();
        _mockLogger = new Mock<ILogger<ConnectionViewModel>>();
        _viewModel = new ConnectionViewModel(_mockDiscoveryService.Object, null, _mockLogger.Object);
    }

    public void Dispose() => _viewModel?.Dispose();

    [Fact]
    public void Constructor_WithAllNullDependencies_ShouldNotThrow()
    {
        var vm = new ConnectionViewModel(null, null, null);
        vm.Should().NotBeNull();
        vm.IsConnected.Should().BeFalse();
        vm.Dispose();
    }

    [Theory]
    [InlineData("ws://localhost:5005/ws")]
    [InlineData("ws://192.168.1.100:5005/ws")]
    public void HostAddress_WhenSetToValidWebSocketUri_ShouldPersist(string validAddress)
    {
        _viewModel.HostAddress = validAddress;
        _viewModel.HostAddress.Should().Be(validAddress);
    }

    [Fact]
    public void InitialState_ShouldBeDisconnected()
    {
        _viewModel.IsConnected.Should().BeFalse();
        _viewModel.IsConnecting.Should().BeFalse();
        _viewModel.IsAutoReconnecting.Should().BeFalse();
    }

    [Fact]
    public void CanConnect_WhenDisconnected_ShouldReturnTrue()
    {
        _viewModel.IsConnected = false;
        _viewModel.IsConnecting = false;
        _viewModel.ConnectCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void CanConnect_WhenAlreadyConnected_ShouldReturnFalse()
    {
        _viewModel.IsConnected = true;
        _viewModel.ConnectCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void CanDisconnect_WhenConnected_ShouldReturnTrue()
    {
        _viewModel.IsConnected = true;
        _viewModel.DisconnectCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void CanDisconnect_WhenDisconnected_ShouldReturnFalse()
    {
        _viewModel.IsConnected = false;
        _viewModel.IsConnecting = false;
        _viewModel.DisconnectCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void IsConnected_WhenChanged_ShouldNotifyCommandsCanExecuteChanged()
    {
        var connectChangedCount = 0;
        _viewModel.ConnectCommand.CanExecuteChanged += (s, e) => connectChangedCount++;
        _viewModel.IsConnected = true;
        connectChangedCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public void LatencyHistory_ShouldBeInitiallyEmpty()
    {
        _viewModel.LatencyHistory.Should().NotBeNull();
        _viewModel.LatencyHistory.Should().BeEmpty();
    }

    [Fact]
    public void LatencyText_ShouldDefaultToDash()
    {
        _viewModel.LatencyText.Should().Be("—");
    }

    [Fact]
    public void AverageLatency_ShouldDefaultToZero()
    {
        _viewModel.AverageLatency.Should().Be(0);
    }

    [Fact]
    public void MaxLatency_ShouldDefaultToZero()
    {
        _viewModel.MaxLatency.Should().Be(0);
    }

    [Fact]
    public void HostCapabilities_WhenNull_SupportsRemoteDesktopShouldBeTrue()
    {
        _viewModel.HostCapabilities = null;
        _viewModel.SupportsRemoteDesktop.Should().BeTrue();
    }

    [Fact]
    public void HostCapabilities_WhenSetWithRemoteDesktopDisabled_ShouldReturnFalse()
    {
        _viewModel.HostCapabilities = new HostCapabilities { SupportsRemoteDesktop = false };
        _viewModel.SupportsRemoteDesktop.Should().BeFalse();
    }

    [Fact]
    public void HostCapabilities_WhenChanged_ShouldNotifyDependentProperties()
    {
        var changedProperties = new List<string>();
        _viewModel.PropertyChanged += (s, e) => { if (e.PropertyName != null) changedProperties.Add(e.PropertyName); };
        _viewModel.HostCapabilities = new HostCapabilities { Platform = "Windows", RuntimeMode = "service" };
        changedProperties.Should().Contain(nameof(ConnectionViewModel.SupportsRemoteDesktop));
        changedProperties.Should().Contain(nameof(ConnectionViewModel.HostRuntimeSummary));
    }

    [Fact]
    public void CanSendPing_WhenNotConnected_ShouldReturnFalse()
    {
        _viewModel.IsConnected = false;
        _viewModel.SendPingCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void CanSendPing_WhenConnected_ShouldReturnTrue()
    {
        _viewModel.IsConnected = true;
        _viewModel.SendPingCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void Dispose_ShouldNotThrowOnDoubleDispose()
    {
        var vm = new ConnectionViewModel(_mockDiscoveryService.Object, null, _mockLogger.Object);
        var act = () => { vm.Dispose(); vm.Dispose(); };
        act.Should().NotThrow();
    }

    [Fact]
    public void Processes_ShouldBeInitializedEmpty()
    {
        _viewModel.Processes.Should().NotBeNull();
        _viewModel.Processes.Should().BeEmpty();
    }

    [Fact]
    public async Task RequestProcessListAsync_WhenNotConnected_ShouldNotThrow()
    {
        _viewModel.IsConnected = false;
        Func<Task> act = async () => await _viewModel.RequestProcessListAsync();
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void LauncherEntriesReceived_EventSubscription_ShouldNotThrow()
    {
        var act = () => { _viewModel.LauncherEntriesReceived += _ => { }; };
        act.Should().NotThrow();
    }

    [Fact]
    public void DiscoverHostsCommand_ShouldExist()
    {
        _viewModel.DiscoverHostsCommand.Should().NotBeNull();
    }

    [Fact]
    public async Task DiscoverHostsCommand_WhenNoDiscoveryService_ShouldNotCrash()
    {
        var vmWithoutDiscovery = new ConnectionViewModel(null, null, null);
        await vmWithoutDiscovery.DiscoverHostsCommand.ExecuteAsync(null);
        vmWithoutDiscovery.StatusText.Should().NotBeNullOrEmpty();
        vmWithoutDiscovery.Dispose();
    }

    [Fact]
    public async Task DiscoverHostsCommand_WhenNoHostsFound_ShouldUpdateStatusText()
    {
        _mockDiscoveryService
            .Setup(x => x.DiscoverHostsAsync(It.IsAny<TimeSpan>(), default))
            .ReturnsAsync(new List<string>());
        await _viewModel.DiscoverHostsCommand.ExecuteAsync(null);
        _viewModel.StatusText.Should().NotBeNullOrEmpty();
    }
}
