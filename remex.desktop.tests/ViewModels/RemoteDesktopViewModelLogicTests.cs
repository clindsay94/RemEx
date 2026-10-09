using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Remex.Core.Models;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// Decisions inside <see cref="RemoteDesktopViewModel"/> that no other test reaches: scale snapping, display-catalog
/// merging and selection, window-result application, and the host-event handlers (stream serial, errors, disconnect).
/// No socket: the host events are fed straight into the internal handlers.
/// </summary>
public sealed class RemoteDesktopViewModelLogicTests : IAsyncLifetime
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("remex-rd-logic-").FullName;
    private readonly ThemeService _theme = new() { PostToUiThread = action => action() };
    private ShellViewModel _shell = null!;
    private RemoteDesktopViewModel _vm = null!;

    public async Task InitializeAsync()
    {
        var layout = new DashboardLayoutService(Path.Combine(_tempDir, "dashboard_layout.json"), _theme);
        await layout.LoadAsync();
        _shell = new ShellViewModel(
            layout,
            _theme,
            new ConnectionViewModel(),
            new ServiceCollection().AddLogging()
                .AddSingleton<SensorAlertStore>()
                .AddSingleton<SensorAlertTracker>()
                .BuildServiceProvider());
        _shell.ProfileReplacedDispatch = run => run();
        _vm = new RemoteDesktopViewModel(new ConnectionViewModel(), _shell) { PostToUi = work => work() };
    }

    public Task DisposeAsync()
    {
        _vm.Dispose();
        _shell.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    // Host events are posted to the UI thread; run them inline, as FileTransferViewModel.PostToUi does in its tests.

    private static DesktopDisplayInfo Display(string id, string name, int left, int top, int w, int h, bool primary = false) =>
        new() { DisplayId = id, Name = name, Left = left, Top = top, Width = w, Height = h, IsPrimary = primary };

    private static DesktopDisplayCatalog Catalog(int version, bool virtualDesktop, params DesktopDisplayInfo[] displays) =>
        new()
        {
            DisplayListVersion = version,
            SupportedCaptureModes = virtualDesktop
                ? [DesktopCaptureMode.Monitor, DesktopCaptureMode.VirtualDesktop]
                : [DesktopCaptureMode.Monitor],
            Displays = displays,
        };

    // ── Scale ──

    [Theory]
    [InlineData(0.0, 0.25)]
    [InlineData(0.374, 0.25)]
    [InlineData(0.375, 0.50)]
    [InlineData(0.624, 0.50)]
    [InlineData(0.625, 0.75)]
    [InlineData(0.874, 0.75)]
    [InlineData(0.875, 1.0)]
    [InlineData(4.0, 1.0)]
    public void ASavedScale_SnapsToTheNearestOfTheFourOffered(double saved, double expected) =>
        RemoteDesktopViewModel.SnapScale(saved).Should().Be(expected);

    [Theory]
    [InlineData(0.25, 0)]
    [InlineData(0.5, 1)]
    [InlineData(0.6, 2)]
    [InlineData(0.75, 2)]
    [InlineData(1.0, 3)]
    public void TheScaleComboIndex_FollowsTheScale(double scale, int index)
    {
        _vm.Scale = scale;

        _vm.SelectedScaleIndex.Should().Be(index);
    }

    [Theory]
    [InlineData(0, 0.25)]
    [InlineData(3, 1.0)]
    [InlineData(-1, 0.5)]
    [InlineData(7, 0.5)]
    public void PickingAComboIndex_SetsTheScale_AndAnUnknownIndexFallsBackToHalf(int index, double scale)
    {
        _vm.SelectedScaleIndex = index;

        _vm.Scale.Should().Be(scale);
    }

    [Theory]
    [InlineData(1.0, false)]
    [InlineData(1.01, false)]
    [InlineData(1.02, true)]
    public void TheZoomedFlag_IgnoresRoundingNoiseAroundOne(double zoom, bool zoomed)
    {
        _vm.UpdateViewportZoom(zoom);

        _vm.IsViewportZoomed.Should().Be(zoomed);
    }

    // ── Display catalog ──

    [Fact]
    public void AllDisplays_SpansEveryMonitor_IncludingOnesLeftOfOrAboveThePrimary()
    {
        var catalog = Catalog(3, true,
            Display("L", "Left", -1920, -200, 1920, 1080),
            Display("P", "Main", 0, 0, 2560, 1440, primary: true));

        _vm.ApplyDisplayCatalog(catalog, null);

        var all = _vm.AvailableDisplayTargets[0];
        all.CaptureMode.Should().Be(DesktopCaptureMode.VirtualDesktop);
        all.DisplayId.Should().BeNull();
        (all.Width, all.Height).Should().Be((4480, 1640), "from x -1920 to 2560 and y -200 to 1440");
    }

    [Fact]
    public void WithoutVirtualDesktopSupport_OnlyTheMonitorsAreOffered_AndThePrimaryIsPicked()
    {
        var catalog = Catalog(1, false,
            Display("A", "Side", 1920, 0, 1920, 1080),
            Display("B", "Main", 0, 0, 1920, 1080, primary: true));

        _vm.ApplyDisplayCatalog(catalog, null);

        _vm.AvailableDisplayTargets.Should().OnlyContain(t => t.CaptureMode == DesktopCaptureMode.Monitor);
        _vm.SelectedDisplayTarget!.DisplayId.Should().Be("B");
        _vm.SelectedDisplayTarget.Label.Should().Be("Main - 1920x1080 (Primary)");
        _vm.AvailableDisplayTargets[0].Label.Should().Be("Side - 1920x1080");
        _vm.HasDisplayTargets.Should().BeTrue();
    }

    [Fact]
    public void ARefreshedCatalog_KeepsTheUsersMonitor_AndTakesTheNewListVersion()
    {
        _vm.ApplyDisplayCatalog(Catalog(1, true,
            Display("A", "Side", 1920, 0, 1920, 1080),
            Display("B", "Main", 0, 0, 1920, 1080, primary: true)), null);
        _vm.SelectedDisplayTarget = _vm.AvailableDisplayTargets.Single(t => t.DisplayId == "A");
        var previous = _vm.SelectedDisplayTarget;

        _vm.ApplyDisplayCatalog(Catalog(2, true,
            Display("A", "Side", 1920, 0, 1920, 1080),
            Display("B", "Main", 0, 0, 1920, 1080, primary: true)), previous);

        _vm.SelectedDisplayTarget!.DisplayId.Should().Be("A");
        _vm.SelectedDisplayTarget.DisplayListVersion.Should().Be(2, "a stale version makes the host reject the switch");
    }

    [Fact]
    public void ARefreshedCatalog_WithoutTheUsersMonitor_FallsBackToThePrimary()
    {
        _vm.ApplyDisplayCatalog(Catalog(1, false, Display("A", "Side", 1920, 0, 800, 600)), null);
        var previous = _vm.SelectedDisplayTarget;

        _vm.ApplyDisplayCatalog(Catalog(2, false,
            Display("B", "Main", 0, 0, 1920, 1080, primary: true),
            Display("C", "Other", 1920, 0, 800, 600)), previous);

        _vm.SelectedDisplayTarget!.DisplayId.Should().Be("B");
    }

    [Fact]
    public void AnAllDisplaysChoice_SurvivesARefresh()
    {
        var catalog = Catalog(1, true, Display("B", "Main", 0, 0, 1920, 1080, primary: true));
        _vm.ApplyDisplayCatalog(catalog, null);
        var previous = _vm.AvailableDisplayTargets.Single(t => t.CaptureMode == DesktopCaptureMode.VirtualDesktop);

        _vm.ApplyDisplayCatalog(catalog with { DisplayListVersion = 2 }, previous);

        _vm.SelectedDisplayTarget!.CaptureMode.Should().Be(DesktopCaptureMode.VirtualDesktop);
    }

    [Fact]
    public void Start_NeedsAConnectionAndADisplay_AndSwitchNeedsALiveStream()
    {
        // Swallow the connection-change refresh, which would otherwise go and ask a host for its displays.
        _vm.PostToUi = _ => { };
        _vm.Connection.IsConnected = true;
        _vm.StartStreamCommand.CanExecute(null).Should().BeFalse("no display has been chosen yet");

        _vm.ApplyDisplayCatalog(Catalog(1, false, Display("B", "Main", 0, 0, 1920, 1080, primary: true)), null);
        _vm.StartStreamCommand.CanExecute(null).Should().BeTrue();
        _vm.SwitchDisplayCommand.CanExecute(null).Should().BeFalse("there is nothing to switch before streaming");

        _vm.IsStreaming = true;
        _vm.StartStreamCommand.CanExecute(null).Should().BeFalse();
        _vm.StopStreamCommand.CanExecute(null).Should().BeTrue();
        _vm.SwitchDisplayCommand.CanExecute(null).Should().BeTrue();

        _vm.IsSwitchingDisplay = true;
        _vm.SwitchDisplayCommand.CanExecute(null).Should().BeFalse("a switch already in flight");
    }

    // ── Window results ──

    [Fact]
    public void AFailedWindowAction_ShowsTheHostsReason_OrAGenericOneWhenItGaveNone()
    {
        _vm.ApplyWindowResult(new DesktopWindowResult { Success = false, ErrorText = "Access is denied" });
        _vm.WindowControlStatusText.Should().Be("Access is denied");

        _vm.ApplyWindowResult(new DesktopWindowResult { Success = false });
        _vm.WindowControlStatusText.Should().Be(LocalizationService.Instance["RemoteDesktop_WindowControlFailed"]);
    }

    [Fact]
    public void ARefreshedWindowList_KeepsTheSelectedWindowById_OrFallsBackToTheFirst()
    {
        _vm.ApplyWindowResult(new DesktopWindowResult
        {
            Success = true,
            Windows = [new DesktopWindowInfo { Id = "1", Title = "A" }, new DesktopWindowInfo { Id = "2", Title = "B" }],
        });
        _vm.SelectedWindow.Should().NotBeNull();
        _vm.SelectedWindow!.Id.Should().Be("1", "nothing was selected, so the first is");

        _vm.SelectedWindow = _vm.AvailableWindows[1];
        _vm.ApplyWindowResult(new DesktopWindowResult
        {
            Success = true,
            Windows = [new DesktopWindowInfo { Id = "3", Title = "C" }, new DesktopWindowInfo { Id = "2", Title = "B2" }],
        });
        _vm.SelectedWindow!.Title.Should().Be("B2", "the same window id, from the fresh list");

        _vm.ApplyWindowResult(new DesktopWindowResult
        {
            Success = true,
            Windows = [new DesktopWindowInfo { Id = "9", Title = "Z" }],
        });
        _vm.SelectedWindow!.Id.Should().Be("9", "the old window is gone");
    }

    [Fact]
    public void AResultWithoutAWindowList_LeavesTheListAlone()
    {
        _vm.ApplyWindowResult(new DesktopWindowResult { Success = true, Windows = [new DesktopWindowInfo { Id = "1" }] });

        _vm.ApplyWindowResult(new DesktopWindowResult { Success = true, Windows = null });

        _vm.AvailableWindows.Should().ContainSingle();
    }

    // ── Host events ──

    [Fact]
    public void TheHostsDescription_OfTheScreen_IsRemembered()
    {
        _vm.OnMetaReceived(new DesktopMeta { ScreenWidth = 2560, ScreenHeight = 1440, DesktopLeft = -1920, DesktopTop = -200 });

        (_vm.ScreenWidth, _vm.ScreenHeight, _vm.DesktopLeft, _vm.DesktopTop).Should().Be((2560, 1440, -1920, -200));
        
        _vm.Resolution.Should().Be("2560×1440");
    }

    [Fact]
    public void ACursorUpdate_FromAnOlderStreamThanTheCurrentOne_IsDropped()
    {
        _vm.OnMetaReceived(new DesktopMeta { StreamSerial = 5, ScreenWidth = 100, ScreenHeight = 100 });
        

        _vm.OnCursorStateReceived(new DesktopCursorState { StreamSerial = 3, X = 11, Y = 12, Visible = true });
        
        _vm.RemoteCursorHostX.Should().Be(0, "it describes the stream the display switch just replaced");

        _vm.OnCursorStateReceived(new DesktopCursorState { StreamSerial = 5, X = 21, Y = 22, Visible = true });
        
        (_vm.RemoteCursorHostX, _vm.RemoteCursorHostY).Should().Be((21, 22));
    }

    [Fact]
    public void ACursorUpdate_WithNoSerial_IsAlwaysApplied()
    {
        _vm.OnMetaReceived(new DesktopMeta { StreamSerial = 5, ScreenWidth = 100, ScreenHeight = 100 });
        

        _vm.OnCursorStateReceived(new DesktopCursorState { StreamSerial = 0, X = 7, Y = 8, Visible = false });
        

        (_vm.RemoteCursorHostX, _vm.RemoteCursorHostY, _vm.RemoteCursorHostVisible).Should().Be((7, 8, false));
    }

    [Fact]
    public void ANewStream_ResetsTheCursorOverlay_ButTheSameStreamDoesNot()
    {
        _vm.OnMetaReceived(new DesktopMeta { StreamSerial = 1, ScreenWidth = 100, ScreenHeight = 100 });
        _vm.OnCursorStateReceived(new DesktopCursorState { StreamSerial = 1, X = 4, Y = 5, Visible = true, ShapeSerial = 9, HotspotX = 2 });
        
        _vm.ActiveCursorShapeSerial.Should().Be(9);

        _vm.OnStreamDescriptorReceived(new DesktopStreamDescriptor { StreamMappingId = "m", StreamSerial = 1 });
        
        _vm.ActiveCursorShapeSerial.Should().Be(9, "the same serial is the same stream");

        _vm.OnStreamDescriptorReceived(new DesktopStreamDescriptor { StreamMappingId = "m", StreamSerial = 2 });
        
        _vm.ActiveCursorShapeSerial.Should().Be(0);
        _vm.RemoteCursorHostVisible.Should().BeFalse();
        _vm.CursorHotspotX.Should().Be(0);
    }

    [Fact]
    public void AHostError_IsShownVerbatim_AndFlagsTheStream()
    {
        _vm.OnErrorReceived("Capture failed: the display is locked");
        

        _vm.StatusText.Should().Be("Capture failed: the display is locked");
        _vm.HasStreamError.Should().BeTrue();
    }

    [Fact]
    public void ADroppedConnection_StopsTheStream_AndZeroesTheFps()
    {
        _vm.IsStreaming = true;
        _vm.IsSwitchingDisplay = true;
        _vm.ActualFps = 59.9;

        _vm.OnDisconnected();
        

        _vm.IsStreaming.Should().BeFalse();
        _vm.IsSwitchingDisplay.Should().BeFalse();
        _vm.ActualFps.Should().Be(0);
        _vm.StatusText.Should().Be(LocalizationService.Instance["Status_Disconnected"]);
        _vm.StopStreamCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task StoppingByHand_ClearsTheStreamState_EvenWithNothingToTalkTo()
    {
        _vm.IsStreaming = true;
        _vm.HasStreamError = true;
        _vm.ActualFps = 30;

        await _vm.StopStreamCommand.ExecuteAsync(null);

        _vm.IsStreaming.Should().BeFalse();
        _vm.HasStreamError.Should().BeFalse();
        _vm.ActualFps.Should().Be(0);
        _vm.StatusText.Should().Be(LocalizationService.Instance["Status_Stopped"]);
    }

    [Fact]
    public void AfterDispose_TheViewModelNoLongerListensToTheShell()
    {
        var raised = new List<string?>();
        _vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        _vm.IsStreaming = true;
        raised.Clear();

        _vm.Dispose();
        _shell.IsReducedMotion = !_shell.IsReducedMotion;

        raised.Should().NotContain(nameof(RemoteDesktopViewModel.ShowStreamPulse));
    }
}
