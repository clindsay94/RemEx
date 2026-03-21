using Android.App;
using Android.Appwidget;
using Android.Content;
using Android.Widget;
using Microsoft.Extensions.DependencyInjection;
using Remex.Client.ViewModels;
using System.Linq;
using ViewStates = global::Android.Views.ViewStates;

namespace Remex.Client.Android;

[BroadcastReceiver(Name = "com.remex.client.SensorWidgetProvider", Label = "Remex Sensors", Exported = true)]
public class SensorWidgetProvider : AppWidgetProvider
{
    public override void OnUpdate(Context? context, AppWidgetManager? appWidgetManager, int[]? appWidgetIds)
    {
        global::Android.Util.Log.Debug("RemexWidget", "SensorWidgetProvider OnUpdate called");
        if (context == null || appWidgetManager == null || appWidgetIds == null) return;
        
        AvaloniaInitHelper.EnsureInitialized();

        if (App.Services == null)
        {
            global::Android.Util.Log.Debug("RemexWidget", "App.Services is still NULL after init");
            return;
        }
        var connVm = App.Services.GetRequiredService<ConnectionViewModel>();
        
        if (!connVm.IsConnected)
        {
            global::Android.Util.Log.Debug("RemexWidget", "Connection not active, triggering AutoConnect");
            _ = connVm.AutoConnectAsync();
        }

        var telemetry = connVm.Telemetry;
        global::Android.Util.Log.Debug("RemexWidget", $"Telemetry is {(telemetry == null ? "NULL" : "Available")}");
        var settingsManager = new WidgetSettingsManager(context);

        foreach (var appWidgetId in appWidgetIds)
        {
            var views = new RemoteViews(context.PackageName, Resource.Layout.sensor_widget);
            var selectedSensors = settingsManager.GetWidgetSensors(appWidgetId);

            if (selectedSensors.Count == 0)
            {
                // Default sensors if none configured
                selectedSensors = new System.Collections.Generic.List<string> { "CPU Load", "GPU Load", "RAM Used" };
            }

            int[] viewIds = { Resource.Id.sensor_1, Resource.Id.sensor_2, Resource.Id.sensor_3, Resource.Id.sensor_4 };

            for (int i = 0; i < 4; i++)
            {
                if (i < selectedSensors.Count)
                {
                    var sensorName = selectedSensors[i].Trim();
                    var sensor = telemetry?.Sensors.FirstOrDefault(s => 
                        string.Equals(s.Name.Trim(), sensorName, System.StringComparison.OrdinalIgnoreCase));
                    
                    var sensorValue = sensor?.Value ?? 0;
                    var unit = sensor?.Unit ?? "";
                    
                    views.SetTextViewText(viewIds[i], $"{sensorName}: {sensorValue:F1} {unit}");
                    views.SetViewVisibility(viewIds[i], ViewStates.Visible);
                }
                else
                {
                    views.SetViewVisibility(viewIds[i], ViewStates.Gone);
                }
            }

            appWidgetManager.UpdateAppWidget(appWidgetId, views);
        }
    }

    public override void OnDeleted(Context? context, int[]? appWidgetIds)
    {
        if (context == null || appWidgetIds == null) return;
        var settingsManager = new WidgetSettingsManager(context);
        foreach (var id in appWidgetIds)
            settingsManager.DeleteWidgetSettings(id);
    }
}
