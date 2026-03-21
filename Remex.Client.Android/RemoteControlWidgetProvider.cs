using Android.App;
using Android.Appwidget;
using Android.Content;
using Android.Widget;
using Microsoft.Extensions.DependencyInjection;
using Remex.Client.ViewModels;
using System.Collections.Generic;
using System.Linq;
using ViewStates = global::Android.Views.ViewStates;

namespace Remex.Client.Android;

[BroadcastReceiver(Name = "com.remex.client.RemoteControlWidgetProvider", Label = "Remex Remote Control", Exported = true)]
public class RemoteControlWidgetProvider : AppWidgetProvider
{
    public const string ActionWidgetCommand = "com.remex.client.ACTION_WIDGET_COMMAND";
    public const string ExtraCommandName = "extra_command_name";

    public override void OnUpdate(Context? context, AppWidgetManager? appWidgetManager, int[]? appWidgetIds)
    {
        global::Android.Util.Log.Debug("RemexWidget", "RemoteControlWidgetProvider OnUpdate");
        if (context == null || appWidgetManager == null || appWidgetIds == null) return;

        AvaloniaInitHelper.EnsureInitialized();
        if (App.Services != null)
        {
            var connVm = App.Services.GetRequiredService<ConnectionViewModel>();
            if (!connVm.IsConnected) _ = connVm.AutoConnectAsync();
        }

        var settingsManager = new WidgetSettingsManager(context);

        foreach (var appWidgetId in appWidgetIds)
        {
            var views = new RemoteViews(context.PackageName, Resource.Layout.remote_control_widget);
            var selectedActions = settingsManager.GetWidgetActions(appWidgetId);

            if (selectedActions.Count == 0)
            {
                selectedActions = new List<string> { "Lock", "Sleep", "Shutdown" };
            }

            int[] btnIds = { Resource.Id.btn_action_1, Resource.Id.btn_action_2, Resource.Id.btn_action_3, Resource.Id.btn_action_4 };

            for (int i = 0; i < 4; i++)
            {
                if (i < selectedActions.Count)
                {
                    var cmd = selectedActions[i];
                    views.SetTextViewText(btnIds[i], cmd);
                    views.SetViewVisibility(btnIds[i], ViewStates.Visible);

                    var intent = new Intent(context, typeof(RemoteControlWidgetProvider));
                    intent.SetAction(ActionWidgetCommand);
                    intent.PutExtra(ExtraCommandName, cmd);
                    
                    // Use command name as request code to keep intents unique
                    var pendingIntent = PendingIntent.GetBroadcast(context, cmd.GetHashCode(), intent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
                    views.SetOnClickPendingIntent(btnIds[i], pendingIntent);
                }
                else
                {
                    views.SetViewVisibility(btnIds[i], ViewStates.Gone);
                }
            }

            appWidgetManager.UpdateAppWidget(appWidgetId, views);
        }
    }

    public override void OnReceive(Context? context, Intent? intent)
    {
        base.OnReceive(context, intent);
        global::Android.Util.Log.Debug("RemexWidget", $"RemoteControlWidgetProvider OnReceive: {intent?.Action}");

        if (intent != null && intent.Action == ActionWidgetCommand)
        {
            AvaloniaInitHelper.EnsureInitialized();
            var cmd = intent.GetStringExtra(ExtraCommandName);
            global::Android.Util.Log.Debug("RemexWidget", $"Executing command: {cmd}");
            if (string.IsNullOrEmpty(cmd)) return;

            ExecuteCommand(cmd);
        }
    }

    private async void ExecuteCommand(string cmd)
    {
        if (App.Services == null) return;
        var connVm = App.Services.GetRequiredService<ConnectionViewModel>();

        if (cmd.StartsWith("Launch: "))
        {
            var appName = cmd.Substring("Launch: ".Length);
            var launcherStorage = App.Services.GetRequiredService<Remex.Core.Services.ILauncherStorageService>();
            var launchers = await launcherStorage.LoadEntriesAsync();
            var app = launchers.FirstOrDefault(l => l.DisplayName == appName);
            if (app != null)
            {
                var p = new System.Collections.Generic.Dictionary<string, string> { { "TargetPath", app.TargetPath } };
                await connVm.SendCommandAsync("LaunchApp", p);
            }
            return;
        }

        // Map widget text to actual commands
        switch (cmd.ToLower())
        {
            case "lock": await connVm.LockCommandAsync(); break;
            case "sleep": await connVm.SleepCommandAsync(); break;
            case "shutdown": await connVm.ShutdownCommandAsync(); break;
            case "restart": await connVm.RestartCommandAsync(); break;
            case "wol": await connVm.WakeOnLanCommandAsync(); break;
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

