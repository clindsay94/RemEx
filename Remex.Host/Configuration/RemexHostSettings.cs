namespace Remex.Host.Configuration;

/// <summary>
/// Configuration settings for RemEx Host service.
/// </summary>
public class RemexHostSettings
{
    public const string SectionName = "RemexHost";

    /// <summary>
    /// Network settings
    /// </summary>
    public NetworkSettings Network { get; set; } = new();

    /// <summary>
    /// Telemetry settings
    /// </summary>
    public TelemetrySettings Telemetry { get; set; } = new();

    /// <summary>
    /// Security settings
    /// </summary>
    public SecuritySettings Security { get; set; } = new();
}

public class NetworkSettings
{
    /// <summary>
    /// Port to listen on for WebSocket connections (default: 5005)
    /// </summary>
    public int Port { get; set; } = 5005;

    /// <summary>
    /// Bind address (default: 0.0.0.0 for all interfaces)
    /// </summary>
    public string BindAddress { get; set; } = "0.0.0.0";

    /// <summary>
    /// Maximum concurrent connections (default: 10)
    /// </summary>
    public int MaxConnections { get; set; } = 10;

    /// <summary>
    /// Connection timeout in seconds (default: 300)
    /// </summary>
    public int ConnectionTimeoutSeconds { get; set; } = 300;
}

public class TelemetrySettings
{
    /// <summary>
    /// Telemetry collection interval in milliseconds (default: 1000)
    /// </summary>
    public int CollectionIntervalMs { get; set; } = 1000;

    /// <summary>
    /// Enable HWiNFO integration on Windows (default: true)
    /// </summary>
    public bool EnableHWiNFO { get; set; } = true;

    /// <summary>
    /// Fallback to WMI/Performance Counters if HWiNFO unavailable (default: true)
    /// </summary>
    public bool EnableFallback { get; set; } = true;
}

public class SecuritySettings
{
    /// <summary>
    /// Require access key for connections (default: true)
    /// </summary>
    public bool RequireAccessKey { get; set; } = true;

    /// <summary>
    /// Access key for client authentication (leave empty to generate on startup)
    /// </summary>
    public string AccessKey { get; set; } = string.Empty;

    /// <summary>
    /// Allow connections from localhost only (default: false)
    /// </summary>
    public bool LocalhostOnly { get; set; } = false;
}
