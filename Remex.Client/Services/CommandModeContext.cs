using System;
using System.IO;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Remex.Core.Services.Command;
using Remex.Core.Services.Network;
using Remex.Client.Services.Command;
using Remex.Client.Services.Network;
using Microsoft.Extensions.Configuration;

namespace Remex.Client.Services;

public static class CommandModeContext
{
    private const string MutexName = @"Global\RemExServiceMutex";
    private static Mutex? _mutex;
    private static CancellationTokenSource? _listenerCts;
    public static bool IsServerMode { get; private set; }

    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // Add Logging
        services.AddLogging(configure =>
        {
            configure.AddConsole();
            configure.AddProvider(new Remex.Core.Logging.InMemoryLoggerProvider());
        });
        services.AddSingleton(configuration);

        // Determine Mode via Mutex
        bool createdNew = false;
        try
        {
            string finalMutexName = OperatingSystem.IsWindows() ? MutexName : "RemExServiceMutex";
            _mutex = new Mutex(false, finalMutexName, out createdNew);
            IsServerMode = createdNew;
        }
        catch (UnauthorizedAccessException)
        {
            // If we get an unauthorized access exception, the Mutex exists but we can't acquire it (likely created by service)
            IsServerMode = false;
        }
        catch (IOException ex)
        {
            Console.WriteLine($"I/O error checking Mutex: {ex.Message}");
            IsServerMode = false; // default to client mode to be safe
        }
        catch (PlatformNotSupportedException ex)
        {
            Console.WriteLine($"Platform does not support Mutex: {ex.Message}");
            IsServerMode = false; // default to client mode to be safe
        }

        if (IsServerMode)
        {
            Console.WriteLine("Initializing Remex.Client in Server Mode (Direct Execution)");
            // Register Direct Execution Services
            if (OperatingSystem.IsWindows())
            {
                services.AddSingleton<ISystemCommandService, WindowsSystemCommandService>();
            }
            else if (OperatingSystem.IsLinux())
            {
                services.AddSingleton<ISystemCommandService, LinuxSystemCommandService>();
            }
            else
            {
                // Fallback for Android/iOS where direct commands might not be implemented yet.
                // We map it to IpcClientCommandService or a NoOp just to satisfy DI
                services.AddSingleton<ISystemCommandService, IpcClientCommandService>();
            }

            services.AddSingleton<IWakeOnLanService, WakeOnLanService>();
            services.AddSingleton<INetworkListener, RemexNetworkListener>();
        }
        else
        {
            Console.WriteLine("Initializing Remex.Client in Client Mode (IPC forwarding)");
            // Register IPC Forwarding Services
            services.AddSingleton<ISystemCommandService, IpcClientCommandService>();
            services.AddSingleton<IWakeOnLanService, IpcWakeOnLanService>();

            // Release the mutex if we acquired it but decide we are in client mode somehow (fallback)    
            if (createdNew)
            {
                _mutex?.ReleaseMutex();
                _mutex?.Dispose();
                _mutex = null;
            }
        }
    }

    public static void StartListener(IServiceProvider provider)
    {
        if (IsServerMode)
        {
            // Start the network listener in the background if we are the server
            _listenerCts = new CancellationTokenSource();
            var listener = provider.GetRequiredService<INetworkListener>();
            _ = listener.StartListeningAsync(_listenerCts.Token);
        }
    }

    public static void Cleanup()
    {
        // Cancel the background listener so the process can exit
        if (_listenerCts != null)
        {
            _listenerCts.Cancel();
            _listenerCts.Dispose();
            _listenerCts = null;
        }

        if (IsServerMode && _mutex != null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch { /* Ignore if not owned */ }
            _mutex.Dispose();
        }
    }
}