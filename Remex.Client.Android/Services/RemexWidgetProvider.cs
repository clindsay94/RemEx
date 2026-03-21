using Android.App;
using Android.Appwidget;
using Android.Content;
using Android.Widget;
using Remex.Client.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using System.Linq;

namespace Remex.Client.Android.Services;

[BroadcastReceiver(Name = "com.remex.client.RemexWidgetProvider",
                 Label = "Remex Telemetry",
                 Exported = true)]
public class RemexWidgetProvider : AppWidgetProvider
{
    public override void OnUpdate(Context? context, AppWidgetManager? appWidgetManager, int[]? appWidgetIds)
    {
        if (context == null || appWidgetManager == null || appWidgetIds == null) return;
        
        AvaloniaInitHelper.EnsureInitialized();
        if (App.Services == null) return;
        var connVm = App.Services.GetRequiredService<ConnectionViewModel>();
        if (!connVm.IsConnected) _ = connVm.AutoConnectAsync();

        var telemetry = connVm.Telemetry;

        foreach (var appWidgetId in appWidgetIds)

        {
            var views = new RemoteViews(context.PackageName, Resource.Layout.remex_widget);

            if (telemetry != null)
            {
                var cpu = telemetry.Sensors.FirstOrDefault(s => s.Name.Contains("CPU Load"))?.Value ?? 0;
                var ram = telemetry.Sensors.FirstOrDefault(s => s.Name.Contains("RAM Used"))?.Value ?? 0;
                var gpu = telemetry.Sensors.FirstOrDefault(s => s.Name.Contains("GPU Load"))?.Value ?? 0;

                views.SetTextViewText(Resource.Id.widget_cpu, $"CPU: {cpu:F0}%");
                views.SetTextViewText(Resource.Id.widget_ram, $"RAM: {ram:F0}%");
                views.SetTextViewText(Resource.Id.widget_gpu, $"GPU: {gpu:F0}%");
            }
            else
            {
                views.SetTextViewText(Resource.Id.widget_cpu, "CPU: --");
                views.SetTextViewText(Resource.Id.widget_ram, "RAM: --");
                views.SetTextViewText(Resource.Id.widget_gpu, "GPU: --");
            }

            appWidgetManager.UpdateAppWidget(appWidgetId, views);
        }
    }
}
