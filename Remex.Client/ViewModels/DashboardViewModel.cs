using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Remex.Client.ViewModels;

/// <summary>
/// Top-level ViewModel for the dashboard. Owns the connection logic and
/// exposes card-oriented data for the UI.
/// </summary>
public partial class DashboardViewModel : ObservableObject
{
    /// <summary>
    /// The shared connection/ping-pong ViewModel — all cards bind into this.
    /// </summary>
    public ConnectionViewModel Connection { get; } = new();

    /// <summary>
    /// Collection bound to the TabControl, holding sensors grouped by Category.
    /// </summary>
    public ObservableCollection<SensorGroupViewModel> CategorizedSensors { get; } = new();

    public DashboardViewModel()
    {
        Connection.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(Connection.Telemetry) && Connection.Telemetry != null)
            {
                ProcessTelemetry(Connection.Telemetry);
            }
        };
    }

    private void ProcessTelemetry(Remex.Core.Messages.TelemetryPayload payload)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (payload.Sensors == null) return;

            foreach (var reading in payload.Sensors)
            {
                var categoryKey = string.IsNullOrWhiteSpace(reading.Category) ? "Other" : reading.Category;

                // Find or create the group
                var group = CategorizedSensors.FirstOrDefault(g => g.CategoryName == categoryKey);
                if (group == null)
                {
                    group = new SensorGroupViewModel { CategoryName = categoryKey };
                    CategorizedSensors.Add(group);
                }

                // Find or create the sensor VM
                var sensorVm = group.Sensors.FirstOrDefault(s => s.Name == reading.Name);
                if (sensorVm == null)
                {
                    sensorVm = new SensorViewModel();
                    group.Sensors.Add(sensorVm);
                }

                // Push new data and advance sparkline array
                sensorVm.Update(reading);
            }
        });
    }
}
