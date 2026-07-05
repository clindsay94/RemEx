namespace Remex.Core.Models.IPC;

public record CommandResponse(bool Success, string Message, string? ErrorDetails)
{
    public PairingPinInfo? PairingPinInfo { get; init; }
}
