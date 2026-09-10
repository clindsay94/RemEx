using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Remex.Core.Services.Command;

/// <remarks>
/// Do NOT switch the restart flags below to <c>shutdown /g</c>. That arms Windows Automatic
/// Restart Sign-On, which signs the user back in unattended after the reboot - and five
/// user-facing confirmation strings now promise the opposite. <c>Confirm_Restart_Message</c>,
/// <c>Confirm_ForceRestart_Message</c>, <c>Confirm_RebootUefi_Message</c>,
/// <c>Confirm_Shutdown_Message</c> and <c>Confirm_ForceShutdown_Message</c> tell the user, in
/// nine languages, that RemEx cannot reach the PC again until someone signs in on it, because
/// the agent is started by a per-user logon task (Windows) or an XDG autostart entry (Linux)
/// and neither fires at the sign-in screen. A "restore my apps after restart" improvement that
/// flipped <c>/r</c> to <c>/r /g</c> would silently make all five strings false, and no test
/// would fail. (RemEx-mkq1.)
/// </remarks>
public class WindowsSystemCommandService : ISystemCommandService
{
    private readonly SystemProcessLauncher _launch;

    public WindowsSystemCommandService()
        : this(launcher: null)
    {
    }

    /// <summary>Test seam: records the command instead of running it. Null in production.</summary>
    internal WindowsSystemCommandService(SystemProcessLauncher? launcher)
    {
        _launch = launcher ?? StartProcess;
    }

    private const int HwndBroadcast = 0xffff;
    private const int WmSyscommand = 0x0112;
    private const int ScMonitorpower = 0xF170;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool LockWorkStation();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public Task Shutdown(int delaySeconds = 0)
    {
        ExecuteProcess("shutdown.exe", BuildShutdownArguments("/s", delaySeconds));
        return Task.CompletedTask;
    }

    public Task ForceShutdown(int delaySeconds = 0)
    {
        ExecuteProcess("shutdown.exe", BuildShutdownArguments("/s /f", delaySeconds));
        return Task.CompletedTask;
    }

    public Task Restart(int delaySeconds = 0)
    {
        ExecuteProcess("shutdown.exe", BuildShutdownArguments("/r", delaySeconds));
        return Task.CompletedTask;
    }

    public Task ForceRestart(int delaySeconds = 0)
    {
        ExecuteProcess("shutdown.exe", BuildShutdownArguments("/r /f", delaySeconds));
        return Task.CompletedTask;
    }

    public Task RestartToUefi(int delaySeconds = 0)
    {
        ExecuteProcess("shutdown.exe", BuildShutdownArguments("/r /fw", delaySeconds));
        return Task.CompletedTask;
    }

    public Task Sleep()
    {
        ExecuteProcess("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0");
        return Task.CompletedTask;
    }

    public Task Hibernate()
    {
        ExecuteProcess("shutdown.exe", "/h");
        return Task.CompletedTask;
    }

    public Task SignOut()
    {
        ExecuteProcess("shutdown.exe", "/l");
        return Task.CompletedTask;
    }

    public Task Lock()
    {
        if (!LockWorkStation())
        {
            var error = Marshal.GetLastWin32Error();
            throw new Exception($"LockWorkStation failed with error code {error}.");
        }
        return Task.CompletedTask;
    }

    public Task MonitorOff()
    {
        _ = SendMessage((IntPtr)HwndBroadcast, WmSyscommand, (IntPtr)ScMonitorpower, (IntPtr)2);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Writes a number into a process argument, invariantly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// NOTHING IS BROKEN HERE TODAY (RemEx-msyn). Interpolating an <c>int</c> uses
    /// <c>CurrentCulture</c>, and 95 runtime cultures render a NEGATIVE one with a
    /// <c>NegativeSign</c> that is not the ASCII hyphen — sv-SE, lt-LT and fi-FI use U+2212 MINUS
    /// SIGN. A positive one has no culture-sensitive rendering at all: integers get no
    /// <c>NativeDigits</c> and no <c>DigitSubstitution</c>, so only the sign can vary. The one value here, the shutdown delay, is clamped by <see cref="NormalizeDelay"/> to
    /// <c>[0, 315360000]</c>, so it cannot be negative and the raw interpolations this replaces
    /// were correct on all 890 cultures.
    /// </para>
    /// <para>
    /// It goes through here anyway because that is the rule RemEx-hbma settled on and RemEx-j7el,
    /// RemEx-tiih, RemEx-clum and RemEx-wssm extended: one rule with no exceptions, so the safety of
    /// a signed operand does not depend on whoever adds it next remembering that this file has a
    /// different convention from the other five. This is the last of them.
    /// </para>
    /// </remarks>
    private static string Arg(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Builds the <c>shutdown.exe</c> argument list for a mode, so the flags and the delay can be
    /// asserted without starting a process.
    /// </summary>
    /// <remarks>
    /// THE FLAGS ARE A PROMISE MADE TO THE USER IN NINE LANGUAGES, which is why this is worth a seam
    /// rather than five interpolations. The type remark above records that switching <c>/r</c> to
    /// <c>/r /g</c> would arm Automatic Restart Sign-On and silently falsify five localized
    /// confirmation strings, "and no test would fail" (RemEx-mkq1). Now one does.
    /// </remarks>
    internal static string BuildShutdownArguments(string modeFlags, int delaySeconds) =>
        $"{modeFlags} /t {Arg(NormalizeDelay(delaySeconds))}";

    private static int NormalizeDelay(int delaySeconds)
    {
        return Math.Clamp(delaySeconds, 0, 315360000);
    }

    private void ExecuteProcess(string fileName, string arguments) => _launch(fileName, arguments);

    private static void StartProcess(string fileName, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            Process.Start(psi);
        }
        catch (Win32Exception ex)
        {
            // Catching Win32Exception as requested for Access Denied situations
            throw new Exception($"Failed to execute {fileName} {arguments}: {ex.Message}", ex);
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to execute {fileName} {arguments}: {ex.Message}", ex);
        }
    }
}
