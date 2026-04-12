using System.Net;

namespace Remex.Core.Validation;

/// <summary>
/// Static helper methods for network input validation.
/// Provides validation with detailed error messages for UI feedback.
/// </summary>
public static class NetworkValidation
{
    /// <summary>
    /// Validates a WebSocket URI format and scheme.
    /// </summary>
    /// <param name="uri">The URI to validate</param>
    /// <param name="errorMessage">Detailed error message if validation fails</param>
    /// <returns>True if valid, false otherwise</returns>
    public static bool IsValidWebSocketUri(string? uri, out string? errorMessage)
    {
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(uri))
        {
            errorMessage = "WebSocket URI cannot be empty";
            return false;
        }

        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsedUri))
        {
            errorMessage = "Invalid URI format. Expected: ws://hostname:port/path";
            return false;
        }

        if (parsedUri.Scheme != "ws" && parsedUri.Scheme != "wss")
        {
            errorMessage = "URI must start with ws:// or wss://";
            return false;
        }

        if (string.IsNullOrWhiteSpace(parsedUri.Host))
        {
            errorMessage = "URI must include a hostname or IP address";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Validates a MAC address format (supports both : and - separators).
    /// </summary>
    /// <param name="mac">The MAC address to validate</param>
    /// <param name="errorMessage">Detailed error message if validation fails</param>
    /// <returns>True if valid, false otherwise</returns>
    public static bool IsValidMacAddress(string? mac, out string? errorMessage)
    {
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(mac))
        {
            errorMessage = "MAC address cannot be empty";
            return false;
        }

        // Remove separators and validate hex format
        var cleaned = mac.Replace(":", "").Replace("-", "");

        if (cleaned.Length != 12)
        {
            errorMessage = "MAC address must be 12 hex characters (6 octets). Format: AA:BB:CC:DD:EE:FF";
            return false;
        }

        if (!cleaned.All(c => Uri.IsHexDigit(c)))
        {
            errorMessage = "MAC address must contain only hexadecimal characters (0-9, A-F)";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Validates an IP address (supports both IPv4 and IPv6).
    /// </summary>
    /// <param name="ip">The IP address to validate</param>
    /// <param name="errorMessage">Detailed error message if validation fails</param>
    /// <returns>True if valid, false otherwise</returns>
    public static bool IsValidIpAddress(string? ip, out string? errorMessage)
    {
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(ip))
        {
            errorMessage = "IP address cannot be empty";
            return false;
        }

        if (!IPAddress.TryParse(ip, out _))
        {
            errorMessage = "Invalid IP address format. Expected format: 192.168.1.1 or ::1";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Validates a network port number (1-65535).
    /// </summary>
    /// <param name="port">The port number to validate</param>
    /// <param name="errorMessage">Detailed error message if validation fails</param>
    /// <returns>True if valid, false otherwise</returns>
    public static bool IsValidPort(int port, out string? errorMessage)
    {
        errorMessage = null;

        if (port < 1 || port > 65535)
        {
            errorMessage = $"Port must be between 1 and 65535 (got: {port})";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Validates a hostname (DNS name or IP address).
    /// </summary>
    /// <param name="hostname">The hostname to validate</param>
    /// <param name="errorMessage">Detailed error message if validation fails</param>
    /// <returns>True if valid, false otherwise</returns>
    public static bool IsValidHostname(string? hostname, out string? errorMessage)
    {
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(hostname))
        {
            errorMessage = "Hostname cannot be empty";
            return false;
        }

        // Try parsing as IP address first
        if (IPAddress.TryParse(hostname, out _))
            return true;

        // Validate as DNS hostname
        if (hostname.Length > 253)
        {
            errorMessage = "Hostname too long (maximum 253 characters)";
            return false;
        }

        var labels = hostname.Split('.');
        foreach (var label in labels)
        {
            if (label.Length == 0 || label.Length > 63)
            {
                errorMessage = "Invalid hostname: each label must be 1-63 characters";
                return false;
            }

            if (label.StartsWith('-') || label.EndsWith('-'))
            {
                errorMessage = "Invalid hostname: labels cannot start or end with hyphens";
                return false;
            }

            if (!label.All(c => char.IsLetterOrDigit(c) || c == '-'))
            {
                errorMessage = "Invalid hostname: labels can only contain letters, digits, and hyphens";
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Normalizes a MAC address to a standard format (uppercase with colons).
    /// </summary>
    /// <param name="mac">The MAC address to normalize</param>
    /// <returns>Normalized MAC address (AA:BB:CC:DD:EE:FF) or original string if invalid</returns>
    public static string NormalizeMacAddress(string mac)
    {
        if (!IsValidMacAddress(mac, out _))
            return mac;

        var cleaned = mac.Replace(":", "").Replace("-", "").ToUpperInvariant();

        // Insert colons every 2 characters
        return string.Join(":", Enumerable.Range(0, 6)
            .Select(i => cleaned.Substring(i * 2, 2)));
    }
}
