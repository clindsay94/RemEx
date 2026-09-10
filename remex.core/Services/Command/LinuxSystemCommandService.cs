using System.Diagnostics;
using System.Globalization;

namespace Remex.Core.Services.Command;

public class LinuxSystemCommandService : ISystemCommandService
{
    private readonly SystemProcessLauncher _launch;

    public LinuxSystemCommandService()
        : this(launcher: null)
    {
    }

    /// <summary>Test seam: records the command instead of running it. Null in production.</summary>
    internal LinuxSystemCommandService(SystemProcessLauncher? launcher)
    {
        _launch = launcher ?? StartProcess;
    }

    public Task Shutdown(int delaySeconds = 0)
    {
        ExecuteProcess("shutdown", BuildShutdownArgs("-h", delaySeconds));
        return Task.CompletedTask;
    }

    public Task ForceShutdown(int delaySeconds = 0)
    {
        if (delaySeconds <= 0)
        {
            ExecuteProcess("systemctl", "poweroff -i");
            return Task.CompletedTask;
        }

        ExecuteProcess("shutdown", BuildShutdownArgs("-h", delaySeconds));
        return Task.CompletedTask;
    }

    public Task Restart(int delaySeconds = 0)
    {
        ExecuteProcess("shutdown", BuildShutdownArgs("-r", delaySeconds));
        return Task.CompletedTask;
    }

    public Task ForceRestart(int delaySeconds = 0)
    {
        if (delaySeconds <= 0)
        {
            ExecuteProcess("systemctl", "reboot -i");
            return Task.CompletedTask;
        }

        ExecuteProcess("shutdown", BuildShutdownArgs("-r", delaySeconds));
        return Task.CompletedTask;
    }

    public Task RestartToUefi(int delaySeconds = 0)
    {
        if (delaySeconds > 0)
        {
            _ = Task.Delay(TimeSpan.FromSeconds(delaySeconds)).ContinueWith(_ =>
            {
                // Fire-and-forget: no caller is awaiting, so surface failures to stderr
                // (captured by the host/journal) instead of swallowing them silently.
                try
                {
                    ExecuteProcess("systemctl", "reboot --firmware-setup");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Deferred RestartToUefi failed: {ex.Message}");
                }
            });
            return Task.CompletedTask;
        }

        ExecuteProcess("systemctl", "reboot --firmware-setup");
        return Task.CompletedTask;
    }

    public Task Sleep()
    {
        ExecuteProcess("systemctl", "suspend");
        return Task.CompletedTask;
    }

    public Task Hibernate()
    {
        ExecuteProcess("systemctl", "hibernate");
        return Task.CompletedTask;
    }

    public Task SignOut()
    {
        ExecuteProcess("loginctl", "terminate-session self");
        return Task.CompletedTask;
    }

    public Task Lock()
    {
        ExecuteProcess("loginctl", "lock-session");
        return Task.CompletedTask;
    }

    public Task MonitorOff()
    {
        ExecuteProcess("sh", "-c \"xset dpms force off\"");
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
    /// <c>NativeDigits</c> and no <c>DigitSubstitution</c>, so only the sign can vary. The one value here, the delay in minutes, is
    /// <c>Math.Max(1, ...)</c>, so it cannot be negative and the raw interpolation this replaces
    /// was correct on all 890 cultures.
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
    /// Builds the <c>shutdown</c> argument list for a mode. Internal so the flags and the delay can
    /// be asserted without starting a process (RemEx-msyn).
    /// </summary>
    /// <remarks>
    /// <c>shutdown</c> takes MINUTES, not seconds, so a sub-minute delay rounds up to one rather than
    /// to zero — a request for 30 seconds must not become an immediate shutdown.
    /// </remarks>
    internal static string BuildShutdownArgs(string modeArg, int delaySeconds)
    {
        if (delaySeconds <= 0)
        {
            return $"{modeArg} now";
        }

        var minutes = Math.Max(1, (int)Math.Ceiling(delaySeconds / 60d));
        return $"{modeArg} +{Arg(minutes)}";
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
        catch (Exception ex)
        {
            throw new Exception($"Failed to execute {fileName} {arguments}: {ex.Message}", ex);
        }
    }
}
