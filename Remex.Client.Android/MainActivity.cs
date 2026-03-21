using Android;
using Android.App;
using Android.Appwidget;
using Android.Content;
using Android.Content.PM;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using Avalonia;
using Avalonia.Android;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Generic;
using Remex.Client;
using Remex.Client.Android.Services;
using Remex.Client.Services;
using Google.Android.Material.Color;
using Android.Graphics;

namespace Remex.Client.Android;

[Activity(
    Name = "com.remex.client.MainActivity",
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
        // Apply Material You dynamic colors to the activity context
        DynamicColors.ApplyToActivityIfAvailable(this);

        // Register Android-specific services before base.OnCreate triggers Avalonia init
        App.RegisterPlatformServices = services =>
        {
            services.AddSingleton<IImmersiveModeService>(new AndroidImmersiveModeService(this));
        };

        base.OnCreate(savedInstanceState);

        // Link shared Avalonia update requests to Android broadcasts
        App.RequestPlatformWidgetUpdate = () =>
        {
            RequestWidgetUpdate(this);
        };

        // Extract and apply dynamic colors to Avalonia after it's initialized
        ApplyDynamicColorsToAvalonia();

        RequestPermissions();
    }

    private void ApplyDynamicColorsToAvalonia()
    {
        try
        {
            // Material 3 Dynamic Color Attributes
            int[] attrs = {
                global::Android.Resource.Attribute.ColorPrimary,
                global::Android.Resource.Attribute.ColorSecondary
            };

            var typedArray = ObtainStyledAttributes(attrs);
            var primaryInt = typedArray.GetColor(0, global::Android.Graphics.Color.Purple);
            typedArray.Recycle();

            var color = new global::Android.Graphics.Color(primaryInt);
            var avaloniaColor = Avalonia.Media.Color.FromArgb(
                color.A,
                color.R,
                color.G,
                color.B);

            var themeService = App.Services?.GetService<ThemeService>();
            if (themeService != null)
            {
                themeService.SetResourceOverride("AccentPrimary", avaloniaColor);
                themeService.SetResourceOverride("AccentPrimaryBrush", new Avalonia.Media.SolidColorBrush(avaloniaColor));
            }
        }
        catch
        {
            // Fallback if dynamic colors fail
        }
    }

    private void RequestPermissions()
    {
        var permissions = new List<string>();

        // Handle storage permissions for older Android versions
        if (global::Android.OS.Build.VERSION.SdkInt < global::Android.OS.BuildVersionCodes.Q)
        {
            if (ContextCompat.CheckSelfPermission(this, global::Android.Manifest.Permission.WriteExternalStorage)
                != Permission.Granted)
            {
                permissions.Add(global::Android.Manifest.Permission.WriteExternalStorage);
                permissions.Add(global::Android.Manifest.Permission.ReadExternalStorage);
            }
        }

        // Handle notification permission for Android 13+
        if (global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.Tiramisu)
        {
            if (ContextCompat.CheckSelfPermission(this, global::Android.Manifest.Permission.PostNotifications)
                != Permission.Granted)
            {
                permissions.Add(global::Android.Manifest.Permission.PostNotifications);
            }
        }

        if (permissions.Count > 0)
        {
            ActivityCompat.RequestPermissions(this, permissions.ToArray(), 1);
        }
    }

    public override void OnTrimMemory(TrimMemory level)
    {
        base.OnTrimMemory(level);
        if (level == TrimMemory.UiHidden || level == TrimMemory.Complete)
        {
            // Flush profile saves when app is backgrounded
            var layoutService = App.Services?.GetService<DashboardLayoutService>();
            if (layoutService != null)
            {
                _ = layoutService.FlushAsync();
            }
        }
    }

    public static void RequestWidgetUpdate(global::Android.Content.Context context)
    {
        var appWidgetManager = AppWidgetManager.GetInstance(context);
        
        int[] ids = appWidgetManager.GetAppWidgetIds(new ComponentName(context, "com.remex.client.SensorWidgetProvider"));
        if (ids.Length > 0)
        {
            var intent = new Intent(context, typeof(SensorWidgetProvider));
            intent.SetAction(AppWidgetManager.ActionAppwidgetUpdate);
            intent.PutExtra(AppWidgetManager.ExtraAppwidgetIds, ids);
            context.SendBroadcast(intent);
        }

        ids = appWidgetManager.GetAppWidgetIds(new ComponentName(context, "com.remex.client.ResourceWidgetProvider"));
        if (ids.Length > 0)
        {
            var intent = new Intent(context, typeof(ResourceWidgetProvider));
            intent.SetAction(AppWidgetManager.ActionAppwidgetUpdate);
            intent.PutExtra(AppWidgetManager.ExtraAppwidgetIds, ids);
            context.SendBroadcast(intent);
        }

        ids = appWidgetManager.GetAppWidgetIds(new ComponentName(context, "com.remex.client.RemexWidgetProvider"));
        if (ids.Length > 0)
        {
            var intent = new Intent(context, typeof(RemexWidgetProvider));
            intent.SetAction(AppWidgetManager.ActionAppwidgetUpdate);
            intent.PutExtra(AppWidgetManager.ExtraAppwidgetIds, ids);
            context.SendBroadcast(intent);
        }
    }
}
