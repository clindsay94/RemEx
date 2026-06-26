using System;
using Microsoft.Extensions.DependencyInjection;

namespace Remex.Desktop.Services;

/// <summary>
/// Resolves services from the in-process embedded host's DI container
/// (<see cref="App.EmbeddedHostServices"/>).
///
/// RemEx 2.0 runs the host in the SAME process as the Avalonia UI, so the former Named-Pipe IPC
/// adapters (power commands, Wake-on-LAN, pairing-PIN queries, app launching) now delegate straight
/// to the real host services through this locator instead of opening the <c>RemExLocalIPC</c> pipe.
/// The cross-process pipe, its ACL/identity gate, and <c>LocalIpcServerService</c> were all deleted —
/// there is no second process to bridge to.
/// </summary>
internal static class EmbeddedHostServiceLocator
{
    /// <summary>
    /// Resolves a required service from the embedded host container. Throws a clear, user-facing
    /// <see cref="InvalidOperationException"/> when the host is not available (e.g. it failed to start
    /// and the app degraded to client-only mode), instead of a bare <see cref="NullReferenceException"/>.
    /// </summary>
    public static T Require<T>() where T : notnull
    {
        var services = App.EmbeddedHostServices
            ?? throw new InvalidOperationException(
                "The RemEx host is not running in this process, so this action is unavailable. " +
                "This usually means the in-process host failed to start — check the host logs.");
        return services.GetRequiredService<T>();
    }
}
