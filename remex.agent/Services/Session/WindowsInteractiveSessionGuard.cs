using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Remex.Core.Guards;

namespace Remex.Agent.Services.Session;

/// <summary>
/// Windows interactive session guard. Runs in the Session-0 SYSTEM service, which has the privilege
/// to reconnect a disconnected session to the console (resuming it unlocked) so RemEx input
/// injection works even after a Microsoft "Windows App" (RDP) client disconnects and locks the
/// session. Ref-counted across concurrent clients; re-locks (disconnects) the session when the last
/// client leaves. Every unlock/re-lock is audit-logged with the client identity.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsInteractiveSessionGuard : IInteractiveSessionGuard
{
    private readonly ILogger<WindowsInteractiveSessionGuard> _logger;
    private readonly object _gate = new();
    private readonly HashSet<string> _engaged = new();
    private uint _heldSessionId;

    public WindowsInteractiveSessionGuard(ILogger<WindowsInteractiveSessionGuard> logger)
    {
        _logger = Guard.NotNull(logger);
    }

    public void EngageForRemoteControl(string clientId)
    {
        lock (_gate)
        {
            bool firstClient = _engaged.Count == 0;
            _engaged.Add(clientId);
            if (!firstClient)
            {
                return;
            }

            try
            {
                var snapshot = EnumerateSessions();
                var decision = SessionGuardPolicy.Decide(snapshot);
                _heldSessionId = decision.SessionId;

                if (decision.Action == SessionGuardAction.Reconnect)
                {
                    ReconnectToConsole(decision.SessionId, clientId);
                }
                else if (decision.Action == SessionGuardAction.NoUserSession)
                {
                    _logger.LogWarning("Session guard: no signed-in user session to keep unlocked (client {Client}).", clientId);
                }

                // Prevent idle sleep/lock while a client is connected.
                SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Session guard: failed to engage for client {Client}.", clientId);
            }
        }
    }

    public void Disengage(string clientId)
    {
        lock (_gate)
        {
            if (!_engaged.Remove(clientId) || _engaged.Count > 0)
            {
                return;
            }

            try
            {
                // Drop the keep-awake hold.
                SetThreadExecutionState(ES_CONTINUOUS);

                // Re-lock by disconnecting the session we kept unlocked (console returns to the
                // secure logon screen). Best-effort: a signed-out machine has nothing to disconnect.
                if (_heldSessionId != 0 && !WTSDisconnectSession(IntPtr.Zero, _heldSessionId, bWait: false))
                {
                    _logger.LogWarning(
                        "Session guard: WTSDisconnectSession failed for session {SessionId} (Win32 {Error}).",
                        _heldSessionId, Marshal.GetLastWin32Error());
                }
                else if (_heldSessionId != 0)
                {
                    _logger.LogInformation("Session guard: re-locked session {SessionId} after client {Client} disconnected.", _heldSessionId, clientId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Session guard: failed to disengage for client {Client}.", clientId);
            }
            finally
            {
                _heldSessionId = 0;
            }
        }
    }

    private void ReconnectToConsole(uint sessionId, string clientId)
    {
        // tscon, run by the SYSTEM service, reconnects the session to the physical console and
        // resumes it UNLOCKED without a password. This is the dual-use unlock the feature exists for.
        var psi = new ProcessStartInfo("tscon.exe", $"{sessionId} /dest:console")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi);
        proc?.WaitForExit(5000);
        _logger.LogInformation(
            "Session guard: reconnected session {SessionId} to the console (unlocked) for client {Client}.",
            sessionId, clientId);
    }

    private List<SessionSnapshot> EnumerateSessions()
    {
        var result = new List<SessionSnapshot>();
        if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out IntPtr ppSessionInfo, out int count))
        {
            _logger.LogWarning("Session guard: WTSEnumerateSessions failed (Win32 {Error}).", Marshal.GetLastWin32Error());
            return result;
        }

        try
        {
            int dataSize = Marshal.SizeOf<WTS_SESSION_INFO>();
            for (int i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WTS_SESSION_INFO>(ppSessionInfo + i * dataSize);
                uint sessionId = info.SessionId;
                bool hasUser = TryGetUserName(sessionId, out _);
                var state = MapState(info.State);
                result.Add(new SessionSnapshot(sessionId, hasUser, state));
            }
        }
        finally
        {
            WTSFreeMemory(ppSessionInfo);
        }

        return result;
    }

    private static bool TryGetUserName(uint sessionId, out string userName)
    {
        userName = string.Empty;
        if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, WTS_INFO_CLASS.WTSUserName, out IntPtr buffer, out int bytes))
        {
            return false;
        }

        try
        {
            userName = Marshal.PtrToStringUni(buffer) ?? string.Empty;
            return !string.IsNullOrEmpty(userName);
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    private static WtsState MapState(WTS_CONNECTSTATE_CLASS s) => s switch
    {
        WTS_CONNECTSTATE_CLASS.WTSActive => WtsState.Active,
        WTS_CONNECTSTATE_CLASS.WTSConnected => WtsState.Connected,
        WTS_CONNECTSTATE_CLASS.WTSDisconnected => WtsState.Disconnected,
        _ => WtsState.Other,
    };

    private const uint ES_CONTINUOUS = 0x80000000;
    private const uint ES_SYSTEM_REQUIRED = 0x00000001;
    private const uint ES_DISPLAY_REQUIRED = 0x00000002;

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateSessions(IntPtr hServer, int Reserved, int Version, out IntPtr ppSessionInfo, out int pCount);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSDisconnectSession(IntPtr hServer, uint sessionId, bool bWait);

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WTSQuerySessionInformation(IntPtr hServer, uint sessionId, WTS_INFO_CLASS wtsInfoClass, out IntPtr ppBuffer, out int pBytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr pMemory);

    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_SESSION_INFO
    {
        public uint SessionId;
        [MarshalAs(UnmanagedType.LPWStr)] public string pWinStationName;
        public WTS_CONNECTSTATE_CLASS State;
    }

    private enum WTS_INFO_CLASS { WTSUserName = 5 }

    private enum WTS_CONNECTSTATE_CLASS
    {
        WTSActive, WTSConnected, WTSConnectQuery, WTSShadow, WTSDisconnected,
        WTSIdle, WTSListen, WTSReset, WTSDown, WTSInit,
    }
}
