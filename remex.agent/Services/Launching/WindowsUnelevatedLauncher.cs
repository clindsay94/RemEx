using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using Remex.Core.Guards;
using Remex.Desktop.Services.Launching;

namespace Remex.Agent.Services.Launching;

/// <summary>
/// Windows <see cref="IUnelevatedLauncher"/>: starts user-facing targets with the desktop shell's
/// (Explorer's) normal, medium-integrity token instead of RemEx's administrator token
/// (RemEx-pp4cm.2). RemEx itself stays elevated; only what its children inherit changes.
/// </summary>
/// <remarks>
/// <para>
/// TWO ROUTES, PICKED BY WHAT THE TARGET IS:
/// </para>
/// <list type="bullet">
/// <item><b>Programs</b> (<c>.exe</c>, and <c>.lnk</c> shortcuts that point at one) are started with
/// <c>CreateProcessWithTokenW</c> and a primary token duplicated from the shell window's process
/// (<c>GetShellWindow</c> → <c>GetWindowThreadProcessId</c> → <c>OpenProcessToken</c> →
/// <c>DuplicateTokenEx</c>). Chosen for programs because it FAILS LOUDLY: a program whose manifest
/// (or compatibility setting) asks for administrator rights is refused with
/// <c>ERROR_ELEVATION_REQUIRED</c> (740), which is reported as
/// <see cref="UnelevatedLaunchResult.ElevationRequired"/> so the caller can fall back to the old
/// elevated launch silently - the same as it always started. Handing such a program to Explorer
/// instead would put a UAC prompt on the PC's secure desktop, which a user driving RemEx from the
/// phone cannot see or answer.</item>
/// <item><b>Everything else</b> (web links, documents, folders, shortcuts to non-programs) is handed to
/// the already-running shell: <c>IShellWindows.FindWindowSW</c> (desktop) → <c>IServiceProvider</c>
/// (<c>SID_STopLevelBrowser</c>) → <c>IShellBrowser.QueryActiveShellView</c> →
/// <c>IShellView.GetItemObject(SVGIO_BACKGROUND)</c> → <c>IShellFolderViewDual.Application</c> →
/// <c>IShellDispatch2.ShellExecute</c>. The ShellExecute then runs INSIDE Explorer, so the file
/// association, the default browser and the folder window all open at Explorer's level.</item>
/// </list>
/// <para>
/// INTEROP STYLE: classic <c>[DllImport]</c> and built-in <c>[ComImport]</c> COM, matching the rest of
/// remex.agent (WgcDesktopCapture, WindowsProcessTimes). That is safe here because the agent is
/// published ReadyToRun, NOT trimmed and NOT NativeAOT, so built-in COM interop stays supported. If the
/// publish ever turns on <c>PublishTrimmed</c>/<c>PublishAot</c>, this file has to move to
/// <c>[GeneratedComInterface]</c>/<c>[LibraryImport]</c>. The dual interfaces are declared with
/// placeholder slots in exact vtable order, taken from the Windows SDK headers (exdisp.h, shldisp.h,
/// shobjidl_core.h), so every call is early-bound through the vtable rather than IDispatch::Invoke.
/// </para>
/// <para>
/// NEVER THROWS for a failed launch: every failure becomes an <see cref="UnelevatedLaunchResult"/>,
/// and <see cref="UserLauncher.Launch"/> falls back to the standard launch.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsUnelevatedLauncher : IUnelevatedLauncher
{
    internal const short SwHide = 0;
    internal const short SwShowNormal = 1;

    private const int ErrorElevationRequired = 740;

    // IShellLinkDataList flags (shlobj_core.h SHELL_LINK_DATA_FLAGS).
    private const uint SldfHasDarwinId = 0x00001000;
    private const uint SldfRunAsUser = 0x00002000;

    private readonly ILogger<WindowsUnelevatedLauncher> _logger;

    public WindowsUnelevatedLauncher(ILogger<WindowsUnelevatedLauncher> logger)
    {
        _logger = Guard.NotNull(logger);
    }

    /// <inheritdoc />
    public UnelevatedLaunchResult TryLaunch(string target, string? arguments, string? workingDirectory)
        => TryLaunch(target, arguments, workingDirectory, SwShowNormal, out _);

    /// <summary>
    /// <see cref="TryLaunch(string, string?, string?)"/> with the window show state and the started
    /// process id exposed, so the integration test can run a hidden child and inspect it. The id is 0
    /// when the shell route was used (Explorer starts the process, not RemEx).
    /// </summary>
    internal UnelevatedLaunchResult TryLaunch(
        string target, string? arguments, string? workingDirectory, short showCommand, out int processId)
    {
        processId = 0;
        ArgumentException.ThrowIfNullOrEmpty(target);

        if (!Environment.IsPrivilegedProcess)
        {
            // A dev run from a normal terminal: the standard launch is already at the user's level.
            _logger.LogDebug("RemEx is not running as administrator, so launches need no permission change.");
            return UnelevatedLaunchResult.NotNeeded;
        }

        var shellWindow = GetShellWindow();
        if (shellWindow == 0)
        {
            _logger.LogInformation(
                "No desktop shell is running, so {Target} will open with RemEx's administrator rights, as before.",
                target);
            return UnelevatedLaunchResult.NoShell;
        }

        try
        {
            var plan = Plan(target, arguments, workingDirectory, showCommand);
            var result = plan.Route switch
            {
                LaunchRoute.ElevationRequired => UnelevatedLaunchResult.ElevationRequired,
                LaunchRoute.Process => StartWithShellToken(shellWindow, plan, out processId),
                _ => ShellExecuteInDesktopShell(shellWindow, target, arguments, workingDirectory, showCommand),
            };

            switch (result)
            {
                case UnelevatedLaunchResult.Launched:
                    _logger.LogDebug("Opened {Target} with normal permissions ({Route} route).", target, plan.Route);
                    break;
                case UnelevatedLaunchResult.ElevationRequired:
                    _logger.LogInformation(
                        "{Target} asks for administrator rights, so it will open with RemEx's administrator rights, as before.",
                        target);
                    break;
                case UnelevatedLaunchResult.NoShell:
                    _logger.LogInformation(
                        "The desktop shell could not be reached, so {Target} will open with RemEx's administrator rights, as before.",
                        target);
                    break;
            }

            return result;
        }
        catch (Exception ex)
        {
            // No path at Warning: it can name the user's own files. The Information line from the
            // caller already recorded what was being opened.
            _logger.LogWarning(ex,
                "Could not open a launch target with normal permissions; falling back to RemEx's administrator rights.");
            return UnelevatedLaunchResult.Failed;
        }
    }

    private enum LaunchRoute
    {
        Process,
        Shell,
        ElevationRequired,
    }

    private readonly record struct LaunchPlan(
        LaunchRoute Route, string Executable, string? Arguments, string? WorkingDirectory, short ShowCommand);

    /// <summary>Decides which route a target takes. Pure apart from reading a shortcut file.</summary>
    private static LaunchPlan Plan(string target, string? arguments, string? workingDirectory, short showCommand)
    {
        var shell = new LaunchPlan(LaunchRoute.Shell, target, arguments, workingDirectory, showCommand);

        if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && !uri.IsFile)
            return shell; // a web (or other protocol) link

        if (Directory.Exists(target))
            return shell;

        var extension = Path.GetExtension(target);
        if (IsProgram(extension))
        {
            return new LaunchPlan(LaunchRoute.Process, target, arguments,
                string.IsNullOrEmpty(workingDirectory) ? Path.GetDirectoryName(target) : workingDirectory,
                showCommand);
        }

        if (string.Equals(extension, ".lnk", StringComparison.OrdinalIgnoreCase) && arguments is null)
            return PlanShortcut(target, showCommand) ?? shell;

        return shell;
    }

    private static bool IsProgram(string extension)
        => string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".com", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves a <c>.lnk</c> that points at a program, so it can take the program route and its
    /// "needs administrator" signal is not lost. <c>null</c> means "hand the shortcut itself to the
    /// shell" (Store/MSI-advertised shortcuts, shortcuts to documents or missing targets).
    /// </summary>
    private static LaunchPlan? PlanShortcut(string shortcutPath, short fallbackShowCommand)
    {
        var link = CreateInProc<IShellLinkW>(ClsidShellLink);
        try
        {
            ((IPersistFile)link).Load(shortcutPath, 0 /* STGM_READ */);

            var flags = ((IShellLinkDataList)link).GetFlags();
            if ((flags & SldfRunAsUser) != 0)
            {
                // "Run as administrator" ticked on the shortcut: the user asked for elevation.
                return new LaunchPlan(LaunchRoute.ElevationRequired, shortcutPath, null, null, fallbackShowCommand);
            }

            if ((flags & SldfHasDarwinId) != 0)
                return null; // MSI-advertised: GetPath returns an icon file, not the program.

            var path = new StringBuilder(MaxPathChars);
            link.GetPath(path, path.Capacity, 0, 0);
            var executable = Environment.ExpandEnvironmentVariables(path.ToString());
            if (executable.Length == 0 || !IsProgram(Path.GetExtension(executable)) || !File.Exists(executable))
                return null;

            var args = new StringBuilder(MaxArgumentChars);
            link.GetArguments(args, args.Capacity);
            var dir = new StringBuilder(MaxPathChars);
            link.GetWorkingDirectory(dir, dir.Capacity);
            var workingDirectory = Environment.ExpandEnvironmentVariables(dir.ToString());
            var show = link.GetShowCmd();

            return new LaunchPlan(
                LaunchRoute.Process,
                executable,
                args.Length == 0 ? null : args.ToString(),
                workingDirectory.Length == 0 || !Directory.Exists(workingDirectory)
                    ? Path.GetDirectoryName(executable)
                    : workingDirectory,
                show == 0 ? fallbackShowCommand : (short)show);
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }

    private const int MaxPathChars = 32768;
    private const int MaxArgumentChars = 32768;

    // ── Program route: CreateProcessWithTokenW with the shell's token ──────────────────────────

    private UnelevatedLaunchResult StartWithShellToken(nint shellWindow, LaunchPlan plan, out int processId)
    {
        processId = 0;
        _ = GetWindowThreadProcessId(shellWindow, out var shellProcessId);
        if (shellProcessId == 0)
            return UnelevatedLaunchResult.NoShell;

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
                        wShowWindow = plan.ShowCommand,
                    };

                    // lpCommandLine must be a writable buffer, so a char[] (pinned, NUL-terminated),
                    // never a string (which the marshaller would pin in place).
                    var commandLine = ("\"" + plan.Executable + "\""
                        + (string.IsNullOrEmpty(plan.Arguments) ? string.Empty : " " + plan.Arguments)
                        + "\0").ToCharArray();

                    if (!CreateProcessWithTokenW(
                            primaryToken,
                            0,
                            plan.Executable,
                            commandLine,
                            CreateUnicodeEnvironment | CreateDefaultErrorMode,
                            environment,
                            string.IsNullOrEmpty(plan.WorkingDirectory) ? null : plan.WorkingDirectory,
                            ref startup,
                            out var info))
                    {
                        var error = Marshal.GetLastPInvokeError();
                        if (error == ErrorElevationRequired)
                            return UnelevatedLaunchResult.ElevationRequired;
                        throw new Win32Exception(error, "CreateProcessWithTokenW failed.");
                    }

                    processId = info.dwProcessId;
                    _ = AllowSetForegroundWindow(info.dwProcessId);
                    CloseHandle(info.hThread);
                    CloseHandle(info.hProcess);
                    return UnelevatedLaunchResult.Launched;
                }
                finally
                {
                    DestroyEnvironmentBlock(environment);
                }
            }
        }
    }

    // ── Shell route: IShellDispatch2.ShellExecute inside Explorer ──────────────────────────────

    private static UnelevatedLaunchResult ShellExecuteInDesktopShell(
        nint shellWindow, string target, string? arguments, string? workingDirectory, short showCommand)
    {
        var shellWindows = CreateLocal<IShellWindows>(ClsidShellWindows);
        object? found = null, browser = null, view = null, folderView = null, application = null;
        try
        {
            object? location = CsidlDesktop;
            object? root = null; // VT_EMPTY
            found = shellWindows.FindWindowSW(ref location, ref root, SwcDesktop, out _, SwfoNeedDispatch);
            if (found is null)
                return UnelevatedLaunchResult.NoShell;

            var service = SidSTopLevelBrowser;
            var shellBrowserIid = typeof(IShellBrowser).GUID;
            browser = ((IComServiceProvider)found).QueryService(ref service, ref shellBrowserIid);
            view = ((IShellBrowser)browser).QueryActiveShellView();

            var dispatchIid = IidIDispatch;
            folderView = ((IShellView)view).GetItemObject(SvgioBackground, ref dispatchIid);
            application = ((IShellFolderViewDual)folderView).GetApplication();

            // Let whatever Explorer opens take the foreground the way a direct launch would have.
            _ = GetWindowThreadProcessId(shellWindow, out var shellProcessId);
            if (shellProcessId != 0)
                _ = AllowSetForegroundWindow(shellProcessId);

            ((IShellDispatch2)application).ShellExecute(
                target,
                arguments ?? string.Empty,
                workingDirectory ?? string.Empty,
                string.Empty, // default verb
                (int)showCommand);
            return UnelevatedLaunchResult.Launched;
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

    private static T CreateLocal<T>(Guid clsid) where T : class => Create<T>(clsid, ClsctxLocalServer);

    private static T CreateInProc<T>(Guid clsid) where T : class => Create<T>(clsid, ClsctxInprocServer);

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
