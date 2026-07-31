using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Remex.Agent.Services.RemoteDesktop.Linux;

namespace Remex.Agent;

/// <summary>
/// Entry point for the <c>remex-host --doctor</c> CLI mode. Evaluates Linux
/// remote-desktop prerequisites, prints a human-readable report, lists any
/// repair actions, and optionally executes the safe (non-elevated, non-install)
/// ones when stdin is a TTY.
/// </summary>
/// <remarks>
/// Reuses <see cref="LinuxRemoteDesktopPrerequisites"/> and
/// <see cref="LinuxDependencyRepairService"/>; the doctor is a thin presentation
/// shell, not a duplicate detection pipeline.
/// </remarks>
[SupportedOSPlatform("linux")]
internal static class HostDoctor
{
    /// <summary>
    /// Runs the doctor flow. Returns an exit code: 0 when the system can stream
    /// remote desktop (tier at least <c>PortalNoPen</c>), 1 otherwise.
    /// </summary>
    public static async Task<int> RunAsync(bool fix = false, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux())
        {
            Console.Error.WriteLine("remex-host --doctor is only supported on Linux.");
            return 2;
        }

        var loggerFactory = LoggerFactory.Create(b =>
            b.AddSimpleConsole(opts =>
            {
                opts.SingleLine = true;
                opts.TimestampFormat = "HH:mm:ss ";
            }).SetMinimumLevel(LogLevel.Information));

        var prereqLogger = loggerFactory.CreateLogger<LinuxRemoteDesktopPrerequisites>();
        var repairLogger = loggerFactory.CreateLogger<LinuxDependencyRepairService>();
        var prereqs = new LinuxRemoteDesktopPrerequisites(prereqLogger);
        var repair = new LinuxDependencyRepairService(prereqs, repairLogger);

        Console.WriteLine("RemEx Host — Linux remote-desktop prerequisite report");
        Console.WriteLine("=====================================================");

        var report = await prereqs.EvaluateAsync(ct);
        PrintReport(report);

        var plan = prereqs.BuildRepairPlan(report);
        if (plan.Actions.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("No repair actions required.");
            return report.CanStream && report.PortalCaptureOperational ? 0 : 1;
        }

        Console.WriteLine();
        Console.WriteLine("Repair plan:");
        var idx = 1;
        foreach (var action in plan.Actions)
        {
            var elev = action.RequiresElevation ? " [sudo]" : string.Empty;
            var install = action.Kind == LinuxRepairActionKind.InstallPackage ? " [install]" : string.Empty;
            Console.WriteLine($"  {idx,2}. [{action.Kind}]{elev}{install} {action.Description}");
            if (!string.IsNullOrEmpty(action.Command))
                Console.WriteLine($"        $ {action.Command}");
            else if (action.ArgumentList is { Count: > 0 } argv)
                Console.WriteLine($"        $ {argv[0]} (+ {argv.Count - 1} arg(s))");
            idx++;
        }

        // Without --fix the doctor only runs SAFE actions (non-install, non-elevated). With --fix it
        // runs the full plan — including package installs and sudo steps — after confirmation.
        if (!fix)
        {
            var anySafe = false;
            foreach (var action in plan.Actions)
            {
                if (action.Kind == LinuxRepairActionKind.InstallPackage) continue;
                if (action.RequiresElevation) continue;
                if (action.Kind == LinuxRepairActionKind.Manual) continue;
                anySafe = true;
                break;
            }

            if (!anySafe)
            {
                Console.WriteLine();
                Console.WriteLine("No safe (non-elevated, non-install) actions to run automatically.");
                Console.WriteLine("Re-run with `--doctor --fix` to install packages / apply sudo steps, or run the commands above by hand.");
                return report.CanStream && report.PortalCaptureOperational ? 0 : 1;
            }
        }

        var prompt = fix
            ? "\nApply ALL repairs, including package installs and sudo steps? [y/N] "
            : "\nApply safe repairs? [y/N] ";

        if (!Console.IsInputRedirected && Environment.UserInteractive)
        {
            Console.Write(prompt);
            var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
            if (answer is not ("y" or "yes"))
            {
                Console.WriteLine("Skipped.");
                return report.CanStream && report.PortalCaptureOperational ? 0 : 1;
            }
        }
        else
        {
            // Non-interactive (installer post-step or piped).
            Console.WriteLine(fix
                ? "\nNon-interactive mode: applying all repairs (including installs)."
                : "\nNon-interactive mode: applying safe repairs.");
        }

        var results = await repair.RepairAsync(
            plan,
            allowPackageInstall: fix,
            allowElevated: fix,
            ct);

        Console.WriteLine();
        Console.WriteLine("Repair results:");
        foreach (var r in results)
        {
            var status = r.Success ? "OK    " : "FAILED";
            Console.WriteLine($"  [{status}] {r.Action.Description}");
            if (!r.Success && !string.IsNullOrEmpty(r.ErrorMessage))
                Console.WriteLine($"           {r.ErrorMessage}");
        }

        // Re-evaluate to see if the safe actions actually fixed anything.
        Console.WriteLine();
        Console.WriteLine("Re-evaluating after repairs...");
        var post = await prereqs.EvaluateAsync(ct);
        Console.WriteLine($"  Tier: {post.SelectedTier}");
        Console.WriteLine($"  RemoteDesktop interface: {(post.PortalRemoteDesktopAvailable ? "available" : "missing")}");
        return post.CanStream && post.PortalCaptureOperational ? 0 : 1;
    }

    private static void PrintReport(LinuxPrerequisiteReport r)
    {
        Console.WriteLine($"  Session type            : {r.SessionType ?? "(unset)"}");
        Console.WriteLine($"  Wayland display         : {r.WaylandDisplay ?? "(none)"}");
        Console.WriteLine($"  X11 display             : {r.X11Display ?? "(none)"}");
        Console.WriteLine($"  D-Bus session bus       : {(r.SessionBusAvailable ? "available" : "MISSING")}");
        Console.WriteLine($"  Portal RemoteDesktop    : {(r.PortalRemoteDesktopAvailable ? "available" : "MISSING")}");
        Console.WriteLine($"  Portal ScreenCast       : {(r.PortalScreenCastAvailable ? "available" : "MISSING")}");
        Console.WriteLine($"  Portal backend installed: {(r.PortalBackendInstalled ? r.PortalBackendPackageName ?? "yes" : "no")}");
        if (r.PortalBackendInstalled && !r.PortalRemoteDesktopAvailable)
            Console.WriteLine($"     -> backend implements RemoteDesktop: {r.PortalBackendImplementsRemoteDesktop} (frontend likely stale)");
        if (!string.IsNullOrEmpty(r.PortalUnavailableReason))
            Console.WriteLine($"  Portal reason          : {r.PortalUnavailableReason}");
        Console.WriteLine($"  PipeWire service        : {(r.PipeWireRunning ? "running" : "stopped")}");
        Console.WriteLine($"  WirePlumber             : {(r.WirePlumberRunning ? "running" : "stopped")}");
        Console.WriteLine($"  libpipewire-0.3         : {(r.PipeWireLibraryAvailable ? r.PipeWireLibraryPath : "MISSING")}");
        Console.WriteLine($"  native PipeWire bridge  : {(r.NativeBridgeAvailable ? r.NativeBridgePath : r.NativeBridgeUnavailableReason ?? "MISSING")}");
        Console.WriteLine($"  libei                   : {(r.LibeiAvailable ? r.LibeiLibraryPath : "MISSING")}");
        Console.WriteLine($"  libevdev                : {(r.LibevdevAvailable ? r.LibevdevLibraryPath : "MISSING")}");
        Console.WriteLine($"  /dev/uinput             : {(r.UinputNodeExists ? (r.UinputWritable ? "writable" : "exists (RO)") : "MISSING")}");
        Console.WriteLine($"  ffmpeg                  : {(r.FfmpegAvailable ? r.FfmpegPath : "MISSING")}");
        Console.WriteLine($"  NVENC                   : {(r.NvencAvailable ? "available" : "no")}");
        Console.WriteLine($"  VA-API                  : {(r.VaapiAvailable ? "available" : "no")}");
        Console.WriteLine();
        Console.WriteLine($"  Selected tier           : {r.SelectedTier}");
        if (!string.IsNullOrEmpty(r.DegradedReason))
            Console.WriteLine($"  Degraded reason         : {r.DegradedReason}");
        if (r.Issues.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Issues:");
            foreach (var issue in r.Issues)
                Console.WriteLine($"  - {issue}");
        }
    }
}
