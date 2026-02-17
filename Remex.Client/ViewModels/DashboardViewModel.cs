using System.Collections.ObjectModel;
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
}
