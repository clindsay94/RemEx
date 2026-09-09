using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Remex.Desktop.Views;

namespace Remex.Desktop.Render.Tests;

/// <summary>
/// Builds a real <see cref="ShellView"/> bound to a real <see cref="ShellViewModel"/> inside a
/// headless <see cref="Window"/>, and captures rasterised frames from it (RemEx-0e9eq). The
/// container mirrors <c>ShellTransferAndDiagnosticsBadgeTests</c>'s fake-DI pattern
/// (remex.desktop.tests) rather than App.axaml.cs's production container - MainWindow.axaml.cs and
/// production <c>OnFrameworkInitializationCompleted</c> both touch things (the tray, the IPC
/// listener, ProgramData) that have no place in a render test.
/// </summary>
/// <remarks>
/// <see cref="CreateAsync"/> is called from inside the <c>[AvaloniaFact]</c>/
/// <c>[AvaloniaTheory]</c>-dispatched test body - not from <c>IAsyncLifetime.InitializeAsync</c>,
/// even though that would read more naturally next to <c>ShellTransferAndDiagnosticsBadgeTests</c>'s
/// own <c>IAsyncLifetime</c> shape (RemEx-0e9eq round 2 finding). <c>InitializeAsync</c> runs outside
/// the Avalonia-dispatched thread that <c>[AvaloniaFact]</c> installs for the test body itself, so
/// <see cref="ThemeService"/>'s constructor - which posts
/// <c>Application.Current.Resources.MergedDictionaries.Add(_overrideResources)</c> onto
/// <c>Dispatcher.UIThread</c> - resolves the wrong dispatcher/Application pairing there and throws
/// "The ResourceDictionary already has a parent" the first time <c>RunJobs()</c> drains it. Measured:
/// the identical construction sequence run synchronously inside an <c>[AvaloniaFact]</c> body (no
/// <c>IAsyncLifetime</c> involved) does not throw. <see cref="DashboardLayoutService.LoadAsync"/> is
/// AWAITED, never blocked on: the test body runs on the headless dispatcher thread, so a
/// <c>GetAwaiter().GetResult()</c> would deadlock the moment the load genuinely went async (a
/// seeded <c>dashboard_layout.json</c> reaches <c>File.ReadAllTextAsync</c>, whose continuation
/// posts back to the very thread that is blocked). The <c>[AvaloniaFact]</c> runner pumps that
/// dispatcher for async test bodies, so the continuation lands on the same thread and
/// <c>ThemeService</c>'s dispatcher affinity is preserved.
/// </remarks>
internal sealed class ShellRenderFixture : IDisposable
{
    private readonly string _tempDir;

    public ShellViewModel ViewModel { get; }
    public ShellView View { get; }
    public Window Window { get; }

    private ShellRenderFixture(string tempDir, ShellViewModel viewModel, ShellView view, Window window)
    {
        _tempDir = tempDir;
        ViewModel = viewModel;
        View = view;
        Window = window;
    }

    public static async Task<ShellRenderFixture> CreateAsync()
    {
        var tempDir = Directory.CreateTempSubdirectory("remex-0e9eq-render-").FullName;

        // NOT PostToUiThread = action => action() - that seam exists for remex.desktop.tests, which
        // has no Avalonia.Headless reference and so no dispatcher to pump (see the property's own
        // remarks). This project has a real headless dispatcher, and forcing the hop inline here
        // creates a genuine race: ThemeService's own constructor defers its first
        // `_overrideResources` add via a real Dispatcher.UIThread.Post, and DashboardLayoutService's
        // LoadAsync (below) can call ApplyCustomization for a stamped profile - if that runs inline
        // instead of also deferring, it adds `_overrideResources` before the constructor's queued
        // job does, and that job's own later Add then throws "The ResourceDictionary already has a
        // parent" (measured). Leaving PostToUiThread at its production default keeps both posts in
        // their original enqueue order, so CaptureFrame's RunJobs() drains them safely.
        var theme = new ThemeService();
        var layoutService = new DashboardLayoutService(Path.Combine(tempDir, "dashboard_layout.json"), theme);
        await layoutService.LoadAsync();

        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<SensorAlertStore>()
            .AddSingleton<SensorAlertTracker>()
            .BuildServiceProvider();

        // App.SkipProductionStartup (armed by RenderTestApp's module initializer) means
        // App.axaml.cs's own OnFrameworkInitializationCompleted container never runs, so this is
        // the only container ShellView's descendants will ever see - some views null-tolerate a
        // missing App.Services (CanvasView.axaml.cs:198, SettingsView.axaml.cs:70/98) but there is
        // no reason to exercise that path here when the real seam exists.
        App.SetServicesForTests(services);

        var viewModel = new ShellViewModel(
            layoutService,
            theme,
            new HardwareThemeService(theme),
            new ConnectionViewModel(),
            services,
            transferQueuePost: action => action())
        {
            ProfileReplacedDispatch = run => run(),
            DiagnosticsLogDispatch = run => run(),

            // The shell chrome under test, not the boot sequence - RemEx-b8dxy and the shell's own
            // paint are what this harness exists to catch, not the splash animation. The drawer is
            // forced open because DrawerHeaderMachineName (named-element coverage) only exists
            // inside the open drawer's content.
            IsWelcomeSplashMounted = false,
            ShowWelcomeSplash = false,
            ShowTutorialOverlay = false,
            IsDrawerOpen = true,
        };

        var view = new ShellView { DataContext = viewModel };
        var window = new Window { Width = 1280, Height = 800, Content = view };

        return new ShellRenderFixture(tempDir, viewModel, view, window);
    }

    /// <summary>
    /// Shows the window, pumps the dispatcher, and captures a frame - retrying up to three render
    /// ticks because the very first tick after <c>Window.Show()</c> can land before layout has
    /// actually produced anything to rasterise, in which case <c>CaptureRenderedFrame</c> returns
    /// null rather than an empty bitmap.
    /// </summary>
    public WriteableBitmap CaptureFrame()
    {
        Window.Show();
        Dispatcher.UIThread.RunJobs();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var frame = Window.CaptureRenderedFrame();
            if (frame != null) return frame;
            Dispatcher.UIThread.RunJobs();
        }

        throw new InvalidOperationException(
            "CaptureRenderedFrame returned null after three render ticks - the window never produced a frame.");
    }

    public void Dispose()
    {
        ViewModel.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort cleanup */ }
    }
}
