using System.Collections.Generic;

namespace Remex.Core.Models.IPC;

/// <summary>
/// A command issued over the external TCP command channel (port 8338).
/// </summary>
/// <param name="Action">The command verb (e.g. <c>SHUTDOWN</c>, <c>RESTART</c>, <c>WAKEONLAN</c>).</param>
/// <param name="Parameters">Optional command parameters keyed by name.</param>
/// <param name="ClientId">
/// Identifier of the paired client issuing the command. The 8338 channel uses server-only TLS,
/// so this application-layer identity is the authentication credential: the listener rejects any
/// request whose <c>ClientId</c> is not registered as a paired client (default-deny), mirroring the
/// pairing gate on the <c>/ws</c> WebSocket channel.
/// </param>
public record CommandRequest(string Action, Dictionary<string, string>? Parameters, string? ClientId = null);
