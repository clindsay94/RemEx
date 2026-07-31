using System.Diagnostics;
using System.Runtime.Versioning;

namespace Remex.Agent;

/// <summary>
/// Windows counterpart to <see cref="HostDoctor"/> (which is Linux/portal-specific). Windows needs no
/// portal/PipeWire setup — screen capture (DXGI Desktop Duplication / GDI) and input (SendInput) are
/// built in — so the only real variable is whether FFmpeg is present for H.264 encoding (otherwise the
/// desktop stream falls back to MJPEG). Keeps <c>Remex.Agent --doctor</c> meaningful on both platforms.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsHostDoctor
{
    public static async Task<int> RunAsync(bool fix = false, CancellationToken ct = default)
    {
        Console.WriteLine("RemEx Host — Windows remote-desktop prerequisite report");
        Console.WriteLine("=======================================================");
        Console.WriteLine($"  OS                  : {Environment.OSVersion.VersionString}");

        var sessionId = Process.GetCurrentProcess().SessionId;
        Console.WriteLine($"  Session             : {(sessionId == 0 ? "Session 0 (non-interactive — no desktop to capture; RemEx should run in your signed-in session)" : $"interactive (session {sessionId})")}");
        Console.WriteLine("  Screen capture      : DXGI Desktop Duplication / GDI (built-in)   : OK");
        Console.WriteLine("  Input simulation    : SendInput (built-in)                        : OK");

        var ffmpeg = ProbeFfmpeg();
        Console.WriteLine($"  H.264 (FFmpeg)      : {(ffmpeg ? "available" : "not found")}");

        Console.WriteLine();
        if (!ffmpeg)
        {
            if (fix)
            {
                Console.WriteLine("Installing FFmpeg via winget (Gyan.FFmpeg)...");
                var installed = await InstallFfmpegAsync(ct);
                Console.WriteLine(installed
                    ? "FFmpeg installed. Open a new terminal so the updated PATH takes effect."
                    : "winget install failed. Install FFmpeg manually and add it to PATH: winget install Gyan.FFmpeg");
                Console.WriteLine();
            }
            else
            {
                Console.WriteLine("FFmpeg was not found on PATH. The desktop stream will use MJPEG. For hardware H.264,");
                Console.WriteLine("re-run with `--doctor --fix` (installs via winget), or run:  winget install Gyan.FFmpeg");
                Console.WriteLine();
            }
        }

        Console.WriteLine("No portal/PipeWire setup is required on Windows. Remote desktop is supported.");

        // Capture requires an interactive desktop, and remex.agent always has one: it runs in the
        // signed-in user's session (elevated, started by the logon task), not in Session 0. That is
        // why capture and SendInput reach the user's desktop directly, with no session bridging.
        return 0;
    }

    private static async Task<bool> InstallFfmpegAsync(CancellationToken ct)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "winget",
                Arguments = "install --id Gyan.FFmpeg --silent --accept-package-agreements --accept-source-agreements",
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null)
            {
                return false;
            }

            await process.WaitForExitAsync(ct);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool ProbeFfmpeg()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = "-version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null)
            {
                return false;
            }

            process.WaitForExit(3000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
