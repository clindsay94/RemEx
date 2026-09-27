using Tmds.DBus.Protocol;

namespace Remex.Agent.Services.Routines;

/// <summary>Finds this PC's idle and session sources (routines spec §8.5.2, §8.5.3).</summary>
public interface IRoutinePlatformSources
{
    /// <summary>The first idle source that answers, in the §8.5.2 probe order; null when none does (§17 Q5).</summary>
    Task<IIdleSource?> ResolveIdleAsync(CancellationToken ct);

    /// <summary>The first session source that starts, in the §8.5.3 order; null when none does.</summary>
    Task<ISessionStateSource?> ResolveSessionAsync(CancellationToken ct);
}

/// <summary>
/// The real sources. Windows: <c>win32.lastinput</c> and <c>win32.wts</c>. Linux: Mutter IdleMonitor, then
/// org.freedesktop.ScreenSaver, then libXss, then logind's idle hint; logind <c>LockedHint</c>, then the
/// ScreenSaver <c>ActiveChanged</c> signals.
/// </summary>
/// <remarks>
/// No <c>/dev/input</c> polling, ever (§17 Q5): a Linux desktop none of these answer on has no idle trigger,
/// and the sync rejects <c>pc.idle</c> there with <c>idle_source_unavailable</c>.
/// </remarks>
public sealed class RoutinePlatformSources(ILoggerFactory loggers) : IRoutinePlatformSources
{
    private readonly ILogger _logger = loggers.CreateLogger<RoutinePlatformSources>();

    /// <inheritdoc />
    public async Task<IIdleSource?> ResolveIdleAsync(CancellationToken ct)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var windows = new Win32LastInputIdleSource();
                return await windows.GetIdleAsync(ct) is not null ? windows : null;
            }

            if (OperatingSystem.IsLinux())
            {
                return await ResolveLinuxIdleAsync(ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Probing for an idle source failed; pc.idle is unavailable on this PC.");
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<ISessionStateSource?> ResolveSessionAsync(CancellationToken ct)
    {
        var candidates = new List<Func<ISessionStateSource>>();
        if (OperatingSystem.IsWindows())
        {
            candidates.Add(() => new WtsSessionStateSource(loggers.CreateLogger<WtsSessionStateSource>()));
        }
        else if (OperatingSystem.IsLinux())
        {
            var logger = loggers.CreateLogger<RoutinePlatformSources>();
            candidates.Add(() => new LogindLockedHintSource(logger));
            candidates.Add(() => new ScreenSaverSessionSource(
                "freedesktop.screensaver", "org.freedesktop.ScreenSaver", "/org/freedesktop/ScreenSaver", logger));
            candidates.Add(() => new ScreenSaverSessionSource(
                "gnome.screensaver", "org.gnome.ScreenSaver", "/org/gnome/ScreenSaver", logger));
        }

        foreach (var create in candidates)
        {
            ISessionStateSource? source = null;
            try
            {
                source = create();
                if (await source.StartAsync(ct))
                {
                    _logger.LogInformation("Routine session source: {Source}.", source.Id);
                    return source;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "A session source failed to start.");
            }

            source?.Dispose();
        }

        _logger.LogInformation("No lock/unlock source on this PC; pc.session routines are unavailable.");
        return null;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private async Task<IIdleSource?> ResolveLinuxIdleAsync(CancellationToken ct)
    {
        var session = await RoutineDbus.ConnectAsync(DBusAddress.Session, _logger);
        if (session is not null)
        {
            foreach (var candidate in new IIdleSource[] { new MutterIdleSource(session), new ScreenSaverIdleSource(session) })
            {
                if (await candidate.GetIdleAsync(ct) is not null)
                {
                    _logger.LogInformation("Routine idle source: {Source}.", candidate.Id);
                    return candidate;
                }
            }

            session.Dispose();
        }

        var xss = XssIdleSource.TryCreate();
        if (xss is not null)
        {
            if (await xss.GetIdleAsync(ct) is not null)
            {
                _logger.LogInformation("Routine idle source: {Source}.", xss.Id);
                return xss;
            }

            xss.Dispose();
        }

        var system = await RoutineDbus.ConnectAsync(DBusAddress.System, _logger);
        if (system is not null)
        {
            var logind = new LogindIdleHintSource(system, await RoutineDbus.SessionPathAsync(system));
            if (await logind.GetIdleAsync(ct) is not null)
            {
                _logger.LogInformation("Routine idle source: {Source} (approximate).", logind.Id);
                return logind;
            }

            system.Dispose();
        }

        _logger.LogInformation("No idle source on this PC; pc.idle routines are unavailable.");
        return null;
    }
}
