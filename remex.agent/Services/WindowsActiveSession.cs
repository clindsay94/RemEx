using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Remex.Agent.Services;

/// <summary>
/// Helpers for reaching the interactive console session from a non-interactive (Session 0) Windows
/// service. The headless agent runs as a LocalSystem service in Session 0, which has no desktop, so
/// actions that target "the current session" (LockWorkStation, the monitor-off broadcast, logoff)
/// no-op there. These helpers locate the signed-in user's console session and either launch a process
/// into it (<see cref="TryLaunch"/> / <see cref="TryLaunchSessionTask"/>) via
/// <c>CreateProcessAsUser</c>, or act on it directly (<see cref="TryLogoffActiveSession"/>).
///
/// Owning the WTS/CreateProcessAsUser P/Invoke in one place keeps it out of <c>Remex.Core</c> (which
/// is also compiled as the Android NativeAOT JNI library) and lets both the command bridge
/// (<c>SessionBridgingCommandService</c>) and the app launcher share a single implementation.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsActiveSession
{
    /// <summary>
    /// True when there is an active console session with a signed-in user we can act on. False at the
    /// lock/login screen before anyone has signed in (no user token), where interactive actions are
    /// inherently impossible.
    /// </summary>
    public static bool IsUserSessionAvailable
    {
        get
        {
            uint sessionId = WTSGetActiveConsoleSessionId();
            if (sessionId == InvalidSessionId)
            {
                return false;
            }

            if (!WTSQueryUserToken(sessionId, out IntPtr token))
            {
                return false;
            }

            CloseHandle(token);
            return true;
        }
    }

    /// <summary>
    /// The session ID currently attached to the physical console, or <see cref="InvalidSessionId"/>
    /// (0xFFFFFFFF) when no session is attached (e.g. every session disconnected). RDP sessions are
    /// never returned here — only the console session. Used to verify the interactive GUI host is
    /// running in the session that actually owns the active input desktop, not a stranded one.
    /// </summary>
    public static uint ActiveConsoleSessionId => WTSGetActiveConsoleSessionId();

    /// <summary>
    /// Launches this host binary into the active console session with <c>--session-task &lt;verb&gt;</c>
    /// so a desktop-bound action (e.g. lock, monitor-off) executes where it can actually take effect.
    /// </summary>
    public static bool TryLaunchSessionTask(string verb, ILogger? logger = null)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            logger?.LogWarning("Cannot launch session task '{Verb}': process path is unavailable.", verb);
            return false;
        }

        var workingDir = System.IO.Path.GetDirectoryName(exePath);
        // Quote the exe in the command line (argv[0]); CREATE_NO_WINDOW keeps the short-lived helper silent.
        var commandLine = $"\"{exePath}\" --session-task {verb}";
        return TryLaunch(exePath, commandLine, workingDir, CREATE_NO_WINDOW, SW_HIDE, logger);
    }

    /// <summary>
    /// Launches a process inside the active console session under the signed-in user's token. Returns
    /// false (and logs) instead of throwing, so callers can fall back to a direct attempt.
    /// </summary>
    public static bool TryLaunch(
        string? applicationName,
        string commandLine,
        string? workingDirectory,
        uint creationFlags,
        ushort showWindow,
        ILogger? logger = null,
        bool elevate = false)
    {
        IntPtr userToken = IntPtr.Zero;
        IntPtr elevatedToken = IntPtr.Zero;
        IntPtr envBlock = IntPtr.Zero;
        try
        {
            uint sessionId = WTSGetActiveConsoleSessionId();
            if (sessionId == InvalidSessionId)
            {
                logger?.LogWarning("No active console session; cannot launch into the interactive session.");
                return false;
            }

            if (!WTSQueryUserToken(sessionId, out userToken))
            {
                logger?.LogWarning(
                    "WTSQueryUserToken failed for session {SessionId} (Win32 error {Error}); no signed-in user to launch as.",
                    sessionId, Marshal.GetLastWin32Error());
                return false;
            }

            // For desktop input injection to reach ELEVATED (admin) windows, the launched process
            // must itself run at HIGH integrity — Windows UIPI silently drops SendInput from a
            // lower-integrity process against a higher-integrity foreground window. WTSQueryUserToken
            // returns the user's *filtered* token (MEDIUM integrity on a UAC admin), so when elevation
            // is requested, obtain the user's linked full-admin token. Falls back to the filtered
            // token for standard users or when no linked token is available.
            IntPtr launchToken = userToken;
            if (elevate)
            {
                elevatedToken = TryGetLinkedElevatedToken(userToken, logger);
                if (elevatedToken != IntPtr.Zero)
                {
                    launchToken = elevatedToken;
                    logger?.LogInformation(
                        "Using the signed-in user's elevated (linked) token for a HIGH-integrity launch in session {SessionId}.",
                        sessionId);
                }
                else
                {
                    logger?.LogInformation(
                        "No elevated linked token available in session {SessionId}; launching at the user's default integrity. " +
                        "Input to elevated (admin) windows will be blocked by Windows UIPI.",
                        sessionId);
                }
            }

            // Build the signed-in user's environment block. Without this the child inherits the
            // SYSTEM service's environment (TEMP/USERPROFILE point into the system profile, which the
            // user token can't use), and a self-contained .NET process aborts during runtime startup
            // before reaching Main — CreateProcessAsUser still returns a PID, so the failure is silent.
            uint flags = creationFlags;
            if (CreateEnvironmentBlock(out envBlock, launchToken, bInherit: false))
            {
                flags |= CREATE_UNICODE_ENVIRONMENT;
            }
            else
            {
                logger?.LogWarning(
                    "CreateEnvironmentBlock failed (Win32 error {Error}); launching with inherited environment.",
                    Marshal.GetLastWin32Error());
                envBlock = IntPtr.Zero;
            }

            var si = new STARTUPINFO();
            si.cb = Marshal.SizeOf(si);
            si.lpDesktop = @"winsta0\default"; // Required for interactive apps.
            si.dwFlags = STARTF_USESHOWWINDOW;
            si.wShowWindow = showWindow;

            // CreateProcessAsUserW may write into lpCommandLine in place (it inserts a null after
            // argv[0] when lpApplicationName is null), so it must be a mutable buffer, not a pinned
            // managed string. Give it headroom beyond the current length.
            var mutableCommandLine = new StringBuilder(commandLine, Math.Max(commandLine.Length + 2, 260));

            bool ok = CreateProcessAsUser(
                launchToken,
                applicationName,
                mutableCommandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                flags,
                envBlock,
                workingDirectory,
                ref si,
                out PROCESS_INFORMATION pi);

            if (!ok)
            {
                logger?.LogWarning(
                    "CreateProcessAsUser failed for session {SessionId} (Win32 error {Error}).",
                    sessionId, Marshal.GetLastWin32Error());
                return false;
            }

            logger?.LogInformation(
                "Launched process (PID {Pid}) in interactive session {SessionId}.",
                pi.dwProcessId, sessionId);
            CloseHandle(pi.hProcess);
            CloseHandle(pi.hThread);
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to launch process in the interactive session.");
            return false;
        }
        finally
        {
            if (envBlock != IntPtr.Zero)
            {
                DestroyEnvironmentBlock(envBlock);
            }
            if (elevatedToken != IntPtr.Zero)
            {
                CloseHandle(elevatedToken);
            }
            if (userToken != IntPtr.Zero)
            {
                CloseHandle(userToken);
            }
        }
    }

    /// <summary>
    /// When the signed-in user is a split-token (UAC) administrator, returns a primary token carrying
    /// their FULL/linked elevated token (HIGH integrity), suitable for <c>CreateProcessAsUser</c>.
    /// Returns <see cref="IntPtr.Zero"/> when no elevated token is available (standard user, UAC
    /// disabled, or the token is already full) — the caller should fall back to the filtered token.
    /// The returned handle is owned by the caller and must be closed.
    /// </summary>
    private static IntPtr TryGetLinkedElevatedToken(IntPtr filteredToken, ILogger? logger)
    {
        // Only a split-token admin (TokenElevationTypeLimited) has a usable linked elevated token.
        // For a full-token admin the process is already elevated; for a standard user there is no
        // admin token to link to. In both cases, keep the original token.
        if (!TryGetTokenElevationType(filteredToken, out int elevationType)
            || elevationType != TokenElevationTypeLimited)
        {
            // RD-F (UIPI diagnosis): log WHY no linked elevated token is used. TokenElevationType
            // 1=Default (UAC off / non-admin → no split token), 2=Full (already elevated), 3=Limited
            // (split-token admin — the only case with a linked HIGH token). If input to admin windows is
            // blocked and this logs type=1 while the signed-in user IS an admin, UAC Admin Approval Mode
            // is likely off (everything runs at one integrity, so a service-launched medium host cannot
            // drive an elevated window). type=2 means the host should already be HIGH — then check for a
            // competing MEDIUM-integrity host instance (e.g. a leftover HKCU\...\Run entry) winning the
            // single-instance guard. See docs/REMOTE_DESKTOP_PERFORMANCE.md.
            logger?.LogInformation(
                "Session guard: no linked elevated token (TokenElevationType={ElevationType}); launching the " +
                "interactive host at the user's default integrity. Input to elevated (admin) foreground " +
                "windows will be blocked by Windows UIPI unless that default is already HIGH.",
                elevationType);
            return IntPtr.Zero;
        }

        IntPtr infoBuffer = Marshal.AllocHGlobal(IntPtr.Size);
        try
        {
            if (!GetTokenInformation(filteredToken, TokenLinkedToken, infoBuffer, (uint)IntPtr.Size, out _))
            {
                logger?.LogWarning(
                    "GetTokenInformation(TokenLinkedToken) failed (Win32 error {Error}); cannot elevate the launch.",
                    Marshal.GetLastWin32Error());
                return IntPtr.Zero;
            }

            // The linked token is an impersonation token; CreateProcessAsUser needs a PRIMARY token.
            IntPtr linkedToken = Marshal.ReadIntPtr(infoBuffer);
            try
            {
                if (!DuplicateTokenEx(
                        linkedToken,
                        MAXIMUM_ALLOWED,
                        IntPtr.Zero,
                        SecurityImpersonation,
                        TokenPrimary,
                        out IntPtr primaryToken))
                {
                    logger?.LogWarning(
                        "DuplicateTokenEx on the linked elevated token failed (Win32 error {Error}).",
                        Marshal.GetLastWin32Error());
                    return IntPtr.Zero;
                }

                return primaryToken;
            }
            finally
            {
                if (linkedToken != IntPtr.Zero)
                {
                    CloseHandle(linkedToken);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(infoBuffer);
        }
    }

    /// <summary>
    /// Reads the token's UAC elevation type (Default / Full / Limited). Returns false on failure.
    /// </summary>
    private static bool TryGetTokenElevationType(IntPtr token, out int elevationType)
    {
        elevationType = 0;
        IntPtr buffer = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            if (!GetTokenInformation(token, TokenElevationType, buffer, sizeof(int), out _))
            {
                return false;
            }

            elevationType = Marshal.ReadInt32(buffer);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Logs off the active console session. The LocalSystem service has the rights to do this directly
    /// via <c>WTSLogoffSession</c>, so no in-session helper is required.
    /// </summary>
    public static bool TryLogoffActiveSession(ILogger? logger = null)
    {
        uint sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == InvalidSessionId)
        {
            logger?.LogWarning("No active console session to log off.");
            return false;
        }

        if (!WTSLogoffSession(IntPtr.Zero, sessionId, bWait: false))
        {
            logger?.LogWarning(
                "WTSLogoffSession failed for session {SessionId} (Win32 error {Error}).",
                sessionId, Marshal.GetLastWin32Error());
            return false;
        }

        logger?.LogInformation("Logged off interactive session {SessionId}.", sessionId);
        return true;
    }

    #region P/Invoke

    private const uint InvalidSessionId = 0xFFFFFFFF;
    private const uint STARTF_USESHOWWINDOW = 0x00000001;
    private const uint CREATE_NO_WINDOW = 0x08000000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const ushort SW_HIDE = 0;
    public const ushort SW_SHOW = 5;
    public const uint CREATE_NEW_CONSOLE = 0x00000010;

    // TOKEN_INFORMATION_CLASS values used for UAC linked-token elevation.
    private const int TokenElevationType = 18;
    private const int TokenLinkedToken = 19;
    // TOKEN_ELEVATION_TYPE: 1 = Default (no UAC split), 2 = Full (already elevated), 3 = Limited (has a linked admin token).
    private const int TokenElevationTypeLimited = 3;
    // SECURITY_IMPERSONATION_LEVEL.SecurityImpersonation and TOKEN_TYPE.TokenPrimary.
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const uint MAXIMUM_ALLOWED = 0x02000000;

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        IntPtr TokenHandle,
        int TokenInformationClass,
        IntPtr TokenInformation,
        uint TokenInformationLength,
        out uint ReturnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(
        IntPtr hExistingToken,
        uint dwDesiredAccess,
        IntPtr lpTokenAttributes,
        int ImpersonationLevel,
        int TokenType,
        out IntPtr phNewToken);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSLogoffSession(IntPtr hServer, uint sessionId, bool bWait);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    // CharSet.Unicode → CreateProcessAsUserW. STARTUPINFO below MUST match (Unicode) or lpDesktop
    // marshals as ANSI and the W API reads it as garbage, killing user32 init (STATUS_DLL_INIT_FAILED).
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(
        IntPtr hToken,
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
        public uint dwX;
        public uint dwY;
        public uint dwXSize;
        public uint dwYSize;
        public uint dwXCountChars;
        public uint dwYCountChars;
        public uint dwFillAttribute;
        public uint dwFlags;
        public ushort wShowWindow;
        public ushort cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    #endregion
}
