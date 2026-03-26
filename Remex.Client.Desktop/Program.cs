using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Remex.Client;
using Remex.Client.Services;
using Remex.Core;
using Remex.Host;
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Remex.Client.Desktop;

class Program
{
    private static Microsoft.AspNetCore.Builder.WebApplication? _hostApp;

    /// <summary>
    /// The port the embedded host actually started on.
    /// Passed to the Avalonia app so the client connects to the right endpoint.
    /// </summary>
    internal static int? EmbeddedHostPort { get; private set; }

    /// <summary>
    /// Stops the embedded host so the Windows Service can bind to the default port.
    /// Safe to call when no host is running (no-op).
    /// </summary>
    internal static async Task StopEmbeddedHostAsync()
    {
        if (_hostApp is not null)
        {
            await _hostApp.StopAsync();
            (_hostApp as IDisposable)?.Dispose();
            _hostApp = null;
            EmbeddedHostPort = null;
        }
    }

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Register desktop-specific services
        App.RegisterPlatformServices = services =>
        {
            services.AddSingleton<Remex.Core.Services.IIconExtractionService, Remex.Client.Desktop.Services.DesktopIconExtractionService>();
        };

        // Start the Remex Host WebSocket server in-process.
        // If the default port is taken (e.g. by the Remex Windows Service),
        // fall back to an alternative port so the desktop always has its own host
        // with access to HWiNFO shared memory in the user session.
        EmbeddedHostPort = TryStartHost(args, RemexConstants.DefaultPort)
                        ?? TryStartHost(args, RemexConstants.DefaultPort + 1);

        if (EmbeddedHostPort.HasValue)
        {
            App.OverrideHostPort = EmbeddedHostPort.Value;
            App.EmbeddedHostInstanceId = HostBootstrapper.InstanceId;
        }

        App.StopEmbeddedHostAsync = StopEmbeddedHostAsync;

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            // Stop the background listener so the process can exit cleanly
            CommandModeContext.Cleanup();

            // Gracefully shut down the embedded host when the UI exits.
            if (_hostApp is not null)
            {
                _hostApp.StopAsync().GetAwaiter().GetResult();
                (_hostApp as IDisposable)?.Dispose();
            }
        }
    }

    /// <summary>
    /// Attempts to start the host on the given port. Returns the port on success, null on failure.
    /// </summary>
    private static int? TryStartHost(string[] args, int port)
    {
        try
        {
            // Quick check: is the port already in use?
            using var probe = new TcpListener(IPAddress.Loopback, port);
            probe.Start();
            probe.Stop();

            _hostApp = HostBootstrapper.CreateApplication(args, port);
            _hostApp.StartAsync().GetAwaiter().GetResult();

            Console.WriteLine($"[Remex] Embedded host started on port {port}.");
            return port;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Remex] Could not start host on port {port}: {ex.Message}");
            _hostApp = null;
            return null;
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
