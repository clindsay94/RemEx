using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Remex.Core.Logging;
using Remex.Desktop.ViewModels;

namespace Remex.Desktop.Services.Launching;

/// <summary>
/// Opens a folder, or shows a file selected inside its folder, in the PC's file manager: Explorer on Windows, the
/// desktop's file manager on Linux. The host runs elevated, so every Windows launch goes through
/// <see cref="IUnelevatedLauncher"/> and never starts an elevated Explorer.
/// </summary>
public static class FileManagerLauncher
{
    /// <summary>How long the Linux file manager gets to answer before a reveal counts as failed.</summary>
    private static readonly TimeSpan LinuxReplyTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Opens <paramref name="directory"/> in the file manager.</summary>
    public static void OpenFolder(string directory)
    {
        try
        {
            DiagnosticLogsViewModel.LaunchFolder(directory);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            InMemoryLogSink.Append(LogLevel.Warning, "Launch", "Could not open a folder in the file manager", ex);
        }
    }

    /// <summary>Opens the file manager with <paramref name="path"/> selected, or just its folder when that is not possible.</summary>
    public static void Reveal(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        if (OperatingSystem.IsWindows())
        {
            RevealOnWindows(path, EmbeddedHostServiceLocator.TryResolve<IUnelevatedLauncher>(), OpenFolder, StartProcess);
        }
        else if (OperatingSystem.IsLinux())
        {
            RevealOnLinux(path, OpenFolder, StartProcess);
        }
        else
        {
            OpenFolder(Path.GetDirectoryName(path) ?? path);
        }
    }

    internal static string WindowsSelectArguments(string path) => $"/select,\"{path}\"";

    internal static string LinuxShowItemsUri(string path) => new Uri(path).AbsoluteUri;

    internal static IReadOnlyList<string> LinuxShowItemsArguments(string path) =>
    [
        "--session", "--print-reply", "--dest=org.freedesktop.FileManager1", "--type=method_call",
        "/org/freedesktop/FileManager1", "org.freedesktop.FileManager1.ShowItems",
        "array:string:" + LinuxShowItemsUri(path), "string:",
    ];

    internal static string WindowsExplorerPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    internal static void RevealOnWindows(
        string path, IUnelevatedLauncher? unelevated, Action<string> openFolder, Func<ProcessStartInfo, Process?> start)
    {
        var explorer = WindowsExplorerPath();
        var arguments = WindowsSelectArguments(path);
        var result = unelevated?.TryLaunch(explorer, arguments, workingDirectory: null);

        switch (result)
        {
            case UnelevatedLaunchResult.Launched:
                return;
            case null:
            case UnelevatedLaunchResult.NotNeeded:
                // Not elevated, so a plain start is already at the user's level.
                try
                {
                    start(new ProcessStartInfo { FileName = explorer, Arguments = arguments, UseShellExecute = false })?.Dispose();
                    return;
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    InMemoryLogSink.Append(LogLevel.Warning, "Launch", "Explorer did not start for a reveal", ex);
                }
                break;
        }

        // Any other answer (ElevationRequired, ProgramRouteUnavailable, NoShell, Failed): never an elevated Explorer.
        openFolder(Path.GetDirectoryName(path) ?? path);
    }

    internal static void RevealOnLinux(string path, Action<string> openFolder, Func<ProcessStartInfo, Process?> start)
    {
        var parent = Path.GetDirectoryName(path) ?? path;
        var psi = new ProcessStartInfo { FileName = "dbus-send", UseShellExecute = false };
        foreach (var argument in LinuxShowItemsArguments(path))
            psi.ArgumentList.Add(argument);

        Process? process;
        try
        {
            process = start(psi);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            process = null;
        }

        if (process is null)
        {
            openFolder(parent);
            return;
        }

        WaitForReplyAsync(process, parent, openFolder).FireAndForget("revealing a file in the file manager");
    }

    internal static async Task WaitForReplyAsync(Process process, string parent, Action<string> openFolder)
    {
        using (process)
        {
            using var timeout = new CancellationTokenSource(LinuxReplyTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                return; // still waiting on the file manager after 2 s: leave it be
            }

            if (process.ExitCode != 0)
                openFolder(parent);
        }
    }

    private static Process? StartProcess(ProcessStartInfo psi) => Process.Start(psi);
}
