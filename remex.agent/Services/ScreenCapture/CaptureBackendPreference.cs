using System;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Remex.Agent.Services.ScreenCapture;

/// <summary>
/// Which Windows screen-capture backend the host should prefer. The orchestrator
/// (<c>WindowsScreenCaptureService</c>) tries backends in a fixed ladder — <b>WGC → DXGI → GDI</b> —
/// and this preference decides where in that ladder it starts.
/// </summary>
public enum CaptureBackend
{
    /// <summary>WGC first, then DXGI, then GDI. The default and recommended setting.</summary>
    Auto = 0,

    /// <summary>Force Windows.Graphics.Capture; fall through to DXGI/GDI only if WGC is unavailable.</summary>
    Wgc = 1,

    /// <summary>Skip WGC; start at DXGI Desktop Duplication (then GDI). For GPUs where WGC misbehaves.</summary>
    Dxgi = 2,

    /// <summary>Skip WGC and DXGI; use GDI BitBlt only. Slowest but most compatible.</summary>
    Gdi = 3,
}

/// <summary>
/// Reads the machine-wide capture-backend preference from the registry. The host runs as LocalSystem in
/// Session 0, so config lives under <b>HKLM</b> (never HKCU/APPDATA — those belong to no real user here),
/// matching the existing registry-backed host state (<c>PairedClientRegistry</c>, <c>StartupRegistrationService</c>).
///
/// <para>
/// Registry value: <c>HKLM\SOFTWARE\RemEx\Capture\Backend</c> (REG_SZ) = <c>Auto | Wgc | Dxgi | Gdi</c>
/// (case-insensitive). Lets an operator pin a problematic GPU to DXGI/GDI — or A/B WGC vs DXGI — without
/// a redeploy. <b>Fails open to <see cref="CaptureBackend.Auto"/></b>: a missing key, an unknown string, or
/// any registry error yields <c>Auto</c> so capture is never disabled by a bad config.
/// </para>
/// </summary>
public static class CaptureBackendPreference
{
    private const string RegistryKeyPath = @"SOFTWARE\RemEx\Capture";
    private const string RegistryValueName = "Backend";

    /// <summary>
    /// Parses a backend preference string to <see cref="CaptureBackend"/>. Case-insensitive; <c>null</c>,
    /// empty, whitespace, or any unrecognized value yields <see cref="CaptureBackend.Auto"/>. Pure and
    /// side-effect-free — the unit-testable core of the registry read.
    /// </summary>
    public static CaptureBackend Parse(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "wgc" => CaptureBackend.Wgc,
            "dxgi" => CaptureBackend.Dxgi,
            "gdi" => CaptureBackend.Gdi,
            "auto" => CaptureBackend.Auto,
            _ => CaptureBackend.Auto,
        };

    /// <summary>
    /// Reads <c>HKLM\SOFTWARE\RemEx\Capture\Backend</c> and parses it. Returns <see cref="CaptureBackend.Auto"/>
    /// on any error (key/value absent, access denied, unexpected type). The optional logger records the
    /// resolved value once at startup for diagnostics.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static CaptureBackend Read(ILogger? logger = null)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegistryKeyPath);
            var raw = key?.GetValue(RegistryValueName) as string;
            var backend = Parse(raw);
            logger?.LogInformation(
                "Capture backend preference: {Backend} (registry HKLM\\{Key}\\{Value} = {Raw}).",
                backend, RegistryKeyPath, RegistryValueName, raw ?? "<unset>");
            return backend;
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Could not read capture backend preference; defaulting to Auto.");
            return CaptureBackend.Auto;
        }
    }
}
