using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Remex.Core;
using Remex.Client.Services;
using Remex.Host;

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
            services.AddSingleton<Remex.Client.Services.IStartupRegistrationService, Remex.Client.Desktop.Services.StartupRegistrationService>();
        };

        // Two-plane model: the RemexHost service (session 0) owns the command plane from boot;
        // the embedded host owns the interactive plane (desktop streaming) for this login session.
        // They coexist on different ports; the phone disambiguates via DesktopMeta.HostInstanceId.
        int preferredPort = IsWindowsServiceRunning("RemexHost")
            ? RemexConstants.DefaultPort + 1   // service owns 5005; take 5006
            : RemexConstants.DefaultPort;
        EmbeddedHostPort = TryStartHost(args, preferredPort)
                        ?? TryStartHost(args, preferredPort + 1);

        if (EmbeddedHostPort.HasValue)
        {
            App.OverrideHostPort = EmbeddedHostPort.Value;
            App.EmbeddedHostInstanceId = HostBootstrapper.InstanceId;
            App.EmbeddedHostServices = _hostApp?.Services;
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
            // Probe on both loopback interfaces (IPv4 and IPv6) to avoid dual-stack wildcard collisions.
            using (var probeV4 = new TcpListener(IPAddress.Loopback, port))
            {
                probeV4.Start();
                probeV4.Stop();
            }

            if (Socket.OSSupportsIPv6)
            {
                using var probeV6 = new TcpListener(IPAddress.IPv6Loopback, port);
                probeV6.Start();
                probeV6.Stop();
            }

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

    private static bool IsWindowsServiceRunning(string serviceName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "sc.exe",
                    Arguments = $"query {serviceName}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            return process.ExitCode == 0
                && output.Contains("STATE", StringComparison.OrdinalIgnoreCase)
                && output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
