using System;
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
    public static bool IsServerMode { get; private set; }

    public static IServiceProvider InitializeAndGetServiceProvider(IConfiguration configuration)
    {
        var services = new ServiceCollection();

        // Add Logging
        services.AddLogging(configure => configure.AddConsole());
        services.AddSingleton(configuration);

        // Determine Mode via Mutex
        bool createdNew = false;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // In client mode we just try to open or create without specific ACLs here to test presence
                // WaitOne(0) checks if we can acquire it without blocking
                _mutex = new Mutex(false, MutexName, out createdNew);
                IsServerMode = createdNew;
            }
            else
            {
                _mutex = new Mutex(false, MutexName, out createdNew);
                IsServerMode = createdNew;
            }
        }
        catch (UnauthorizedAccessException)
        {
            // If we get an unauthorized access exception, the Mutex exists but we can't acquire it (likely created by service)
            IsServerMode = false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error checking Mutex: {ex.Message}");
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

        var provider = services.BuildServiceProvider();

        if (IsServerMode)
        {
            // Start the network listener in the background if we are the server
            var listener = provider.GetRequiredService<INetworkListener>();
            _ = listener.StartListeningAsync(CancellationToken.None);
        }

        return provider;
    }

    public static void Cleanup()
    {
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
