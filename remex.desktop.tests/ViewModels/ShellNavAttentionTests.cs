using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// The sidebar toggle shows a dot whenever a sidebar badge (alerts or transfers) is active, so a
/// user with the drawer closed still sees that something is waiting.
/// </summary>
public sealed class ShellNavAttentionTests : IAsyncLifetime
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("remex-navattn-").FullName;
    private readonly ThemeService _theme = new() { PostToUiThread = action => action() };
    private readonly object _queueLock = new();
    private ShellViewModel _shell = null!;

    public async Task InitializeAsync()
    {
        var layout = new DashboardLayoutService(Path.Combine(_tempDir, "dashboard_layout.json"), _theme);
        await layout.LoadAsync();
        _shell = new ShellViewModel(
            layout, _theme, new ConnectionViewModel(),
            new ServiceCollection().AddLogging()
                .AddSingleton<SensorAlertStore>()
                .AddSingleton<SensorAlertTracker>()
                .BuildServiceProvider(),
            transferQueuePost: action =>
            {
                lock (_queueLock)
                    action();
            });
        _shell.ProfileReplacedDispatch = run => run();
        _shell.DiagnosticsLogDispatch = run => run();
    }

    public Task DisposeAsync()
    {
        _shell.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort cleanup */ }
        return Task.CompletedTask;
    }

    [Fact]
    public void NoAttentionAtStart()
    {
        _shell.HasNavAttention.Should().BeFalse();
        _shell.NavAttentionHelp.Should().BeEmpty();
    }

    [Fact]
    public void AnAlertRaisesItAndSaysWhy()
    {
        var raised = new List<string?>();
        _shell.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        _shell.AlertBadgeCount = 1;

        _shell.HasNavAttention.Should().BeTrue();
        _shell.NavAttentionHelp.Should().Be(LocalizationService.Instance["Nav_AttentionHelp"]).And.NotBeEmpty();
        raised.Should().Contain(nameof(ShellViewModel.HasNavAttention));
        raised.Should().Contain(nameof(ShellViewModel.NavAttentionHelp));

        _shell.AlertBadgeCount = 0;
        _shell.HasNavAttention.Should().BeFalse();
        _shell.NavAttentionHelp.Should().BeEmpty();
    }

    [Fact]
    public async Task AnActiveTransferRaisesIt()
    {
        var raised = new List<string?>();
        _shell.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        var started = new TaskCompletionSource();
        var gate = new TaskCompletionSource();
        var item = _shell.TransferQueueForTests.Enqueue(
            FileTransferQueueKind.Upload, "test.txt", async (_, _) =>
            {
                started.TrySetResult();
                await gate.Task;
            });
        await started.Task;

        _shell.HasNavAttention.Should().BeTrue();
        raised.Should().Contain(nameof(ShellViewModel.HasNavAttention));

        gate.SetResult();
        await item.Completion.Task;
        _shell.HasNavAttention.Should().BeFalse();
    }

    [Fact]
    public void TheDrawerToggleWrapsItsIconInADotBadgeBoundToIt()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "ShellView.axaml"));
        var start = xaml.IndexOf("Name=\"DrawerToggle\"", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0);
        var toggle = xaml[start..xaml.IndexOf("</Button>", start, StringComparison.Ordinal)];

        toggle.Should().Contain("<material:Badged IsBadgeVisible=\"{Binding HasNavAttention}\"");
        toggle.Should().Contain("BadgeDisplayContent=\"False\"");
        toggle.Should().Contain("AutomationProperties.HelpText=\"{Binding NavAttentionHelp}\"");
    }

    private static string RepoRoot([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));
}
