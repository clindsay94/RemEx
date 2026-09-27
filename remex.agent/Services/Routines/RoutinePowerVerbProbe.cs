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
/// hibernation file), and <c>GetFirmwareType</c> decides RESTARTTOUEFI. Linux: every verb is offered
/// for now; the logind <c>CanSuspend</c> / <c>CanHibernate</c> / <c>CanRebootToFirmwareSetup</c> probe
/// lands with the host runner (routines S4, <c>RoutinePowerVerbProbeTests</c>). A verb that turns out
/// not to work fails visibly at run time (<c>power_denied_by_os</c> / <c>power_failed</c>).
/// </para>
/// <para>
/// A probe that throws never takes the capability record down with it: it falls back to the full
/// list, which is exactly what the host offered before it could probe.
/// </para>
/// </remarks>
public static class RoutinePowerVerbProbe
{
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
        }

        return [.. RoutinePowerVerbs.All];
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
