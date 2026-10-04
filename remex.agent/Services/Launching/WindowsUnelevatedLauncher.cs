using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Remex.Core.Guards;
using Remex.Desktop.Services.Launching;

namespace Remex.Agent.Services.Launching;

/// <summary>
/// Windows <see cref="IUnelevatedLauncher"/>: starts user-facing targets with the desktop shell's
/// (Explorer's) normal, medium-integrity token instead of RemEx's administrator token
/// (RemEx-pp4cm.2). RemEx itself stays elevated; only what its children inherit changes.
/// </summary>
/// <remarks>
/// <para>TWO ROUTES, PICKED BY WHAT THE TARGET IS:</para>
/// <list type="bullet">
/// <item><b>Programs</b> (<c>.exe</c>, and <c>.lnk</c> shortcuts that point at one) are started with
/// <c>CreateProcessWithTokenW</c> and a primary token duplicated from the shell window's process.
/// Chosen for programs because it FAILS LOUDLY: a program whose manifest (or compatibility setting)
/// asks for administrator rights is refused with <c>ERROR_ELEVATION_REQUIRED</c>, reported as
/// <see cref="UnelevatedLaunchResult.ElevationRequired"/> so the caller falls back to the old elevated
/// launch silently. Handing such a program to Explorer would put a UAC prompt on the secure desktop,
/// which a user driving RemEx from the phone cannot answer. Any other failure on this route is
/// <see cref="UnelevatedLaunchResult.ProgramRouteUnavailable"/>, which also falls back - no worse than
/// before for a program the owner put on the launcher list.</item>
/// <item><b>Everything else</b> (web links, documents, folders) is handed to the running shell via
/// <c>IShellDispatch2.ShellExecute</c>, so it opens INSIDE Explorer at Explorer's level. A failure here
/// is retried once, then tried as <c>explorer.exe "&lt;target&gt;"</c> with the shell's token, and if
/// that fails too the result is <see cref="UnelevatedLaunchResult.Failed"/>, which the caller must NOT
/// turn into an elevated launch: a transient Explorer hiccup must never open an administrator browser
/// (security review of RemEx-pp4cm.2).</item>
/// </list>
/// <para>
/// The shell's token is checked first: an ELEVATED Explorer (TokenElevationTypeFull) or one in another
/// session is not a normal-permission source, so the program route reports ProgramRouteUnavailable and
/// the shell route Failed. The user SID is deliberately not compared: with over-the-shoulder
/// elevation, RemEx legitimately runs as a different (admin) account from the signed-in user.
/// </para>
/// <para>
/// All Win32/COM work is behind <see cref="IShellAccess"/> (<see cref="NativeShellAccess"/> in
/// production), so this class is the fallback POLICY and is unit-tested with a fake.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsUnelevatedLauncher : IUnelevatedLauncher
{
    internal const short SwHide = 0;
    internal const short SwShowNormal = 1;

    /// <summary><c>CreateProcessWithTokenW</c> rejects a longer <c>lpCommandLine</c>.</summary>
    internal const int MaxTokenCommandLineChars = 1024;

    internal static readonly TimeSpan ShellRetryDelay = TimeSpan.FromMilliseconds(400);

    private readonly ILogger<WindowsUnelevatedLauncher> _logger;
    private readonly IShellAccess _shell;

    public WindowsUnelevatedLauncher(ILogger<WindowsUnelevatedLauncher> logger)
        : this(logger, new NativeShellAccess())
    {
    }

    internal WindowsUnelevatedLauncher(ILogger<WindowsUnelevatedLauncher> logger, IShellAccess shell)
    {
        _logger = Guard.NotNull(logger);
        _shell = Guard.NotNull(shell);
    }

    /// <inheritdoc />
    public UnelevatedLaunchResult TryLaunch(string target, string? arguments, string? workingDirectory)
        => TryLaunch(target, arguments, workingDirectory, SwShowNormal, out _);

    /// <summary>
    /// <see cref="TryLaunch(string, string?, string?)"/> with the window show state and the started
    /// process id exposed, so the integration test can run a hidden child and inspect it. The id is 0
    /// when Explorer started the target (shell route).
    /// </summary>
    internal UnelevatedLaunchResult TryLaunch(
        string target, string? arguments, string? workingDirectory, short showCommand, out int processId)
    {
        processId = 0;
        ArgumentException.ThrowIfNullOrEmpty(target);

        if (!_shell.IsElevated)
        {
            // A dev run from a normal terminal: the standard launch is already at the user's level.
            _logger.LogDebug("RemEx is not running as administrator, so launches need no permission change.");
            return UnelevatedLaunchResult.NotNeeded;
        }

        var shellWindow = _shell.GetShellWindow();
        if (shellWindow == 0)
        {
            _logger.LogInformation(
                "No desktop shell is running, so {Target} will open with RemEx's administrator rights, as before.",
                target);
            return UnelevatedLaunchResult.NoShell;
        }

        var plan = Plan(target, arguments, workingDirectory, showCommand);
        if (plan.Route == LaunchRoute.ElevationRequired)
        {
            LogElevationRequired(target);
            return UnelevatedLaunchResult.ElevationRequired;
        }

        var shellProcessId = SafeShellProcessId(shellWindow);
        var unusableReason = ShellTokenProblem(shellProcessId);

        return plan.Route == LaunchRoute.Process
            ? LaunchProgram(target, plan, shellProcessId, unusableReason, out processId)
            : LaunchThroughShell(target, arguments, workingDirectory, showCommand, shellWindow, shellProcessId, unusableReason);
    }

    // ── Program route ─────────────────────────────────────────────────────────────────────────

    private UnelevatedLaunchResult LaunchProgram(
        string target, LaunchPlan plan, int shellProcessId, string? unusableReason, out int processId)
    {
        processId = 0;
        if (unusableReason is not null)
            return ProgramFallback(target, unusableReason);

        var commandLine = CommandLine(plan.Executable, plan.Arguments);
        if (commandLine.Length > MaxTokenCommandLineChars)
            return ProgramFallback(target, $"its command line is longer than the {MaxTokenCommandLineChars} characters Windows allows for this");

        try
        {
            var started = _shell.StartWithShellToken(
                shellProcessId, plan.Executable, commandLine, plan.WorkingDirectory, plan.ShowCommand, out processId);
            if (started == TokenStartResult.ElevationRequired)
            {
                LogElevationRequired(target);
                return UnelevatedLaunchResult.ElevationRequired;
            }

            _logger.LogDebug("Opened {Target} with normal permissions (program route).", target);
            return UnelevatedLaunchResult.Launched;
        }
        catch (Exception ex)
        {
            // E.g. the Secondary Logon service is disabled (CreateProcessWithTokenW needs it).
            return ProgramFallback(target, ex.Message);
        }
    }

    private UnelevatedLaunchResult ProgramFallback(string target, string reason)
    {
        _logger.LogInformation(
            "{Target} can't start with normal permissions ({Reason}), so it will open with RemEx's administrator rights, as before.",
            target, reason);
        return UnelevatedLaunchResult.ProgramRouteUnavailable;
    }

    // ── Shell route ───────────────────────────────────────────────────────────────────────────

    private UnelevatedLaunchResult LaunchThroughShell(
        string target, string? arguments, string? workingDirectory, short showCommand,
        nint shellWindow, int shellProcessId, string? unusableReason)
    {
        if (unusableReason is not null)
            return ShellFailed(null, unusableReason);

        Exception? last = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (attempt > 0)
                _shell.Delay(ShellRetryDelay); // Explorer restarting or busy: give it a moment
            try
            {
                _shell.ShellExecuteInDesktopShell(shellWindow, target, arguments, workingDirectory, showCommand);
                _logger.LogDebug("Opened {Target} with normal permissions (shell route).", target);
                return UnelevatedLaunchResult.Launched;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        // Last resort: explorer.exe "<target>" started with the shell's own token. Explorer hands it
        // to the running shell (or opens it itself), still at normal permissions.
        if (arguments is null)
        {
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            var commandLine = CommandLine(explorer, Quote(target));
            if (commandLine.Length <= MaxTokenCommandLineChars)
            {
                try
                {
                    if (_shell.StartWithShellToken(
                            shellProcessId, explorer, commandLine, workingDirectory, showCommand, out _)
                        == TokenStartResult.Started)
                    {
                        _logger.LogInformation(
                            "Explorer did not answer, so {Target} was opened through explorer.exe with normal permissions.",
                            target);
                        return UnelevatedLaunchResult.Launched;
                    }
                }
                catch (Exception ex)
                {
                    last = ex;
                }
            }
        }

        return ShellFailed(last, "the desktop shell did not respond");
    }

    private UnelevatedLaunchResult ShellFailed(Exception? cause, string reason)
    {
        // No path at Warning: it can name the user's own files.
        _logger.LogWarning(cause,
            "Could not open a link, document or folder with normal permissions ({Reason}). RemEx will not open it with administrator rights instead.",
            reason);
        return UnelevatedLaunchResult.Failed;
    }

    // ── Shell token check ─────────────────────────────────────────────────────────────────────

    private int SafeShellProcessId(nint shellWindow)
    {
        try
        {
            return _shell.GetShellProcessId(shellWindow);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the desktop shell's process id.");
            return 0;
        }
    }

    /// <summary><c>null</c> when the shell's token is a usable normal-permission source; else why not.</summary>
    private string? ShellTokenProblem(int shellProcessId)
    {
        if (shellProcessId == 0)
            return "the desktop shell's process could not be identified";

        ShellTokenInfo? info;
        try
        {
            info = _shell.QueryShellToken(shellProcessId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the desktop shell's token.");
            info = null;
        }

        if (info is not { } token)
            return "the desktop shell's permissions could not be read";
        if (token.ElevationType == ShellTokenInfo.TokenElevationTypeFull)
            return "the desktop shell itself is running as administrator";
        if (token.SessionId != _shell.CurrentSessionId)
            return "the desktop shell belongs to a different Windows session";
        return null;
    }

    // ── Planning ──────────────────────────────────────────────────────────────────────────────

    private enum LaunchRoute
    {
        Process,
        Shell,
        ElevationRequired,
    }

    private readonly record struct LaunchPlan(
        LaunchRoute Route, string Executable, string? Arguments, string? WorkingDirectory, short ShowCommand);

    /// <summary>Decides which route a target takes. Pure apart from reading a shortcut file.</summary>
    private LaunchPlan Plan(string target, string? arguments, string? workingDirectory, short showCommand)
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
    /// Resolves a <c>.lnk</c> that points at a program, so it takes the program route and its "needs
    /// administrator" signal is not lost. <c>null</c> means "hand the shortcut itself to the shell"
    /// (MSI-advertised shortcuts, shortcuts to documents or missing targets, unreadable shortcuts).
    /// </summary>
    private LaunchPlan? PlanShortcut(string shortcutPath, short fallbackShowCommand)
    {
        ShortcutInfo link;
        try
        {
            link = _shell.ReadShortcut(shortcutPath);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read a shortcut; handing it to the desktop shell as-is.");
            return null;
        }

        if (link.RunAsAdministrator) // "Run as administrator" ticked: the user asked for elevation
            return new LaunchPlan(LaunchRoute.ElevationRequired, shortcutPath, null, null, fallbackShowCommand);

        if (link.Advertised)
            return null; // MSI-advertised: the stored path is an icon file, not the program

        var executable = Environment.ExpandEnvironmentVariables(link.TargetPath);
        if (executable.Length == 0 || !IsProgram(Path.GetExtension(executable)) || !File.Exists(executable))
            return null;

        var workingDirectory = Environment.ExpandEnvironmentVariables(link.WorkingDirectory);
        return new LaunchPlan(
            LaunchRoute.Process,
            executable,
            link.Arguments.Length == 0 ? null : link.Arguments,
            workingDirectory.Length == 0 || !Directory.Exists(workingDirectory)
                ? Path.GetDirectoryName(executable)
                : workingDirectory,
            link.ShowCommand == 0 ? fallbackShowCommand : (short)link.ShowCommand);
    }

    // A trailing backslash would escape the closing quote under the usual command-line rules
    // ("C:\x\" reads as C:\x"), so it is doubled; Windows path parsing collapses the pair again.
    private static string Quote(string value) => "\"" + (value.EndsWith('\\') ? value + "\\" : value) + "\"";

    private static string CommandLine(string executable, string? arguments)
        => Quote(executable) + (string.IsNullOrEmpty(arguments) ? string.Empty : " " + arguments);

    private void LogElevationRequired(string target)
        => _logger.LogInformation(
            "{Target} asks for administrator rights, so it will open with RemEx's administrator rights, as before.",
            target);
}
