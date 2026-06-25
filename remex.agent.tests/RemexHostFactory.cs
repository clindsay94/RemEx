using System;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Remex.Core.Services;

namespace Remex.Agent.Tests;

/// <summary>
/// Builds Remex.Agent in-process for integration tests via <see cref="HostBootstrapper.CreateApplication"/>
/// + TestServer, WITHOUT running the consolidated GUI <c>Program.Main</c>.
///
/// The consolidated entry point launches Avalonia and disposes the embedded host in its <c>finally</c>
/// block; under <see cref="WebApplicationFactory{TEntryPoint}"/> that runs on the resolver's thread and
/// races the test (it disposes the very host the test is using — an <c>ObjectDisposedException</c> on the
/// service provider during endpoint routing). Building the host directly here side-steps the entry point
/// entirely and gives each fixture an isolated, cleanly-disposed host.
/// </summary>
public sealed class RemexHostFactory : WebApplicationFactory<Program>
{
    private Action<IServiceCollection>? _configureServices;
    private HostMode _mode = HostMode.Full;

    // Single public (implicit, parameterless) constructor so the factory can be used directly as an
    // xUnit IClassFixture. Tests that need service overrides chain WithServices(...) before first use.

    /// <summary>
    /// Registers service overrides applied immediately before the host is built (last-wins). Must be
    /// called before the first <c>Server</c>/<c>CreateClient</c> access, which lazily builds the host.
    /// </summary>
    public RemexHostFactory WithServices(Action<IServiceCollection> configureServices)
    {
        _configureServices = configureServices;
        return this;
    }

    /// <summary>Builds the host in the given <see cref="HostMode"/> (default <see cref="HostMode.Full"/>).</summary>
    public RemexHostFactory WithMode(HostMode mode)
    {
        _mode = mode;
        return this;
    }

    // Returning a non-null builder makes the base class skip entry-point (Program.Main) resolution;
    // CreateHost ignores this dummy and builds the real host via HostBootstrapper.
    protected override IHostBuilder CreateHostBuilder() => new HostBuilder();

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var app = HostBootstrapper.CreateApplication(
            Array.Empty<string>(),
            mode: _mode,
            configureWebHost: webHost => webHost.UseTestServer(),
            configureServices: services =>
            {
                // Replace OS-disrupting host services with safe doubles for ALL test hosts. Registered
                // FIRST so any caller override via WithServices(...) runs last and still wins (e.g.
                // RemoteDesktopHandlerTests' own MockScreenCaptureService / MockCommandService).
                //
                //  • Capture: the real WindowsScreenCaptureService opens a live DXGI Desktop Duplication
                //    session in its constructor, and HostBootstrapper resolves IHostCapabilitiesProvider
                //    (which depends on IScreenCaptureService) eagerly at startup — so without this every
                //    fixture boot opens a real session; across the full suite that storm wedges GPU/DWM.
                //  • Session guard: the real WindowsInteractiveSessionGuard runs `tscon /dest:console`
                //    (when the keep-session-unlocked flag is set) which physically locks the developer's
                //    Windows session — screen flash → login/PIN screen.
                //  • System commands: defense-in-depth so a test can never lock/sign-out/reboot the box.
                // (RemEx-21g.)
                services.AddSingleton<IScreenCaptureService, FakeScreenCaptureService>();
                services.AddSingleton<Remex.Agent.Services.Session.IInteractiveSessionGuard, NoOpInteractiveSessionGuard>();
                services.AddSingleton<Remex.Core.Services.Command.ISystemCommandService, NoOpSystemCommandService>();
                _configureServices?.Invoke(services);
            });
        app.Start();
        return app;
    }
}
