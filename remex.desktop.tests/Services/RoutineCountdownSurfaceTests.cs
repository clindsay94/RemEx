using System.Runtime.CompilerServices;
using Remex.Desktop.Services.Routines;
using Remex.Desktop.ViewModels;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// The PC countdown surfaces (routines spec §8.6): the window's view model, the tray's "Cancel routine"
/// state, and the guard that the countdown window never depends on <c>MainWindow</c>.
/// </summary>
public sealed class RoutineCountdownSurfaceTests
{
    private static RoutineCountdownPrompt Prompt(bool test = false, bool dry = false, string? phone = "Pixel") =>
        new("run-1", "Evening", "SHUTDOWN", phone, "nfc.tap", 15, test, dry);

    [Fact]
    public void TheWindowShowsTheRoutineTheActionTheOriginAndTheSecondsLeft()
    {
        var vm = new RoutineCountdownViewModel(Prompt(), () => { });

        Assert.Equal("Evening", vm.RoutineName);
        Assert.Equal(15, vm.SecondsLeft);
        Assert.Contains("15", vm.CountdownText, StringComparison.Ordinal);
        Assert.Contains("Pixel", vm.OriginText, StringComparison.Ordinal);
        Assert.False(vm.HasNote);
        Assert.DoesNotContain("Routine_", vm.CountdownText + vm.OriginText + vm.ActionLabel, StringComparison.Ordinal);
    }

    [Fact]
    public void TickCountsDownAndStopsAtZero()
    {
        var vm = new RoutineCountdownViewModel(Prompt() with { Seconds = 2 }, () => { });

        vm.Tick();
        vm.Tick();
        vm.Tick();

        Assert.Equal(0, vm.SecondsLeft);
    }

    [Fact]
    public void CancelRunsTheCallbackExactlyOnce()
    {
        var calls = 0;
        var vm = new RoutineCountdownViewModel(Prompt(), () => calls++);

        vm.CancelCommand.Execute(null);
        vm.CancelCommand.Execute(null);

        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ATestOrADryRunSaysSoInTheWindow(bool test, bool dry)
    {
        var vm = new RoutineCountdownViewModel(Prompt(test, dry), () => { });

        Assert.True(vm.HasNote);
        Assert.False(string.IsNullOrWhiteSpace(vm.NoteText));
    }

    [Fact]
    public void AnUnknownPhoneStillReadsNaturally()
    {
        var vm = new RoutineCountdownViewModel(Prompt(phone: null), () => { });

        Assert.DoesNotContain("{0}", vm.OriginText, StringComparison.Ordinal);
        Assert.DoesNotContain("Routine_", vm.OriginText, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTrayItemFollowsTheCountdownAndCancelsIt()
    {
        var tray = RoutineCountdownTrayState.Instance;
        var cancelled = 0;
        var changes = 0;
        void OnChanged() => changes++;
        tray.Changed += OnChanged;
        try
        {
            tray.Activate("run-tray", () => cancelled++);
            Assert.True(tray.IsActive);

            // A stale close for another run must not disarm the live one.
            tray.Deactivate("run-other");
            Assert.True(tray.IsActive);

            Assert.True(tray.CancelActive());
            Assert.Equal(1, cancelled);

            tray.Deactivate("run-tray");
            Assert.False(tray.IsActive);
            Assert.False(tray.CancelActive());
            Assert.Equal(2, changes);
        }
        finally
        {
            tray.Changed -= OnChanged;
            tray.Deactivate("run-tray");
        }
    }

    [Theory]
    [InlineData("SHUTDOWN")]
    [InlineData("MONITOROFF")]
    public void EveryVerbHasALocalizedLabel(string verb)
    {
        Assert.DoesNotContain("Routine_", RoutineStrings.ActionLabel(verb), StringComparison.Ordinal);
    }

    /// <summary>
    /// docs/REGRESSION-GUARDS.md, "The routine countdown window must not depend on MainWindow". Code
    /// lines only: the files explain the rule in comments, which must stay allowed to name it.
    /// </summary>
    [Theory]
    [InlineData("Views/RoutineCountdownWindow.axaml.cs")]
    [InlineData("Services/Routines/AvaloniaRoutineUi.cs")]
    public void TheCountdownWindowNeverReachesForMainWindow(string relativePath)
    {
        var path = Path.Combine([RepoRoot(), "remex.desktop", .. relativePath.Split('/')]);
        var codeLines = File.ReadAllLines(path)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith("//", StringComparison.Ordinal) && !line.StartsWith('*'));

        foreach (var line in codeLines)
        {
            Assert.DoesNotContain("MainWindow", line, StringComparison.Ordinal);
            Assert.DoesNotContain("ShowDialog", line, StringComparison.Ordinal);
            Assert.DoesNotContain("Owner", line, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheCountdownWindowMarkupHasOneButtonThatIsDefaultAndCancel()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "RoutineCountdownWindow.axaml"));

        Assert.Equal(1, CountOf(xaml, "<Button "));
        Assert.Contains("IsDefault=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsCancel=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Topmost=\"True\"", xaml, StringComparison.Ordinal);
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
