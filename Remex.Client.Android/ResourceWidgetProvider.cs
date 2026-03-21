using Android.App;
using Android.Appwidget;
using Android.Content;
using Android.Widget;
using Microsoft.Extensions.DependencyInjection;
using Remex.Client.ViewModels;
using System.Linq;
using ViewStates = global::Android.Views.ViewStates;

namespace Remex.Client.Android;

[BroadcastReceiver(Name = "com.remex.client.ResourceWidgetProvider", Label = "Remex Resources", Exported = true)]
public class ResourceWidgetProvider : AppWidgetProvider
{
    public override void OnUpdate(Context? context, AppWidgetManager? appWidgetManager, int[]? appWidgetIds)
    {
        global::Android.Util.Log.Debug("RemexWidget", "ResourceWidgetProvider OnUpdate");
        if (context == null || appWidgetManager == null || appWidgetIds == null) return;

        AvaloniaInitHelper.EnsureInitialized();
        if (App.Services == null) return;
        var connVm = App.Services.GetRequiredService<ConnectionViewModel>();
        if (!connVm.IsConnected) _ = connVm.AutoConnectAsync();
        
        // We need to request the process list if it's not fresh
        _ = connVm.RequestProcessListAsync();
        
        var settingsManager = new WidgetSettingsManager(context);
        var mode = settingsManager.GetWidgetMode(appWidgetIds.FirstOrDefault());
        bool isRamMode = mode.Contains("RAM");

        var processes = isRamMode 
            ? connVm.Processes.OrderByDescending(p => p.MemoryUsage).Take(3).ToList()
            : connVm.Processes.OrderByDescending(p => p.CpuUsage).Take(3).ToList();

        foreach (var appWidgetId in appWidgetIds)
        {
            var views = new RemoteViews(context.PackageName, Resource.Layout.resource_widget);
            views.SetTextViewText(Resource.Id.widget_title, isRamMode ? "Top RAM Usage" : "Top CPU Usage");

            int[] viewIds = { Resource.Id.proc_1, Resource.Id.proc_2, Resource.Id.proc_3 };

            if (processes.Count == 0)
            {
                views.SetTextViewText(Resource.Id.proc_1, "No data available");
                views.SetViewVisibility(Resource.Id.proc_2, ViewStates.Gone);
                views.SetViewVisibility(Resource.Id.proc_3, ViewStates.Gone);
            }
            else
            {
                for (int i = 0; i < 3; i++)
                {
                    if (i < processes.Count)
                    {
                        var p = processes[i];
                        var stats = isRamMode ? $"{p.MemoryUsage:F1} MB" : $"{p.CpuUsage:F1}% CPU";
                        views.SetTextViewText(viewIds[i], $"{p.Name}: {stats}");
                        views.SetViewVisibility(viewIds[i], ViewStates.Visible);
                    }
                    else
                    {
                        views.SetViewVisibility(viewIds[i], ViewStates.Gone);
                    }
                }
            }

            appWidgetManager.UpdateAppWidget(appWidgetId, views);
        }
    }
}
