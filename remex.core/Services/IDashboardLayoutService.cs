using System.Threading.Tasks;
using Remex.Core.Models;

namespace Remex.Core.Services;

/// <summary>
/// Loads and saves the dashboard layout profile (card positions, settings).
/// </summary>
public interface IDashboardLayoutService
{
    /// <summary>
    /// Loads the persisted profile, or returns a default profile if none exists.
    /// </summary>
    Task<DashboardProfile> LoadAsync();

    /// <summary>
    /// Persists the given profile to disk (or equivalent storage).
    /// </summary>
    Task SaveAsync(DashboardProfile profile);
}
