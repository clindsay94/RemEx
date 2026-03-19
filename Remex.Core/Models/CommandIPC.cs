using System;

namespace Remex.Core.Models;

/// <summary>
/// A request sent over the local Named Pipe IPC.
/// </summary>
public record CommandRequest(
    string Action,
    string? TargetPath = null,
    string? AuthKey = null
);

/// <summary>
/// The response from the local Named Pipe IPC.
/// </summary>
public record CommandResponse(
    bool Success,
    string Message
);
