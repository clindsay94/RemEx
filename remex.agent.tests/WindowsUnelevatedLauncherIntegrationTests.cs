using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Abstractions;
using Remex.Agent.Services.Launching;
using Remex.Desktop.Services.Launching;

namespace Remex.Agent.Tests;

/// <summary>
/// RemEx-pp4cm.2, against the REAL Windows launcher: a child started through
/// <see cref="WindowsUnelevatedLauncher"/> from an elevated process must not be high integrity, and a
/// program that asks for administrator rights must be reported back for the elevated fallback rather
/// than started.
/// </summary>
/// <remarks>
/// Only meaningful from an ELEVATED test run, because that is the only situation where there is a
/// token to drop; from a normal terminal the launcher correctly answers NotNeeded and these skip with
/// that reason. Every child is started hidden (SW_HIDE) and either exits on its own or is killed, so
/// nothing is left on the desktop.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsUnelevatedLauncherIntegrationTests : IDisposable
{
    // Integrity-level SIDs as whoami /groups prints them. Locale-independent, unlike the labels.
    private const string MediumIntegritySid = "S-1-16-8192";
    private const string HighIntegritySid = "S-1-16-12288";
    private const string SystemIntegritySid = "S-1-16-16384";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "remex-pp4cm2-it-" + Guid.NewGuid().ToString("N"));

    public WindowsUnelevatedLauncherIntegrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static WindowsUnelevatedLauncher Launcher() => new(NullLogger<WindowsUnelevatedLauncher>.Instance);

    [ElevatedWindowsOnlyFact]
    public void AChildStartedFromTheElevatedHostRunsAtMediumIntegrity()
    {
        var output = Path.Combine(_dir, "groups.txt");
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

        var result = Launcher().TryLaunch(
            cmd, $"/d /c whoami /groups > \"{output}\" 2>&1", _dir, WindowsUnelevatedLauncher.SwHide, out var pid);

        Assert.Equal(UnelevatedLaunchResult.Launched, result);
        Assert.NotEqual(0, pid);
        WaitForExitOrKill(pid);

        var groups = File.ReadAllText(output);
        Assert.Contains(MediumIntegritySid, groups);
        Assert.DoesNotContain(HighIntegritySid, groups);
        Assert.DoesNotContain(SystemIntegritySid, groups);
    }

    [ElevatedWindowsOnlyFact]
    public void ADocumentHandedToTheDesktopShellOpensAtMediumIntegrity()
    {
        // The shell route (links, documents, folders): the ShellExecute runs inside Explorer via
        // IShellDispatch2. A .cmd is the one "document" whose opening can report its own integrity,
        // and it closes itself; SW_HIDE is passed through to Explorer so no console shows.
        var output = Path.Combine(_dir, "shell-groups.txt");
        var script = Path.Combine(_dir, "probe.cmd");
        File.WriteAllText(script, $"@whoami /groups > \"{output}\" 2>&1\r\n");

        var result = Launcher().TryLaunch(script, null, _dir, WindowsUnelevatedLauncher.SwHide, out var pid);

        Assert.Equal(UnelevatedLaunchResult.Launched, result);
        Assert.Equal(0, pid); // Explorer started it, not RemEx
        var groups = ReadWhenWritten(output);
        Assert.Contains(MediumIntegritySid, groups);
        Assert.DoesNotContain(HighIntegritySid, groups);
    }

    [ElevatedWindowsOnlyFact]
    public void AShortcutToAProgramIsResolvedAndStartedAtMediumIntegrity()
    {
        // Shortcuts to programs take the program route (so "needs administrator" is still detected),
        // which means the launcher reads the .lnk's target, arguments and working folder itself.
        var output = Path.Combine(_dir, "lnk-groups.txt");
        var shortcut = Path.Combine(_dir, "probe.lnk");
        CreateShortcut(
            shortcut,
            Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            $"/d /c whoami /groups > \"{output}\" 2>&1",
            _dir);

        var result = Launcher().TryLaunch(shortcut, null, null, WindowsUnelevatedLauncher.SwHide, out var pid);

        Assert.Equal(UnelevatedLaunchResult.Launched, result);
        Assert.NotEqual(0, pid); // program route: RemEx started it with the shell's token
        WaitForExitOrKill(pid);
        var groups = ReadWhenWritten(output);
        Assert.Contains(MediumIntegritySid, groups);
        Assert.DoesNotContain(HighIntegritySid, groups);
    }

    [ElevatedWindowsOnlyFact]
    public void AProgramThatAsksForAdministratorRightsIsHandedBackForTheElevatedFallback()
    {
        // regedit.exe's manifest asks for highestAvailable, which for an administrator's normal token
        // means "elevate" - CreateProcessWithTokenW refuses it with ERROR_ELEVATION_REQUIRED, and the
        // launcher must report that instead of starting anything.
        var regedit = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "regedit.exe");
        Assert.True(File.Exists(regedit), "regedit.exe should exist on every Windows install");

        var result = Launcher().TryLaunch(regedit, null, null, WindowsUnelevatedLauncher.SwHide, out var pid);

        if (pid != 0)
            WaitForExitOrKill(pid, waitMs: 0); // never leave a window behind, even on failure
        Assert.Equal(UnelevatedLaunchResult.ElevationRequired, result);
    }

    /// <summary>Polls until the probe's output file is complete (whoami writes it in one go and exits).</summary>
    private static string ReadWhenWritten(string path)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(path))
                {
                    var text = File.ReadAllText(path);
                    if (text.Contains("S-1-16-", StringComparison.Ordinal))
                        return text;
                }
            }
            catch (IOException)
            {
                // still being written
            }

            Thread.Sleep(100);
        }

        throw new TimeoutException($"The probe never wrote its group list to {path}.");
    }

    /// <summary>Writes a .lnk through the stock WScript.Shell automation object.</summary>
    private static void CreateShortcut(string path, string target, string arguments, string workingDirectory)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("WScript.Shell is not registered.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic link = shell.CreateShortcut(path);
            link.TargetPath = target;
            link.Arguments = arguments;
            link.WorkingDirectory = workingDirectory;
            link.WindowStyle = 7; // minimised; the launcher's SW_HIDE only applies when the .lnk has none
            link.Save();
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }

    private static void WaitForExitOrKill(int pid, int waitMs = 15_000)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return; // already exited
        }

        using (process)
        {
            if (!process.WaitForExit(waitMs))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                process.WaitForExit(5_000);
            }
        }
    }
}

/// <summary>
/// Runs only on Windows from an elevated (administrator) test process; skips with the reason
/// otherwise. xUnit honours <see cref="FactAttribute.Skip"/> set in a derived attribute's constructor,
/// same as <see cref="WindowsOnlyFactAttribute"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ElevatedWindowsOnlyFactAttribute : FactAttribute
{
    public ElevatedWindowsOnlyFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Windows-only: de-elevation drops a Windows administrator token.";
        else if (!Environment.IsPrivilegedProcess)
            Skip = "Needs an elevated test run: this process is not running as administrator, so there is "
                + "no administrator token to drop and the launcher correctly answers NotNeeded. Run the "
                + "tests from an elevated terminal to exercise it.";
    }
}
