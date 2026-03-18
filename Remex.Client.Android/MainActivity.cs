using Android;
using Android.App;
using Android.Content.PM;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using Avalonia;
using Avalonia.Android;
using Remex.Client;

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
