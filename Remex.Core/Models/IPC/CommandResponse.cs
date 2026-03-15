namespace Remex.Core.Models.IPC;

public record CommandResponse(bool Success, string Message, string? ErrorDetails);
