using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace Remex.Core.Validation;

/// <summary>
/// Custom validation attributes for RemEx domain objects.
/// </summary>
public class ValidWebSocketUriAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (value is not string uriString || string.IsNullOrWhiteSpace(uriString))
            return ValidationResult.Success; // Let [Required] handle null/empty

        if (!Uri.TryCreate(uriString, UriKind.Absolute, out var uri))
            return new ValidationResult("Invalid URI format. Expected format: ws://hostname:port/path");

        if (uri.Scheme != "ws" && uri.Scheme != "wss")
            return new ValidationResult("URI must use ws:// or wss:// scheme");

        return ValidationResult.Success;
    }
}

public class ValidMacAddressAttribute : ValidationAttribute
{
    private static readonly Regex MacAddressRegex = new(
        @"^([0-9A-Fa-f]{2}[:-]){5}([0-9A-Fa-f]{2})$",
        RegexOptions.Compiled);

    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (value is not string mac || string.IsNullOrWhiteSpace(mac))
            return ValidationResult.Success;

        if (!MacAddressRegex.IsMatch(mac))
            return new ValidationResult(
                "Invalid MAC address format. Expected format: AA:BB:CC:DD:EE:FF or AA-BB-CC-DD-EE-FF");

        return ValidationResult.Success;
    }
}

public class ValidIpAddressAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (value is not string ip || string.IsNullOrWhiteSpace(ip))
            return ValidationResult.Success;

        if (!System.Net.IPAddress.TryParse(ip, out _))
            return new ValidationResult(
                "Invalid IP address format. Expected format: 192.168.1.1 or ::1");

        return ValidationResult.Success;
    }
}

public class ValidPortAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (value is not int port)
            return ValidationResult.Success;

        if (port < 1 || port > 65535)
            return new ValidationResult("Port must be between 1 and 65535");

        return ValidationResult.Success;
    }
}

/// <summary>
/// Validates that a network hostname is valid (DNS name or IP address).
/// </summary>
public class ValidHostnameAttribute : ValidationAttribute
{
    private static readonly Regex DnsNameRegex = new(
        @"^(?=.{1,253}$)(?:(?!-)[A-Za-z0-9-]{1,63}(?<!-)\.)*(?!-)[A-Za-z0-9-]{1,63}(?<!-)$",
        RegexOptions.Compiled);

    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (value is not string hostname || string.IsNullOrWhiteSpace(hostname))
            return ValidationResult.Success;

        // Try parsing as IP address first
        if (System.Net.IPAddress.TryParse(hostname, out _))
            return ValidationResult.Success;

        // Validate as DNS hostname
        if (!DnsNameRegex.IsMatch(hostname))
            return new ValidationResult(
                "Invalid hostname. Must be a valid DNS name or IP address.");

        return ValidationResult.Success;
    }
}
