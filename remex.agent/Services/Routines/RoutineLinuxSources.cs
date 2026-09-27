using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Tmds.DBus.Protocol;

namespace Remex.Agent.Services.Routines;

/// <summary>
/// The platform-free parts of the Linux trigger sources, so they are tested on every OS
/// (<c>LogindSessionSourceTests</c>, <c>RoutinePowerVerbProbeTests</c>).
/// </summary>
public static class LogindParsing
{
    public const string SessionInterface = "org.freedesktop.login1.Session";

    /// <summary>What a <c>PropertiesChanged</c> signal says about <c>LockedHint</c>.</summary>
    /// <param name="Value">The new value when the signal carried it.</param>
    /// <param name="Invalidated">True when the property was invalidated and must be re-read.</param>
    public readonly record struct LockedHintChange(bool? Value, bool Invalidated)
    {
        public bool IsRelevant => Value is not null || Invalidated;
    }

    /// <summary>
    /// Reads <c>LockedHint</c> out of an <c>org.freedesktop.DBus.Properties.PropertiesChanged(s, a{sv}, as)</c>
    /// body, already reduced to plain values (<c>bool</c> for a D-Bus boolean).
    /// </summary>
    /// <remarks>
    /// <b>ONLY THE PROPERTY, NEVER THE <c>Lock</c>/<c>Unlock</c> SIGNALS (docs/REGRESSION-GUARDS.md).</b>
    /// Those are logind asking the screen locker to lock or unlock: a request that may be refused or may
    /// never be acted on. <c>LockedHint</c> is set by the locker once the screen actually is locked, which
    /// is the state a <c>pc.session</c> routine is about.
    /// </remarks>
    public static LockedHintChange ParseLockedHint(
        string? interfaceName, IReadOnlyDictionary<string, object?> changed, IReadOnlyList<string> invalidated)
    {
        if (!string.Equals(interfaceName, SessionInterface, StringComparison.Ordinal))
        {
            return default;
        }

        if (changed.TryGetValue("LockedHint", out var raw) && raw is bool value)
        {
            return new LockedHintChange(value, false);
        }

        return new LockedHintChange(null, invalidated.Contains("LockedHint", StringComparer.Ordinal));
    }

    /// <summary>
    /// Idle time from logind's <c>IdleHint</c> / <c>IdleSinceHint</c> (realtime, microseconds): zero while
    /// not idle. Coarse (the desktop sets the hint only after its own idle delay), which is why it is the
    /// last source in the probe order.
    /// </summary>
    public static TimeSpan IdleFromHint(bool idleHint, ulong idleSinceMicroseconds, DateTimeOffset now)
    {
        if (!idleHint || idleSinceMicroseconds == 0)
        {
            return TimeSpan.Zero;
        }

        var since = DateTimeOffset.UnixEpoch.AddTicks((long)Math.Min(idleSinceMicroseconds * 10, (ulong)long.MaxValue / 2));
        var idle = now - since;
        return idle < TimeSpan.Zero ? TimeSpan.Zero : idle;
    }

    /// <summary>logind's <c>Can*</c> answers mean "go ahead" only when they are exactly <c>yes</c> (§7.5).</summary>
    public static bool IsYes(string? answer) => string.Equals(answer, "yes", StringComparison.Ordinal);
}

/// <summary>Small D-Bus helpers for the routine sources.</summary>
[SupportedOSPlatform("linux")]
internal static class RoutineDbus
{
    public const string LoginService = "org.freedesktop.login1";
    public const string LoginPath = "/org/freedesktop/login1";
    public const string LoginManager = "org.freedesktop.login1.Manager";

    /// <summary>No routine D-Bus call may stall a poll or a start: an activatable service can hang.</summary>
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(2);

    public static async Task<DBusConnection?> ConnectAsync(string? address, ILogger logger)
    {
        if (string.IsNullOrEmpty(address))
        {
            return null;
        }

        try
        {
            var connection = new DBusConnection(address);
            await Task.Run(async () => await connection.ConnectAsync()).WaitAsync(CallTimeout);
            return connection;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "D-Bus connect failed for a routine source.");
            return null;
        }
    }

    public static Task<VariantValue> GetPropertyAsync(DBusConnection connection, string destination, string path, string iface, string name)
    {
        MessageBuffer buffer;
        {
            var writer = connection.GetMessageWriter();
            writer.WriteMethodCallHeader(
                destination: destination,
                path: path,
                @interface: "org.freedesktop.DBus.Properties",
                member: "Get",
                signature: "ss");
            writer.WriteString(iface);
            writer.WriteString(name);
            buffer = writer.CreateMessage();
        }

        return Timed(async () => await connection.CallMethodAsync(
            buffer, static (Message msg, object? state) => msg.GetBodyReader().ReadVariantValue()));
    }

    /// <summary>Runs one call under <see cref="CallTimeout"/>.</summary>
    public static Task<T> Timed<T>(Func<Task<T>> call, CancellationToken ct = default) => call().WaitAsync(CallTimeout, ct);

    /// <summary>This process's logind session object, or the caller's "auto" session when it has none.</summary>
    public static async Task<string> SessionPathAsync(DBusConnection system)
    {
        try
        {
            MessageBuffer buffer;
            {
                var writer = system.GetMessageWriter();
                writer.WriteMethodCallHeader(
                    destination: LoginService, path: LoginPath, @interface: LoginManager,
                    member: "GetSessionByPID", signature: "u");
                writer.WriteUInt32((uint)Environment.ProcessId);
                buffer = writer.CreateMessage();
            }

            return await Timed(async () => await system.CallMethodAsync(
                buffer, static (Message msg, object? state) => msg.GetBodyReader().ReadObjectPathAsString()));
        }
        catch (Exception)
        {
            // A user-service agent is not itself in a session; "auto" resolves to the caller's display session.
            return LoginPath + "/session/auto";
        }
    }

    public static object? Plain(VariantValue value) => value.Type switch
    {
        VariantValueType.Bool => value.GetBool(),
        VariantValueType.UInt64 => value.GetUInt64(),
        VariantValueType.UInt32 => value.GetUInt32(),
        VariantValueType.String => value.GetString(),
        _ => null,
    };
}

/// <summary><c>gnome.idlemonitor</c>: Mutter's <c>GetIdletime</c> (ms) on the session bus.</summary>
[SupportedOSPlatform("linux")]
internal sealed class MutterIdleSource(DBusConnection session) : IIdleSource, IDisposable
{
    public string Id => "gnome.idlemonitor";

    /// <summary>The source owns its session-bus connection; closed on host shutdown.</summary>
    public void Dispose() => session.Dispose();

    public async Task<TimeSpan?> GetIdleAsync(CancellationToken ct)
    {
        try
        {
            MessageBuffer buffer;
            {
                var writer = session.GetMessageWriter();
                writer.WriteMethodCallHeader(
                    destination: "org.gnome.Mutter.IdleMonitor",
                    path: "/org/gnome/Mutter/IdleMonitor/Core",
                    @interface: "org.gnome.Mutter.IdleMonitor",
                    member: "GetIdletime");
                buffer = writer.CreateMessage();
            }

            var ms = await RoutineDbus.Timed(
                async () => await session.CallMethodAsync(buffer, static (Message msg, object? state) => msg.GetBodyReader().ReadUInt64()), ct);
            return TimeSpan.FromMilliseconds(ms);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return null;
        }
    }
}

/// <summary><c>freedesktop.screensaver</c>: <c>GetSessionIdleTime</c> (seconds) on the session bus (KDE and others).</summary>
[SupportedOSPlatform("linux")]
internal sealed class ScreenSaverIdleSource(DBusConnection session) : IIdleSource, IDisposable
{
    public string Id => "freedesktop.screensaver";

    /// <summary>The source owns its session-bus connection; closed on host shutdown.</summary>
    public void Dispose() => session.Dispose();

    public async Task<TimeSpan?> GetIdleAsync(CancellationToken ct)
    {
        foreach (var path in new[] { "/org/freedesktop/ScreenSaver", "/ScreenSaver" })
        {
            try
            {
                MessageBuffer buffer;
                {
                    var writer = session.GetMessageWriter();
                    writer.WriteMethodCallHeader(
                        destination: "org.freedesktop.ScreenSaver",
                        path: path,
                        @interface: "org.freedesktop.ScreenSaver",
                        member: "GetSessionIdleTime");
                    buffer = writer.CreateMessage();
                }

                var seconds = await RoutineDbus.Timed(
                    async () => await session.CallMethodAsync(buffer, static (Message msg, object? state) => msg.GetBodyReader().ReadUInt32()), ct);
                return TimeSpan.FromSeconds(seconds);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Next path; GNOME's implementation of this interface refuses the call outright.
            }
        }

        return null;
    }
}

/// <summary><c>logind.idlehint</c>: the coarse fallback from the session object's idle hints.</summary>
[SupportedOSPlatform("linux")]
internal sealed class LogindIdleHintSource(DBusConnection system, string sessionPath) : IIdleSource, IDisposable
{
    public string Id => "logind.idlehint";

    /// <summary>The source owns its system-bus connection; closed on host shutdown.</summary>
    public void Dispose() => system.Dispose();

    public async Task<TimeSpan?> GetIdleAsync(CancellationToken ct)
    {
        try
        {
            var hint = await RoutineDbus.GetPropertyAsync(system, RoutineDbus.LoginService, sessionPath, LogindParsing.SessionInterface, "IdleHint");
            var since = await RoutineDbus.GetPropertyAsync(system, RoutineDbus.LoginService, sessionPath, LogindParsing.SessionInterface, "IdleSinceHint");
            return RoutineDbus.Plain(hint) is bool idle && RoutineDbus.Plain(since) is ulong sinceUs
                ? LogindParsing.IdleFromHint(idle, sinceUs, DateTimeOffset.UtcNow)
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// <c>x11.xss</c>: <c>XScreenSaverQueryInfo</c> from the optional <c>libXss.so.1</c>, loaded with
/// <see cref="NativeLibrary.TryLoad(string, out IntPtr)"/>; absent means skipped, never a crash.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class XssIdleSource : IIdleSource, IDisposable
{
    private readonly IntPtr _display;
    private readonly IntPtr _root;
    private readonly IntPtr _info;
    private readonly QueryInfo _query;
    private readonly CloseDisplay _close;

    private XssIdleSource(IntPtr display, IntPtr root, IntPtr info, QueryInfo query, CloseDisplay close)
    {
        _display = display;
        _root = root;
        _info = info;
        _query = query;
        _close = close;
    }

    public string Id => "x11.xss";

    public static XssIdleSource? TryCreate()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))
            || !NativeLibrary.TryLoad("libX11.so.6", out var x11)
            || !NativeLibrary.TryLoad("libXss.so.1", out var xss))
        {
            return null;
        }

        try
        {
            var open = Marshal.GetDelegateForFunctionPointer<OpenDisplay>(NativeLibrary.GetExport(x11, "XOpenDisplay"));
            var rootWindow = Marshal.GetDelegateForFunctionPointer<DefaultRootWindow>(NativeLibrary.GetExport(x11, "XDefaultRootWindow"));
            var close = Marshal.GetDelegateForFunctionPointer<CloseDisplay>(NativeLibrary.GetExport(x11, "XCloseDisplay"));
            var alloc = Marshal.GetDelegateForFunctionPointer<AllocInfo>(NativeLibrary.GetExport(xss, "XScreenSaverAllocInfo"));
            var query = Marshal.GetDelegateForFunctionPointer<QueryInfo>(NativeLibrary.GetExport(xss, "XScreenSaverQueryInfo"));

            var display = open(IntPtr.Zero);
            if (display == IntPtr.Zero)
            {
                return null;
            }

            var info = alloc();
            return info == IntPtr.Zero ? null : new XssIdleSource(display, rootWindow(display), info, query, close);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or MarshalDirectiveException)
        {
            return null;
        }
    }

    public Task<TimeSpan?> GetIdleAsync(CancellationToken ct)
    {
        if (_query(_display, _root, _info) == 0)
        {
            return Task.FromResult<TimeSpan?>(null);
        }

        // XScreenSaverInfo { Window window; int state; int kind; unsigned long til_or_since; unsigned long
        // idle; unsigned long eventMask; }: on LP64 idle is at byte 24.
        var idleMs = Marshal.ReadInt64(_info, 24);
        return Task.FromResult<TimeSpan?>(TimeSpan.FromMilliseconds(Math.Max(0, idleMs)));
    }

    public void Dispose() => _close(_display);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr OpenDisplay(IntPtr name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr DefaultRootWindow(IntPtr display);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CloseDisplay(IntPtr display);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr AllocInfo();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int QueryInfo(IntPtr display, IntPtr drawable, IntPtr info);
}

/// <summary>
/// <c>logind.lockedhint</c>: the session object's <c>LockedHint</c> on the system bus, followed through
/// <c>PropertiesChanged</c>.
/// </summary>
/// <remarks>
/// <b>logind's <c>Lock</c>/<c>Unlock</c> SIGNALS ARE REQUESTS, NOT STATE (docs/REGRESSION-GUARDS.md).</b>
/// They ask the locker to act and are sent whether or not it does; subscribing to them would fire
/// "on lock" routines for a lock that never happened and miss locks the locker started itself. Only
/// <c>LockedHint</c> is used. See <see cref="LogindParsing.ParseLockedHint"/>.
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed class LogindLockedHintSource(ILogger logger) : ISessionStateSource
{
    private DBusConnection? _system;
    private string? _sessionPath;
    private IDisposable? _subscription;
    private volatile object? _locked;

    public string Id => "logind.lockedhint";

    public bool? IsLocked => _locked as bool?;

    public event Action<bool>? Changed;

    public async Task<bool> StartAsync(CancellationToken ct)
    {
        _system = await RoutineDbus.ConnectAsync(DBusAddress.System, logger);
        if (_system is null)
        {
            return false;
        }

        try
        {
            _sessionPath = await RoutineDbus.SessionPathAsync(_system);
            var initial = RoutineDbus.Plain(await RoutineDbus.GetPropertyAsync(
                _system, RoutineDbus.LoginService, _sessionPath, LogindParsing.SessionInterface, "LockedHint"));
            if (initial is not bool locked)
            {
                return false;
            }

            _locked = locked;
            var rule = new MatchRule
            {
                Type = MessageType.Signal,
                Sender = RoutineDbus.LoginService,
                Path = _sessionPath,
                Interface = "org.freedesktop.DBus.Properties",
                Member = "PropertiesChanged",
            };

            _subscription = await _system.AddMatchAsync(
                rule,
                static (Message msg, object? state) =>
                {
                    var reader = msg.GetBodyReader();
                    var iface = reader.ReadString();
                    var changed = reader.ReadDictionaryOfStringToVariantValue();
                    var invalidated = reader.ReadArrayOfString();
                    var plain = new Dictionary<string, object?>(StringComparer.Ordinal);
                    foreach (var (key, value) in changed)
                    {
                        plain[key] = RoutineDbus.Plain(value);
                    }

                    return LogindParsing.ParseLockedHint(iface, plain, invalidated);
                },
                (Notification<LogindParsing.LockedHintChange> n) =>
                {
                    if (n.Exception is not null || !n.HasValue || !n.Value.IsRelevant)
                    {
                        return;
                    }

                    if (n.Value.Value is { } value)
                    {
                        Publish(value);
                    }
                    else
                    {
                        _ = RereadAsync();
                    }
                },
                flags: ObserverFlags.None);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "logind LockedHint is not available.");
            return false;
        }
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _system?.Dispose();
    }

    private async Task RereadAsync()
    {
        try
        {
            if (_system is not null && _sessionPath is not null
                && RoutineDbus.Plain(await RoutineDbus.GetPropertyAsync(
                    _system, RoutineDbus.LoginService, _sessionPath, LogindParsing.SessionInterface, "LockedHint")) is bool value)
            {
                Publish(value);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Re-reading LockedHint failed.");
        }
    }

    private void Publish(bool locked)
    {
        if (_locked is bool previous && previous == locked)
        {
            return;
        }

        _locked = locked;
        Changed?.Invoke(locked);
    }
}

/// <summary>
/// <c>freedesktop.screensaver</c> / <c>gnome.screensaver</c>: the session bus <c>ActiveChanged(bool)</c>
/// signal, the fallback when logind has no <c>LockedHint</c> for this session.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class ScreenSaverSessionSource(string id, string service, string path, ILogger logger) : ISessionStateSource
{
    private DBusConnection? _session;
    private IDisposable? _subscription;
    private volatile object? _locked;

    public string Id => id;

    public bool? IsLocked => _locked as bool?;

    public event Action<bool>? Changed;

    public async Task<bool> StartAsync(CancellationToken ct)
    {
        _session = await RoutineDbus.ConnectAsync(DBusAddress.Session, logger);
        if (_session is null)
        {
            return false;
        }

        try
        {
            MessageBuffer buffer;
            {
                var writer = _session.GetMessageWriter();
                writer.WriteMethodCallHeader(destination: service, path: path, @interface: service, member: "GetActive");
                buffer = writer.CreateMessage();
            }

            var session = _session;
            _locked = await RoutineDbus.Timed(
                async () => await session.CallMethodAsync(buffer, static (Message msg, object? state) => msg.GetBodyReader().ReadBool()), ct);

            var rule = new MatchRule { Type = MessageType.Signal, Interface = service, Member = "ActiveChanged", Path = path };
            _subscription = await _session.AddMatchAsync(
                rule,
                static (Message msg, object? state) => msg.GetBodyReader().ReadBool(),
                (Notification<bool> n) =>
                {
                    if (n.Exception is null && n.HasValue && !(_locked is bool previous && previous == n.Value))
                    {
                        _locked = n.Value;
                        Changed?.Invoke(n.Value);
                    }
                },
                flags: ObserverFlags.None);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "{Service} is not available as a session source.", service);
            return false;
        }
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _session?.Dispose();
    }
}
