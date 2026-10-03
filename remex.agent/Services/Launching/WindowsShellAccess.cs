using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Remex.Agent.Services.Launching;

/// <summary>What <see cref="IShellAccess.QueryShellToken"/> reads from the shell process's token.</summary>
/// <param name="ElevationType">TOKEN_ELEVATION_TYPE: 1 Default (no split token), 2 Full (elevated),
/// 3 Limited (the normal half of an administrator's split token).</param>
/// <param name="SessionId">The token's terminal-services session.</param>
internal readonly record struct ShellTokenInfo(int ElevationType, int SessionId)
{
    public const int TokenElevationTypeFull = 2;
}

/// <summary>What a <c>.lnk</c> says about itself (<see cref="IShellAccess.ReadShortcut"/>).</summary>
internal sealed record ShortcutInfo(
    string TargetPath, string Arguments, string WorkingDirectory, int ShowCommand, bool RunAsAdministrator, bool Advertised);

/// <summary>Result of <see cref="IShellAccess.StartWithShellToken"/> when it does not throw.</summary>
internal enum TokenStartResult
{
    Started,
    ElevationRequired,
}

/// <summary>
/// Every Windows primitive <see cref="WindowsUnelevatedLauncher"/> needs, behind one seam so the
/// fallback POLICY (which failures may fall back to the elevated launch and which must not) can be
/// tested without a desktop, an elevated test run, or a real launch (RemEx-pp4cm.2 review).
/// </summary>
internal interface IShellAccess
{
    /// <summary>True when this process holds an elevated administrator token.</summary>
    bool IsElevated { get; }

    /// <summary>This process's session (the interactive session RemEx runs in).</summary>
    int CurrentSessionId { get; }

    /// <summary><c>GetShellWindow</c>: 0 when no desktop shell is running.</summary>
    nint GetShellWindow();

    /// <summary>The process owning the shell window, or 0.</summary>
    int GetShellProcessId(nint shellWindow);

    /// <summary>The shell process's token facts, or <c>null</c> when they cannot be read.</summary>
    ShellTokenInfo? QueryShellToken(int shellProcessId);

    /// <summary>Reads a shortcut. Throws on an unreadable file.</summary>
    ShortcutInfo ReadShortcut(string shortcutPath);

    /// <summary>
    /// <c>IShellDispatch2.ShellExecute</c> inside the running Explorer. Throws on any failure,
    /// including Explorer's desktop view not being reachable (Explorer restarting).
    /// </summary>
    void ShellExecuteInDesktopShell(nint shellWindow, string target, string? arguments, string? workingDirectory, short showCommand);

    /// <summary>
    /// <c>CreateProcessWithTokenW</c> with a primary token duplicated from the shell process. Throws
    /// <see cref="Win32Exception"/> on every failure except <c>ERROR_ELEVATION_REQUIRED</c>.
    /// </summary>
    TokenStartResult StartWithShellToken(
        int shellProcessId, string executable, string commandLine, string? workingDirectory, short showCommand, out int processId);

    /// <summary>Waits before the shell-route retry.</summary>
    void Delay(TimeSpan delay);
}

/// <summary>The real <see cref="IShellAccess"/>: Win32 and shell COM.</summary>
/// <remarks>
/// INTEROP STYLE: classic <c>[DllImport]</c> and built-in <c>[ComImport]</c> COM, matching the rest of
/// remex.agent (WgcDesktopCapture, WindowsProcessTimes). Safe because the agent is published
/// ReadyToRun, NOT trimmed and NOT NativeAOT, so built-in COM interop stays supported; if the publish
/// ever turns on <c>PublishTrimmed</c>/<c>PublishAot</c>, this file moves to
/// <c>[GeneratedComInterface]</c>/<c>[LibraryImport]</c>. The interfaces are declared with placeholder
/// slots in exact vtable order from the Windows SDK headers (exdisp.h, shldisp.h, shobjidl_core.h), so
/// every call is early-bound through the vtable rather than IDispatch::Invoke.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class NativeShellAccess : IShellAccess
{
    private const int ErrorElevationRequired = 740;

    // IShellLinkDataList flags (shlobj_core.h SHELL_LINK_DATA_FLAGS).
    private const uint SldfHasDarwinId = 0x00001000;
    private const uint SldfRunAsUser = 0x00002000;

    private const int MaxPathChars = 32768;

    public bool IsElevated => Environment.IsPrivilegedProcess;

    public int CurrentSessionId { get; } = ReadCurrentSessionId();

    private static int ReadCurrentSessionId()
    {
        using var self = Process.GetCurrentProcess();
        return self.SessionId;
    }

    nint IShellAccess.GetShellWindow() => GetShellWindow();

    public int GetShellProcessId(nint shellWindow)
    {
        _ = GetWindowThreadProcessId(shellWindow, out var pid);
        return pid;
    }

    public ShellTokenInfo? QueryShellToken(int shellProcessId)
    {
        using var process = OpenProcess(ProcessQueryLimitedInformation, false, shellProcessId);
        if (process.IsInvalid || !OpenProcessToken(process, TokenQuery, out var token))
            return null;

        using (token)
        {
            if (!GetTokenInformation(token, TokenInformationElevationType, out var elevationType, sizeof(int), out _)
                || !GetTokenInformation(token, TokenInformationSessionId, out var sessionId, sizeof(int), out _))
            {
                return null;
            }

            return new ShellTokenInfo(elevationType, sessionId);
        }
    }

    public ShortcutInfo ReadShortcut(string shortcutPath)
    {
        var link = Create<IShellLinkW>(ClsidShellLink, ClsctxInprocServer);
        try
        {
            ((IPersistFile)link).Load(shortcutPath, 0 /* STGM_READ */);
            var flags = ((IShellLinkDataList)link).GetFlags();

            var path = new StringBuilder(MaxPathChars);
            link.GetPath(path, path.Capacity, 0, 0);
            var args = new StringBuilder(MaxPathChars);
            link.GetArguments(args, args.Capacity);
            var dir = new StringBuilder(MaxPathChars);
            link.GetWorkingDirectory(dir, dir.Capacity);

            return new ShortcutInfo(
                path.ToString(), args.ToString(), dir.ToString(), link.GetShowCmd(),
                RunAsAdministrator: (flags & SldfRunAsUser) != 0,
                Advertised: (flags & SldfHasDarwinId) != 0);
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }

    public void ShellExecuteInDesktopShell(
        nint shellWindow, string target, string? arguments, string? workingDirectory, short showCommand)
    {
        var shellWindows = Create<IShellWindows>(ClsidShellWindows, ClsctxLocalServer);
        object? found = null, browser = null, view = null, folderView = null, application = null;
        try
        {
            object? location = CsidlDesktop;
            object? root = null; // VT_EMPTY
            found = shellWindows.FindWindowSW(ref location, ref root, SwcDesktop, out _, SwfoNeedDispatch)
                ?? throw new COMException("Explorer's desktop window is not registered (Explorer may be restarting).");

            var service = SidSTopLevelBrowser;
            var shellBrowserIid = typeof(IShellBrowser).GUID;
            browser = ((IComServiceProvider)found).QueryService(ref service, ref shellBrowserIid);
            view = ((IShellBrowser)browser).QueryActiveShellView();

            var dispatchIid = IidIDispatch;
            folderView = ((IShellView)view).GetItemObject(SvgioBackground, ref dispatchIid);
            application = ((IShellFolderViewDual)folderView).GetApplication();

            // Let whatever Explorer opens take the foreground the way a direct launch would have.
            var shellProcessId = GetShellProcessId(shellWindow);
            if (shellProcessId != 0)
                _ = AllowSetForegroundWindow(shellProcessId);

            ((IShellDispatch2)application).ShellExecute(
                target,
                arguments ?? string.Empty,
                workingDirectory ?? string.Empty,
                string.Empty, // default verb
                (int)showCommand);
        }
        finally
        {
            foreach (var com in new[] { application, folderView, view, browser, found, shellWindows })
            {
                if (com is not null && Marshal.IsComObject(com))
                    Marshal.ReleaseComObject(com);
            }
        }
    }

    public TokenStartResult StartWithShellToken(
        int shellProcessId, string executable, string commandLine, string? workingDirectory, short showCommand,
        out int processId)
    {
        processId = 0;
        using var shellProcess = OpenProcess(ProcessQueryLimitedInformation, false, shellProcessId);
        if (shellProcess.IsInvalid)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "OpenProcess(shell) failed.");

        if (!OpenProcessToken(shellProcess, TokenDuplicate, out var shellToken))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "OpenProcessToken(shell) failed.");

        using (shellToken)
        {
            if (!DuplicateTokenEx(shellToken,
                    TokenQuery | TokenDuplicate | TokenAssignPrimary | TokenAdjustDefault | TokenAdjustSessionId,
                    0, SecurityImpersonation, TokenPrimary, out var primaryToken))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "DuplicateTokenEx failed.");
            }

            using (primaryToken)
            {
                // The user's own environment, not RemEx's (which may carry host-only variables).
                if (!CreateEnvironmentBlock(out var environment, primaryToken, false))
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateEnvironmentBlock failed.");

                try
                {
                    var startup = new StartupInfo
                    {
                        cb = Marshal.SizeOf<StartupInfo>(),
                        dwFlags = StartfUseShowWindow,
                        wShowWindow = showCommand,
                    };

                    // lpCommandLine must be a writable buffer, so a char[] (pinned, NUL-terminated),
                    // never a string (which the marshaller would pin in place).
                    var buffer = (commandLine + "\0").ToCharArray();

                    if (!CreateProcessWithTokenW(
                            primaryToken,
                            0,
                            executable,
                            buffer,
                            CreateUnicodeEnvironment | CreateDefaultErrorMode,
                            environment,
                            string.IsNullOrEmpty(workingDirectory) ? null : workingDirectory,
                            ref startup,
                            out var info))
                    {
                        var error = Marshal.GetLastPInvokeError();
                        if (error == ErrorElevationRequired)
                            return TokenStartResult.ElevationRequired;
                        throw new Win32Exception(error, "CreateProcessWithTokenW failed.");
                    }

                    processId = info.dwProcessId;
                    _ = AllowSetForegroundWindow(info.dwProcessId);
                    CloseHandle(info.hThread);
                    CloseHandle(info.hProcess);
                    return TokenStartResult.Started;
                }
                finally
                {
                    DestroyEnvironmentBlock(environment);
                }
            }
        }
    }

    public void Delay(TimeSpan delay) => Thread.Sleep(delay);

    private static T Create<T>(Guid clsid, uint context) where T : class
    {
        var iid = typeof(T).GUID;
        var hr = CoCreateInstance(ref clsid, 0, context, ref iid, out var instance);
        Marshal.ThrowExceptionForHR(hr);
        return (T)instance;
    }

    // ── Native declarations ───────────────────────────────────────────────────────────────────

    private static readonly Guid ClsidShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid ClsidShellLink = new("00021401-0000-0000-C000-000000000046");
    private static readonly Guid SidSTopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid IidIDispatch = new("00020400-0000-0000-C000-000000000046");

    private const int CsidlDesktop = 0;
    private const int SwcDesktop = 0x8;
    private const int SwfoNeedDispatch = 0x1;
    private const uint SvgioBackground = 0;
    private const uint ClsctxInprocServer = 0x1;
    private const uint ClsctxLocalServer = 0x4;

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenAssignPrimary = 0x0001;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenQuery = 0x0008;
    private const uint TokenAdjustDefault = 0x0080;
    private const uint TokenAdjustSessionId = 0x0100;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const int TokenInformationSessionId = 12;
    private const int TokenInformationElevationType = 18;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateDefaultErrorMode = 0x04000000;
    private const int StartfUseShowWindow = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int cb;
        public nint lpReserved;
        public nint lpDesktop;
        public nint lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public nint lpReserved2;
        public nint hStdInput;
        public nint hStdOutput;
        public nint hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint hProcess;
        public nint hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("user32.dll")]
    private static extern nint GetShellWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out int processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        SafeProcessHandle processHandle, uint desiredAccess, out SafeAccessTokenHandle tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        SafeAccessTokenHandle token, int informationClass, out int information, int informationLength, out int returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(
        SafeAccessTokenHandle existingToken, uint desiredAccess, nint tokenAttributes,
        int impersonationLevel, int tokenType, out SafeAccessTokenHandle newToken);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessWithTokenW(
        SafeAccessTokenHandle token,
        uint logonFlags,
        string applicationName,
        char[] commandLine,
        uint creationFlags,
        nint environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(
        out nint environment, SafeAccessTokenHandle token, [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(nint environment);

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        ref Guid clsid, nint outer, uint context, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out object instance);

    // Vtable placeholders (Slot*) keep each declared method at its SDK slot; they are never called.

    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IShellWindows
    {
        void SlotGetCount();
        void SlotItem();
        void SlotNewEnum();
        void SlotRegister();
        void SlotRegisterPending();
        void SlotRevoke();
        void SlotOnNavigate();
        void SlotOnActivated();

        [return: MarshalAs(UnmanagedType.IDispatch)]
        object? FindWindowSW(
            [In, MarshalAs(UnmanagedType.Struct)] ref object? location,
            [In, MarshalAs(UnmanagedType.Struct)] ref object? locationRoot,
            int windowClass,
            out int hwnd,
            int options);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IComServiceProvider
    {
        [return: MarshalAs(UnmanagedType.Interface)]
        object QueryService(ref Guid service, ref Guid iid);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        void SlotGetWindow();
        void SlotContextSensitiveHelp();
        void SlotInsertMenusSB();
        void SlotSetMenuSB();
        void SlotRemoveMenusSB();
        void SlotSetStatusTextSB();
        void SlotEnableModelessSB();
        void SlotTranslateAcceleratorSB();
        void SlotBrowseObject();
        void SlotGetViewStateStream();
        void SlotGetControlWindow();
        void SlotSendControlMsg();

        [return: MarshalAs(UnmanagedType.Interface)]
        object QueryActiveShellView();
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        void SlotGetWindow();
        void SlotContextSensitiveHelp();
        void SlotTranslateAccelerator();
        void SlotEnableModeless();
        void SlotUIActivate();
        void SlotRefresh();
        void SlotCreateViewWindow();
        void SlotDestroyViewWindow();
        void SlotGetCurrentInfo();
        void SlotAddPropertySheetPages();
        void SlotSaveViewState();
        void SlotSelectItem();

        [return: MarshalAs(UnmanagedType.Interface)]
        object GetItemObject(uint item, ref Guid iid);
    }

    [ComImport, Guid("E7A1AF80-4D96-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IShellFolderViewDual
    {
        [return: MarshalAs(UnmanagedType.IDispatch)]
        object GetApplication();
    }

    [ComImport, Guid("A4C6892C-3BA9-11D2-9DEA-00C04FB16162"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IShellDispatch2
    {
        // IShellDispatch (23 methods)
        void SlotApplication();
        void SlotParent();
        void SlotNameSpace();
        void SlotBrowseForFolder();
        void SlotWindows();
        void SlotOpen();
        void SlotExplore();
        void SlotMinimizeAll();
        void SlotUndoMinimizeAll();
        void SlotFileRun();
        void SlotCascadeWindows();
        void SlotTileVertically();
        void SlotTileHorizontally();
        void SlotShutdownWindows();
        void SlotSuspend();
        void SlotEjectPC();
        void SlotSetTime();
        void SlotTrayProperties();
        void SlotHelp();
        void SlotFindFiles();
        void SlotFindComputer();
        void SlotRefreshMenu();
        void SlotControlPanelItem();

        // IShellDispatch2
        void SlotIsRestricted();

        void ShellExecute(
            [MarshalAs(UnmanagedType.BStr)] string file,
            [MarshalAs(UnmanagedType.Struct)] object arguments,
            [MarshalAs(UnmanagedType.Struct)] object directory,
            [MarshalAs(UnmanagedType.Struct)] object operation,
            [MarshalAs(UnmanagedType.Struct)] object show);
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxChars, nint findData, uint flags);
        void SlotGetIDList();
        void SlotSetIDList();
        void SlotGetDescription();
        void SlotSetDescription();
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int maxChars);
        void SlotSetWorkingDirectory();
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int maxChars);
        void SlotSetArguments();
        void SlotGetHotkey();
        void SlotSetHotkey();
        int GetShowCmd();
    }

    [ComImport, Guid("45E2B4AE-B1C3-11D0-B92F-00A0C90312E1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkDataList
    {
        void SlotAddDataBlock();
        void SlotCopyDataBlock();
        void SlotRemoveDataBlock();
        uint GetFlags();
    }
}
