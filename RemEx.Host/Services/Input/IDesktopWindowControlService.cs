using Remex.Core.Models;

namespace Remex.Host.Services.Input;

public interface IDesktopWindowControlService
{
    DesktopWindowResult QueryWindows(DesktopWindowQuery query);
    DesktopWindowResult ExecuteAction(DesktopWindowAction action);
}

public sealed class UnsupportedDesktopWindowControlService : IDesktopWindowControlService
{
    public DesktopWindowResult QueryWindows(DesktopWindowQuery query) => new()
    {
        RequestId = query.RequestId,
        Success = false,
        ErrorText = "Advanced window control is unavailable on this host.",
    };

    public DesktopWindowResult ExecuteAction(DesktopWindowAction action) => new()
    {
        RequestId = action.RequestId,
        Action = action.Action,
        Success = false,
        ErrorText = "Advanced window control is unavailable on this host.",
    };
}
