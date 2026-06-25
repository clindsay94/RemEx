namespace Remex.Core.Models;

public sealed record ProcessKillResult(bool Success, string Message, bool NeedsElevation = false);
