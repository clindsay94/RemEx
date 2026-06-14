using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Remex.Host.Services.Input;
using Remex.Host.Services.ScreenCapture;

namespace Remex.Host.Services.RemoteDesktop.Windows;

[SupportedOSPlatform("windows")]
public sealed record WindowsRemoteDesktopDiagnosticReport
{
    public int SessionId { get; init; }
    public bool UserInteractive { get; init; }
    public bool IsSessionZero { get; init; }
    public bool SupportsRemoteDesktopSession { get; init; }
    public bool InputDesktopAccessible { get; init; }
    public string? InputDesktopName { get; init; }
    public bool CurrentDesktopReady { get; init; }
    public bool DxgiAvailable { get; init; }
    public string? CaptureBackend { get; init; }
    public string? InputBackend { get; init; }
    public string? RemoteDesktopUnavailableReason { get; init; }
    public string? CurrentDesktopUnavailableReason { get; init; }
    public string? CaptureBackendDegradedReason { get; init; }
    public string? LastCaptureFailureReason { get; init; }
    public string? LastInputFailureReason { get; init; }
    public IReadOnlyList<string> Issues { get; init; } = Array.Empty<string>();
    public DateTimeOffset CollectedAt { get; init; } = DateTimeOffset.UtcNow;
}

[SupportedOSPlatform("windows")]
internal static class WindowsRemoteDesktopDiagnostics
{
    private const uint DesktopReadObjects = 0x0001;
    private const int UserObjectName = 2;
    private const string DefaultDesktopName = "Default";
    private const string WinlogonDesktopName = "Winlogon";

    public static WindowsRemoteDesktopDiagnosticReport Evaluate(
        WindowsScreenCaptureService? captureService = null,
        WindowsInputSimulationService? inputService = null)
    {
        var issues = new List<string>();
        var sessionId = Process.GetCurrentProcess().SessionId;
        var userInteractive = Environment.UserInteractive;
        var isSessionZero = sessionId == 0;
        var supportsRemoteDesktopSession = userInteractive && !isSessionZero;

        string? remoteDesktopUnavailableReason = null;
        if (!supportsRemoteDesktopSession)
        {
            remoteDesktopUnavailableReason =
                "Remote desktop requires a signed-in interactive Windows user session. " +
                "Run Remex Desktop interactively, or configure the Windows service to log on as that user instead of Session 0.";
            issues.Add(remoteDesktopUnavailableReason);
        }

        var (inputDesktopAccessible, inputDesktopName, inputDesktopReason) = TryGetInputDesktopInfo();
        string? currentDesktopUnavailableReason = null;
        if (supportsRemoteDesktopSession)
        {
            if (!inputDesktopAccessible)
            {
                currentDesktopUnavailableReason = inputDesktopReason
                    ?? "Remex could not access the active Windows input desktop. The session may be locked or showing a secure prompt.";
            }
            else if (!string.IsNullOrWhiteSpace(inputDesktopName)
                     && !string.Equals(inputDesktopName, DefaultDesktopName, StringComparison.OrdinalIgnoreCase))
            {
                currentDesktopUnavailableReason = string.Equals(inputDesktopName, WinlogonDesktopName, StringComparison.OrdinalIgnoreCase)
                    ? "Windows is currently showing the Winlogon secure desktop (lock screen or credential/UAC prompt). Unlock the session or dismiss the secure prompt, then retry remote desktop."
                    : $"Windows is currently showing the '{inputDesktopName}' desktop instead of the normal '{DefaultDesktopName}' desktop. Return to the signed-in desktop, then retry remote desktop.";
            }

            if (!string.IsNullOrWhiteSpace(currentDesktopUnavailableReason))
            {
                issues.Add(currentDesktopUnavailableReason);
            }
        }

        string? captureBackendDegradedReason = null;
        if (captureService is not null && !captureService.IsDxgiAvailable)
        {
            captureBackendDegradedReason = captureService.DxgiUnavailableReason is { Length: > 0 } dxgiReason
                ? $"DXGI desktop duplication is unavailable ({dxgiReason}). Remex will fall back to GDI capture, so GPU-accelerated or overlay windows may appear black."
                : "DXGI desktop duplication is unavailable. Remex will fall back to GDI capture, so GPU-accelerated or overlay windows may appear black.";
            issues.Add(captureBackendDegradedReason);
        }

        if (!string.IsNullOrWhiteSpace(captureService?.LastCaptureFailureReason))
        {
            issues.Add(captureService.LastCaptureFailureReason);
        }

        if (!string.IsNullOrWhiteSpace(inputService?.LastInputFailureReason))
        {
            issues.Add(inputService.LastInputFailureReason);
        }

        return new WindowsRemoteDesktopDiagnosticReport
        {
            SessionId = sessionId,
            UserInteractive = userInteractive,
            IsSessionZero = isSessionZero,
            SupportsRemoteDesktopSession = supportsRemoteDesktopSession,
            InputDesktopAccessible = inputDesktopAccessible,
            InputDesktopName = inputDesktopName,
            CurrentDesktopReady = supportsRemoteDesktopSession && string.IsNullOrWhiteSpace(currentDesktopUnavailableReason),
            DxgiAvailable = captureService?.IsDxgiAvailable ?? false,
            CaptureBackend = captureService?.BackendName ?? "gdi",
            InputBackend = inputService?.BackendName ?? "sendinput",
            RemoteDesktopUnavailableReason = remoteDesktopUnavailableReason,
            CurrentDesktopUnavailableReason = currentDesktopUnavailableReason,
            CaptureBackendDegradedReason = captureBackendDegradedReason,
            LastCaptureFailureReason = captureService?.LastCaptureFailureReason,
            LastInputFailureReason = inputService?.LastInputFailureReason,
            Issues = issues.AsReadOnly(),
            CollectedAt = DateTimeOffset.UtcNow,
        };
    }

    private static (bool Accessible, string? Name, string? Reason) TryGetInputDesktopInfo()
    {
        var desktop = OpenInputDesktop(0, false, DesktopReadObjects);
        if (desktop == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            var message = error != 0
                ? new Win32Exception(error).Message
                : "Unknown error";
            return (false, null,
                $"Remex could not open the active Windows input desktop (Win32 {error}: {message}). The session may be locked or showing a secure prompt.");
        }

        try
        {
            uint requiredLength = 0;
            _ = GetUserObjectInformation(desktop, UserObjectName, IntPtr.Zero, 0, ref requiredLength);
            if (requiredLength == 0)
            {
                var error = Marshal.GetLastWin32Error();
                var message = error != 0
                    ? new Win32Exception(error).Message
                    : "Unknown error";
                return (false, null,
                    $"Remex could not query the active Windows desktop name (Win32 {error}: {message}).");
            }

            var buffer = Marshal.AllocHGlobal((int)requiredLength);
            try
            {
                if (!GetUserObjectInformation(desktop, UserObjectName, buffer, requiredLength, ref requiredLength))
                {
                    var error = Marshal.GetLastWin32Error();
                    return (false, null,
                        $"Remex could not query the active Windows desktop name (Win32 {error}: {new Win32Exception(error).Message}).");
                }

                var desktopName = Marshal.PtrToStringUni(buffer);
                return (!string.IsNullOrWhiteSpace(desktopName), desktopName, null);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseDesktop(desktop);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(
        IntPtr hObj,
        int nIndex,
        IntPtr pvInfo,
        uint nLength,
        ref uint lpnLengthNeeded);
}
