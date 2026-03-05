using Avalonia;
using Remex.Client;
using Remex.Host;
using System;
using System.Threading.Tasks;

namespace Remex.Client.Desktop;

class Program
{
    private static Microsoft.AspNetCore.Builder.WebApplication? _hostApp;

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Start the Remex Host WebSocket server in-process.
        try
        {
            _hostApp = HostBootstrapper.CreateApplication(args);
            _ = _hostApp.StartAsync();
        }
        catch (Exception ex)
        {
            // If the host fails to start (e.g. port already in use),
            // log and continue — the UI can still connect to an external host.
            Console.Error.WriteLine($"[Remex] Embedded host failed to start: {ex.Message}");
            _hostApp = null;
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            // Gracefully shut down the embedded host when the UI exits.
            if (_hostApp is not null)
            {
                _hostApp.StopAsync().GetAwaiter().GetResult();
                (_hostApp as IDisposable)?.Dispose();
            }
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
