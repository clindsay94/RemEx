using Remex.Core.Services;

namespace Remex.Desktop.Configuration;

/// <summary>
/// Configuration settings for RemEx Client application.
/// </summary>
public class RemexClientSettings
{
    public const string SectionName = "RemexClient";

    /// <summary>
    /// Connection settings
    /// </summary>
    public ConnectionSettings Connection { get; set; } = new();

    /// <summary>
    /// UI and visualization settings
    /// </summary>
    public UiSettings Ui { get; set; } = new();

    /// <summary>
    /// File path settings
    /// </summary>
    public PathSettings Paths { get; set; } = new();
}

public class ConnectionSettings
{
    /// <summary>
    /// Maximum latency data points to display (default: 30)
    /// </summary>
    public int MaxLatencyPoints { get; set; } = 30;

    /// <summary>
    /// Maximum reconnect delay in seconds (default: 30)
    /// </summary>
    public int MaxReconnectDelaySeconds { get; set; } = 30;

    /// <summary>
    /// Initial reconnect delay in milliseconds (default: 1000)
    /// </summary>
    public int InitialReconnectDelayMs { get; set; } = 1000;

    /// <summary>
    /// Connection timeout in milliseconds (default: 5000)
    /// </summary>
    public int ConnectionTimeoutMs { get; set; } = 5000;

    /// <summary>
    /// WebSocket ping interval in seconds (default: 30)
    /// </summary>
    public int PingIntervalSeconds { get; set; } = 30;
}

public class UiSettings
{
    /// <summary>
    /// Dashboard refresh interval in milliseconds (default: 1000)
    /// </summary>
    public int RefreshIntervalMs { get; set; } = 1000;

    /// <summary>
    /// Maximum telemetry history points (default: 60)
    /// </summary>
    public int MaxTelemetryHistoryPoints { get; set; } = 60;

    /// <summary>
    /// Enable animations (default: true)
    /// </summary>
    public bool EnableAnimations { get; set; } = true;
}

/// <remarks>
/// The defaults resolve through <see cref="RemexDataPaths.PerUserDirectory"/>, so a test that binds
/// this section — or just constructs it — cannot end up pointing real writes at the developer's own
/// profile (RemEx-ln0k). The production paths are unchanged: that property returns
/// <c>%LocalAppData%/Remex</c> unless the test redirect is set.
/// </remarks>
public class PathSettings
{
    /// <summary>
    /// Application data directory (default: %LocalAppData%/Remex)
    /// </summary>
    public string AppDataDirectory { get; set; } = RemexDataPaths.PerUserDirectory;

    /// <summary>
    /// Dashboard profiles directory
    /// </summary>
    public string DashboardProfilesDirectory { get; set; } =
        Path.Combine(RemexDataPaths.PerUserDirectory, "Profiles");

    /// <summary>
    /// Logs directory
    /// </summary>
    public string LogsDirectory { get; set; } =
        Path.Combine(RemexDataPaths.PerUserDirectory, "Logs");
}
