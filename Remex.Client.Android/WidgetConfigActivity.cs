using Android.App;
using Android.Appwidget;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;
using Microsoft.Extensions.DependencyInjection;
using Remex.Client.ViewModels;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Remex.Client.Android;

[Activity(Name = "com.remex.client.WidgetConfigActivity", Label = "Configure Widget", Theme = "@style/MyTheme.NoActionBar", Exported = true)]
public class WidgetConfigActivity : Activity
{
    private int _widgetId = AppWidgetManager.InvalidAppwidgetId;
    private WidgetSettingsManager? _settingsManager;
    private ListView? _itemsList;
    private List<string> _availableItems = new();
    private List<string> _selectedItems = new();
    private string _widgetType = "Sensor";

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        global::Android.Util.Log.Debug("RemexWidget", "WidgetConfigActivity OnCreate called");
        SetContentView(Resource.Layout.widget_config);

        _settingsManager = new WidgetSettingsManager(this);

        // Get widget ID from intent
        var extras = Intent?.Extras;
        if (extras != null)
        {
            _widgetId = extras.GetInt(AppWidgetManager.ExtraAppwidgetId, AppWidgetManager.InvalidAppwidgetId);
        }
        global::Android.Util.Log.Debug("RemexWidget", $"Configuring Widget ID: {_widgetId}");

        if (_widgetId == AppWidgetManager.InvalidAppwidgetId)
        {
            Finish();
            return;
        }

        // Set result to cancelled by default (if user backs out)
        SetResult(Result.Canceled);

        _itemsList = FindViewById<ListView>(Resource.Id.items_list);
        var saveBtn = FindViewById<Button>(Resource.Id.save_widget_btn);
        var title = FindViewById<TextView>(Resource.Id.config_title);

        // Detect widget type based on the provider
        var widgetManager = AppWidgetManager.GetInstance(this);
        var info = widgetManager.GetAppWidgetInfo(_widgetId);
        var providerName = info?.Provider?.ClassName ?? "";
        global::Android.Util.Log.Debug("RemexWidget", $"Provider ClassName: {providerName}");

        if (providerName.EndsWith("SensorWidgetProvider"))
        {
            _widgetType = "Sensor";
            title.Text = "Configure Sensor Widget";
            LoadAvailableSensors();
        }
        else if (providerName.EndsWith("RemoteControlWidgetProvider"))
        {
            _widgetType = "Remote";
            title.Text = "Configure Remote Control";
            LoadAvailableActions();
        }
        else if (providerName.EndsWith("ResourceWidgetProvider"))
        {
            _widgetType = "Resource";
            title.Text = "Configure Resource Widget";
            _availableItems = new List<string> { "Top 3 CPU Processes", "Top 3 RAM Processes" };
        }
        else
        {
            global::Android.Util.Log.Debug("RemexWidget", "Unknown provider, defaulting to Sensor list logic");
            _widgetType = "Sensor";
            LoadAvailableSensors();
        }

        var adapter = new ArrayAdapter<string>(this, global::Android.Resource.Layout.SimpleListItemMultipleChoice, _availableItems);
        if (_itemsList != null)
        {
            _itemsList.Adapter = adapter;
            _itemsList.ChoiceMode = ChoiceMode.Multiple;
        }

        saveBtn.Click += (s, e) => SaveAndFinish();
    }

    private void LoadAvailableSensors()
    {
        if (App.Services == null) return;
        var connVm = App.Services.GetRequiredService<ConnectionViewModel>();
        if (connVm.Telemetry != null)
        {
            _availableItems = connVm.Telemetry.Sensors.Select(s => s.Name).ToList();
        }
        else
        {
            _availableItems = new List<string> { "CPU Load", "GPU Load", "RAM Used", "System Temperature" };
        }
    }

    private void LoadAvailableActions()
    {
        _availableItems = new List<string> { "Lock", "Sleep", "Shutdown", "Restart", "WOL" };
        
        // Also add launcher apps to the list
        if (App.Services != null)
        {
            var launcherStorage = App.Services.GetRequiredService<Remex.Core.Services.ILauncherStorageService>();
            var launchers = Task.Run(async () => await launcherStorage.LoadEntriesAsync()).GetAwaiter().GetResult();
            foreach (var app in launchers)
            {
                _availableItems.Add($"Launch: {app.DisplayName}");
            }
        }
    }

    private void SaveAndFinish()
    {
        if (_itemsList == null || _settingsManager == null) return;

        var checkedItems = _itemsList.CheckedItemPositions;
        for (int i = 0; i < _availableItems.Count; i++)
        {
            if (checkedItems.Get(i))
            {
                _selectedItems.Add(_availableItems[i]);
            }
        }

        if (_widgetType == "Sensor")
            _settingsManager.SaveWidgetSensors(_widgetId, _selectedItems.Take(4).ToList());
        else if (_widgetType == "Remote")
            _settingsManager.SaveWidgetActions(_widgetId, _selectedItems.Take(4).ToList());
        else if (_widgetType == "Resource")
        {
            // For resource, we take the first selection as the mode (CPU or RAM)
            var mode = _selectedItems.FirstOrDefault() ?? "Top 3 CPU Processes";
            _settingsManager.SaveWidgetMode(_widgetId, mode);
        }

        // Notify widget update
        var appWidgetManager = AppWidgetManager.GetInstance(this);
        var updateIntent = new Intent(AppWidgetManager.ActionAppwidgetUpdate);
        updateIntent.PutExtra(AppWidgetManager.ExtraAppwidgetIds, new[] { _widgetId });
        SendBroadcast(updateIntent);

        // Success!
        var resultValue = new Intent();
        resultValue.PutExtra(AppWidgetManager.ExtraAppwidgetId, _widgetId);
        SetResult(Result.Ok, resultValue);
        Finish();
    }
}
