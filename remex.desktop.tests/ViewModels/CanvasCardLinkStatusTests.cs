using System;
using System.IO;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// The Sensors Connection card must not read "Connected" under "No phone connected" (RemEx-pp4cm.24):
/// it shares Home's rule through <see cref="PhonePresenceText"/>.
/// </summary>
public sealed class CanvasCardLinkStatusTests : IDisposable
{
    private readonly ShellConnectionState _savedState = PhonePresenceMonitor.Instance.State;
    private readonly ConnectionViewModel _connection = new();
    private readonly CanvasCardViewModel _card;

    public CanvasCardLinkStatusTests()
    {
        _card = new CanvasCardViewModel { CardType = "Connection", Connection = _connection };
    }

    public void Dispose()
    {
        _card.Detach();
        PhonePresenceMonitor.Instance.State = _savedState;
    }

    [Fact]
    public void LinkUpWithNoPhoneSaysItIsWaiting()
    {
        PhonePresenceMonitor.Instance.State = ShellConnectionState.NoPhone;
        _connection.IsConnected = true;

        _card.LinkStatusText.Should().Be(LocalizationService.Instance["Home_WaitingForPhone"]);
    }

    [Fact]
    public void LinkUpWithAPhoneShowsTheLinksOwnStatus()
    {
        PhonePresenceMonitor.Instance.State = ShellConnectionState.PhoneAttached;
        _connection.IsConnected = true;

        _card.LinkStatusText.Should().Be(_connection.StatusText);
    }

    [Fact]
    public void LinkDownShowsTheLinksOwnStatus()
    {
        PhonePresenceMonitor.Instance.State = ShellConnectionState.NoPhone;
        _connection.IsConnected = false;

        _card.LinkStatusText.Should().Be(_connection.StatusText);
    }

    [Fact]
    public void ThePhoneArrivingOrTheLinkChangingRaisesTheLine()
    {
        PhonePresenceMonitor.Instance.State = ShellConnectionState.NoPhone;
        _connection.IsConnected = true;
        var raised = 0;
        _card.PropertyChanged += (_, e) => raised += e.PropertyName == nameof(CanvasCardViewModel.LinkStatusText) ? 1 : 0;

        PhonePresenceMonitor.Instance.State = ShellConnectionState.PhoneAttached;
        _connection.IsConnected = false;

        raised.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void TheConnectionCardBindsTheReconciledLine()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "CanvasView.axaml"));
        xaml.Should().Contain("{Binding LinkStatusText}");
        xaml.Should().NotContain("{Binding Connection.StatusText}");
    }

    private static string RepoRoot([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));
}
