using System.Collections.Generic;

namespace Remex.Core.Models.IPC;

public record CommandRequest(string Action, Dictionary<string, string>? Parameters, string? AuthKey = null);
