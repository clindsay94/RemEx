using System.Threading;
using System.Threading.Tasks;
using Remex.Core.Messages;

namespace Remex.Core.Services;

/// <summary>
/// Abstract service capable of reading live hardware telemetry (CPU/GPU/RAM).
/// The implementation varies radically per OS (Shared Memory on Windows, /sys/class on Linux).
/// </summary>
public interface ITelemetryService
{
    /// <summary>
    /// Captures a snapshot of the current hardware sensors.
    /// </summary>
    Task<TelemetryPayload> GetTelemetryAsync(CancellationToken ct = default);
}
