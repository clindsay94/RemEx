using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Remex.Core.Messages;
using Remex.Core.Services;

namespace Remex.Agent.Services.Telemetry;

/// <summary>
/// A background service that polls telemetry data periodically and caches the latest payload.
/// This prevents redundant system scans when multiple clients are connected.
/// </summary>
public sealed class TelemetryBackgroundService(
    ITelemetryService telemetryService,
    ILogger<TelemetryBackgroundService> logger) : BackgroundService
{
    /// <summary>
    /// Gets the most recent telemetry data retrieved from the system.
    /// </summary>
    public TelemetryPayload? CurrentPayload { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Telemetry background broadcaster started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                CurrentPayload = await telemetryService.GetTelemetryAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error polling telemetry data.");
            }

            // Poll every 1 second
            await Task.Delay(1000, stoppingToken);
        }

        logger.LogInformation("Telemetry background broadcaster stopped.");
    }
}
