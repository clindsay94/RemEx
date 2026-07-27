using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Remex.Core.Services.Command;
using Remex.Core.Services.Network;
using Remex.Desktop.Services.Command;
using Remex.Desktop.Services.Network;

namespace Remex.Desktop.Services;

/// <summary>
/// Wires the desktop UI's command / Wake-on-LAN services to the in-process host.
///
/// RemEx 2.0 is a single process: the embedded host (<c>HostBootstrapper</c>) owns the real
/// <see cref="ISystemCommandService"/>, <see cref="IWakeOnLanService"/>, and the network listeners.
/// The UI only needs those interfaces in its own DI container to satisfy view-model dependencies, so
/// it registers thin delegating adapters (<c>IpcClientCommandService</c> / <c>IpcWakeOnLanService</c>)
/// that forward to <see cref="App.EmbeddedHostServices"/>.
///
/// The former two-process "server vs client" mode — a <c>Global\RemExServiceMutex</c> arbiter plus a
/// duplicate network listener — is gone. Keeping it would double-bind the listener now that the host
/// always runs in-process (the deleted <c>LocalIpcServerService</c> was the only thing that grabbed
/// that mutex first and kept the UI in "client" mode). (RemEx-aep Phase 3)
/// </summary>
public static class CommandModeContext
{
    /// <summary>
    /// Retained for call sites that branch on the legacy two-process mode. There is no separate
    /// service process in RemEx 2.0, so the UI is never the "server" — this is always false.
    /// </summary>
    public static bool IsServerMode => false;

    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddLogging(configure =>
        {
            configure.AddConsole();
            configure.AddProvider(new Remex.Core.Logging.InMemoryLoggerProvider());
        });
        services.AddSingleton(configuration);

        // Delegating adapters: both forward to the in-process host's real services via
        // EmbeddedHostServiceLocator. The host owns the network listeners and the concrete
        // command / Wake-on-LAN implementations; the UI must NOT start its own listener.
        services.AddSingleton<ISystemCommandService, IpcClientCommandService>();
        services.AddSingleton<IWakeOnLanService, IpcWakeOnLanService>();
    }

    /// <summary>
    /// No-op: the embedded host runs the network listener. Kept so existing call sites
    /// (<c>App.OnFrameworkInitializationCompleted</c>) need no change.
    /// </summary>
    public static void StartListener(IServiceProvider provider)
    {
        // Intentionally empty — the in-process host owns the listener (see class summary).
    }

    /// <summary>
    /// No-op cleanup: there is no UI-owned mutex or listener to release. Kept so existing call sites
    /// (<c>App.ShutdownApplication</c>, <c>Program.Main</c> finally) need no change.
    /// </summary>
    public static void Cleanup()
    {
        // Intentionally empty — see class summary.
    }
}
