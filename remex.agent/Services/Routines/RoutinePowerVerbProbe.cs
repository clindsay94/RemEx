using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Remex.Core.Routines;

namespace Remex.Agent.Services.Routines;

/// <summary>
/// Which routine power verbs this host can actually carry out, for
/// <c>HostCapabilities.RoutinePowerVerbs</c> (routines spec §7.5, RemEx-pp0rt.3).
/// </summary>
/// <remarks>
/// <para>
/// Static facts only, because the result lives in <c>HostCapabilitiesProvider</c>'s cached record.
/// Never includes <c>WAKEONLAN</c> (D5): it is not in <see cref="RoutinePowerVerbs.All"/> to begin with.
/// </para>
/// <para>
/// Windows: <c>GetPwrCapabilities</c> decides SLEEP (S3, or modern standby) and HIBERNATE (S4 with a
/// hibernation file), and <c>GetFirmwareType</c> decides RESTARTTOUEFI. A Windows probe whose DLL call
/// throws falls back to the full list, which is what the host offered before it could probe.
/// </para>
/// <para>
/// <b>Linux: ASK logind, AND ADVERTISE ONLY WHAT IT SAYS YES TO (RemEx-pp0rt.9 merge gate).</b> SHUTDOWN
/// and FORCESHUTDOWN follow <c>CanPowerOff</c>, RESTART and FORCERESTART <c>CanReboot</c>, RESTARTTOUEFI
/// <c>CanRebootToFirmwareSetup</c>, SLEEP <c>CanSuspend</c>, HIBERNATE <c>CanHibernate</c>, each only on an
/// exact <c>yes</c> (<c>challenge</c> would raise a polkit prompt nobody is there to answer). SIGNOUT and
/// LOCK run through <c>loginctl</c> and are offered when logind answered at all. MONITOROFF runs
/// <c>xset dpms force off</c>, so it is offered only on an X11 session with <c>xset</c> on the PATH. A verb
/// that cannot be probed is not advertised: the editor then disables it rather than letting a routine fail
/// later on a PC that could never have run it.
/// </para>
/// </remarks>
public static class RoutinePowerVerbProbe
{
    /// <summary>How long the Linux probe may take. It runs once, inside the cached capability record.</summary>
    public static readonly TimeSpan LinuxProbeTimeout = TimeSpan.FromSeconds(3);

    public static List<string> Probe()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return ProbeWindows();
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            // Fall through to the unfiltered list; see the class remarks.
            return [.. RoutinePowerVerbs.All];
        }

        if (OperatingSystem.IsLinux())
        {
            return ProbeLinux();
        }

        return [.. RoutinePowerVerbs.All];
    }

    /// <summary>
    /// The Linux mapping, free of D-Bus so it is tested everywhere. <paramref name="answers"/> holds logind's
    /// <c>Can*</c> answers by method name, or is null when logind could not be reached.
    /// </summary>
    public static List<string> MapLinux(IReadOnlyDictionary<string, string?>? answers, bool monitorOffAvailable)
    {
        bool Yes(string method) =>
            answers is not null && answers.TryGetValue(method, out var answer) && LogindParsing.IsYes(answer);

        var verbs = new List<string>(RoutinePowerVerbs.All.Count);
        foreach (var verb in RoutinePowerVerbs.All)
        {
            var supported = verb switch
            {
                RoutinePowerVerbs.Shutdown or RoutinePowerVerbs.ForceShutdown => Yes("CanPowerOff"),
                RoutinePowerVerbs.Restart or RoutinePowerVerbs.ForceRestart => Yes("CanReboot"),
                RoutinePowerVerbs.RestartToUefi => Yes("CanRebootToFirmwareSetup"),
                RoutinePowerVerbs.Sleep => Yes("CanSuspend"),
                RoutinePowerVerbs.Hibernate => Yes("CanHibernate"),
                RoutinePowerVerbs.SignOut or RoutinePowerVerbs.Lock => answers is not null,
                RoutinePowerVerbs.MonitorOff => monitorOffAvailable,
                _ => false,
            };

            if (supported)
            {
                verbs.Add(verb);
            }
        }

        return verbs;
    }

    /// <summary>The logind methods the Linux probe asks.</summary>
    public static readonly IReadOnlyList<string> LogindMethods =
        ["CanPowerOff", "CanReboot", "CanRebootToFirmwareSetup", "CanSuspend", "CanHibernate"];

    [SupportedOSPlatform("linux")]
    private static List<string> ProbeLinux()
    {
        IReadOnlyDictionary<string, string?>? answers = null;
        try
        {
            // Off the caller's context: this runs inside a Lazy the capability provider may build from a
            // thread that carries a SynchronizationContext.
            var probe = Task.Run(AskLogindAsync);
            if (probe.Wait(LinuxProbeTimeout))
            {
                answers = probe.Result;
            }
        }
        catch (AggregateException)
        {
            answers = null;
        }

        return MapLinux(answers, IsMonitorOffAvailable());
    }

    [SupportedOSPlatform("linux")]
    private static async Task<IReadOnlyDictionary<string, string?>?> AskLogindAsync()
    {
        var system = await RoutineDbus.ConnectAsync(
            Tmds.DBus.Protocol.DBusAddress.System, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        if (system is null)
        {
            return null;
        }

        using (system)
        {
            var answers = new Dictionary<string, string?>(StringComparer.Ordinal);
            var reachable = false;
            foreach (var method in LogindMethods)
            {
                try
                {
                    Tmds.DBus.Protocol.MessageBuffer buffer;
                    {
                        var writer = system.GetMessageWriter();
                        writer.WriteMethodCallHeader(
                            destination: RoutineDbus.LoginService,
                            path: RoutineDbus.LoginPath,
                            @interface: RoutineDbus.LoginManager,
                            member: method);
                        buffer = writer.CreateMessage();
                    }

                    answers[method] = await RoutineDbus.Timed(async () => await system.CallMethodAsync(
                        buffer, static (Tmds.DBus.Protocol.Message msg, object? state) => msg.GetBodyReader().ReadString()));
                    reachable = true;
                }
                catch (Exception)
                {
                    // An older logind without this method: that verb simply is not offered.
                    answers[method] = null;
                }
            }

            return reachable ? answers : null;
        }
    }

    private static bool IsMonitorOffAvailable()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
        {
            return false;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return path.Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => File.Exists(Path.Combine(dir, "xset")));
    }

    [SupportedOSPlatform("windows")]
    private static List<string> ProbeWindows()
    {
        // SYSTEM_POWER_CAPABILITIES is 76 bytes of BOOLEAN/BYTE fields followed by enums; only the
        // leading BOOLEANs are read, by offset, so the struct is not re-declared here.
        var capabilities = new byte[128];
        var havePowerCaps = GetPwrCapabilities(capabilities);
        var systemS3 = havePowerCaps && capabilities[SystemS3Offset] != 0;
        var systemS4 = havePowerCaps && capabilities[SystemS4Offset] != 0;
        var hiberFilePresent = havePowerCaps && capabilities[HiberFilePresentOffset] != 0;
        var modernStandby = havePowerCaps && capabilities[AoAcOffset] != 0;

        var isUefi = GetFirmwareType(out var firmwareType) && firmwareType == FirmwareTypeUefi;

        var verbs = new List<string>(RoutinePowerVerbs.All.Count);
        foreach (var verb in RoutinePowerVerbs.All)
        {
            var supported = verb switch
            {
                // Without the power-capabilities call we cannot tell, so offer rather than hide.
                RoutinePowerVerbs.Sleep => !havePowerCaps || systemS3 || modernStandby,
                RoutinePowerVerbs.Hibernate => !havePowerCaps || (systemS4 && hiberFilePresent),
                RoutinePowerVerbs.RestartToUefi => isUefi,
                _ => true,
            };

            if (supported)
            {
                verbs.Add(verb);
            }
        }

        return verbs;
    }

    private const int SystemS3Offset = 5;
    private const int SystemS4Offset = 6;
    private const int HiberFilePresentOffset = 8;
    private const int AoAcOffset = 20;
    private const int FirmwareTypeUefi = 2;

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool GetPwrCapabilities([Out] byte[] systemPowerCapabilities);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFirmwareType(out int firmwareType);
}
