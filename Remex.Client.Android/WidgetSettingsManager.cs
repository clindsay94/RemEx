using Android.Content;
using System.Collections.Generic;
using System.Linq;

namespace Remex.Client.Android;

public class WidgetSettingsManager
{
    private const string PrefsName = "RemexWidgetSettings";
    private readonly ISharedPreferences? _prefs;

    public WidgetSettingsManager(Context context)
    {
        _prefs = context.GetSharedPreferences(PrefsName, FileCreationMode.Private);
    }

    public void SaveWidgetSensors(int widgetId, List<string> sensorNames)
    {
        _prefs?.Edit()?.PutString($"widget_{widgetId}_sensors", string.Join("|", sensorNames))?.Apply();
    }

    public List<string> GetWidgetSensors(int widgetId)
    {
        var raw = _prefs?.GetString($"widget_{widgetId}_sensors", "") ?? "";
        return string.IsNullOrEmpty(raw) ? new List<string>() : raw.Split('|').ToList();
    }

    public void SaveWidgetActions(int widgetId, List<string> actions)
    {
        _prefs?.Edit()?.PutString($"widget_{widgetId}_actions", string.Join("|", actions))?.Apply();
    }

    public List<string> GetWidgetActions(int widgetId)
    {
        var raw = _prefs?.GetString($"widget_{widgetId}_actions", "") ?? "";
        return string.IsNullOrEmpty(raw) ? new List<string>() : raw.Split('|').ToList();
    }

    public void SaveWidgetMode(int widgetId, string mode)
    {
        _prefs?.Edit()?.PutString($"widget_{widgetId}_mode", mode)?.Apply();
    }

    public string GetWidgetMode(int widgetId)
    {
        return _prefs?.GetString($"widget_{widgetId}_mode", "") ?? "";
    }

    public void DeleteWidgetSettings(int widgetId)
    {
        _prefs?.Edit()?.Remove($"widget_{widgetId}_sensors")?.Remove($"widget_{widgetId}_actions")?.Remove($"widget_{widgetId}_mode")?.Apply();
    }
}
