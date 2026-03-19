using Android;
using Android.App;
using Android.Content.PM;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using Avalonia;
using Avalonia.Android;
using Microsoft.Extensions.DependencyInjection;
using Remex.Client;
using Remex.Client.Android.Services;
using Remex.Client.Services;

namespace Remex.Client.Android;

[Activity(
    Label = "Remex",
    Theme = "@style/MyTheme.NoActionBar",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity<App>
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        return base.CustomizeAppBuilder(builder)
            .WithInterFont()
            .LogToTrace();
    }

    protected override void OnCreate(global::Android.OS.Bundle? savedInstanceState)
    {
        // Register Android-specific services before base.OnCreate triggers Avalonia init
        App.RegisterPlatformServices = services =>
        {
            services.AddSingleton<IImmersiveModeService>(new AndroidImmersiveModeService(this));
        };

        base.OnCreate(savedInstanceState);

        if (global::Android.OS.Build.VERSION.SdkInt < global::Android.OS.BuildVersionCodes.Q)
        {
            if (ContextCompat.CheckSelfPermission(this, global::Android.Manifest.Permission.WriteExternalStorage)
                != Permission.Granted)
            {
                ActivityCompat.RequestPermissions(this,
                    new[] { global::Android.Manifest.Permission.WriteExternalStorage,
                            global::Android.Manifest.Permission.ReadExternalStorage }, 1);
            }
        }
    }
}
