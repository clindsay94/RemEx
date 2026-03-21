using Avalonia;
using Avalonia.Android;
using Remex.Client;
using Remex.Client.Android.Services;
using Remex.Client.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Remex.Client.Android;

public static class AvaloniaInitHelper
{
    private static bool _isInitialized = false;

    public static void EnsureInitialized()
    {
        if (_isInitialized || App.Services != null)
        {
            _isInitialized = true;
            return;
        }

        global::Android.Util.Log.Debug("RemexWidget", "Initializing Avalonia from background...");

        // Setup the app builder just like MainActivity does
        var builder = AppBuilder.Configure<App>()
            .UseAndroid()
            .WithInterFont()
            .LogToTrace();

        // Ensure platform services are registered
        App.RegisterPlatformServices = services =>
        {
            // Note: MainActivity is null here, so we might need a context-free immersive mode or dummy
            services.AddSingleton<IImmersiveModeService>(new DummyImmersiveModeService());
        };

        builder.SetupWithoutStarting();
        _isInitialized = true;

        // Ensure background updates also trigger Android system broadcasts
        App.RequestPlatformWidgetUpdate = () =>
        {
            MainActivity.RequestWidgetUpdate(global::Android.App.Application.Context);
        };
        
        global::Android.Util.Log.Debug("RemexWidget", "Avalonia background initialization complete.");
    }

    private class DummyImmersiveModeService : IImmersiveModeService
    {
        public void EnterImmersiveMode() { }
        public void ExitImmersiveMode() { }
    }
}
