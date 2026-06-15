using System;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace Remex.Host;

/// <summary>
/// Windows counterpart to <see cref="HostDoctor"/> (which is Linux/portal-specific). Windows needs no
/// portal/PipeWire setup — screen capture (DXGI Desktop Duplication / GDI) and input (SendInput) are
/// built in — so the only real variable is whether FFmpeg is present for H.264 encoding (otherwise the
/// desktop stream falls back to MJPEG). Keeps <c>RemEx.Host --doctor</c> meaningful on both platforms.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsHostDoctor
{
    public static Task<int> RunAsync(CancellationToken ct = default)
    {
        Console.WriteLine("RemEx Host — Windows remote-desktop prerequisite report");
        Console.WriteLine("=======================================================");
        Console.WriteLine($"  OS                  : {Environment.OSVersion.VersionString}");

        var sessionId = Process.GetCurrentProcess().SessionId;
        Console.WriteLine($"  Session             : {(sessionId == 0 ? "Session 0 (service — no interactive desktop to capture)" : $"interactive (session {sessionId})")}");
        Console.WriteLine("  Screen capture      : DXGI Desktop Duplication / GDI (built-in)   : OK");
        Console.WriteLine("  Input simulation    : SendInput (built-in)                        : OK");

        var ffmpeg = ProbeFfmpeg();
        Console.WriteLine($"  H.264 (FFmpeg)      : {(ffmpeg ? "available" : "not found")}");

        Console.WriteLine();
        if (!ffmpeg)
        {
            Console.WriteLine("FFmpeg was not found on PATH. The desktop stream will use MJPEG. For hardware H.264,");
            Console.WriteLine("install FFmpeg and add it to PATH, e.g.:  winget install Gyan.FFmpeg");
            Console.WriteLine();
        }

        Console.WriteLine("No portal/PipeWire setup is required on Windows. Remote desktop is supported.");

        // Capture requires an interactive desktop; a service in Session 0 cannot stream, but that is by
        // design (the agent runs in Session 0 for commands only; streaming is the logged-in GUI host).
        return Task.FromResult(0);
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
