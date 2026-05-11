using System.Diagnostics;

namespace Remex.Host.Services.Input;

internal enum LinuxDesktopTool
{
    None,
    Kdotool,
    Xdotool,
    Ydotool,
}

internal sealed record LinuxDesktopBackendStatus(
    string DesktopEnvironment,
    bool IsWaylandSession,
    bool IsKdePlasma,
    bool HasDisplayServer,
    LinuxDesktopTool InputTool,
    string? InputToolPath,
    LinuxDesktopTool CursorQueryTool,
    string? CursorQueryToolPath,
    LinuxDesktopTool WindowControlTool,
    string? WindowControlToolPath)
{
    public bool SupportsBasicInput => InputTool != LinuxDesktopTool.None;
    public bool SupportsCursorQuery => CursorQueryTool != LinuxDesktopTool.None;
    public bool SupportsAdvancedWindowControl => WindowControlTool != LinuxDesktopTool.None;

    public string? InputBackendName => LinuxDesktopBackendProbe.GetToolName(InputTool);
    public string? CursorQueryBackendName => LinuxDesktopBackendProbe.GetToolName(CursorQueryTool);
    public string? WindowControlBackendName => LinuxDesktopBackendProbe.GetToolName(WindowControlTool);
}

internal static class LinuxDesktopBackendProbe
{
    public static LinuxDesktopBackendStatus Probe()
    {
        var desktopEnvironment = FirstNonEmpty(
            Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP"),
            Environment.GetEnvironmentVariable("XDG_SESSION_DESKTOP"),
            Environment.GetEnvironmentVariable("DESKTOP_SESSION"));

        var sessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
        var hasWaylandDisplay = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
        var hasX11Display = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY"));
        var isWaylandSession = hasWaylandDisplay ||
            string.Equals(sessionType, "wayland", StringComparison.OrdinalIgnoreCase);

        var isKdePlasma =
            ContainsToken(desktopEnvironment, "KDE") ||
            ContainsToken(desktopEnvironment, "PLASMA") ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("KDE_SESSION_VERSION"));

        var kdotoolPath = FindExecutable("kdotool");
        var xdotoolPath = FindExecutable("xdotool");
        var ydotoolPath = FindExecutable("ydotool");

        var inputTool = SelectInputTool(isWaylandSession, xdotoolPath, ydotoolPath);
        var cursorTool = SelectCursorTool(isKdePlasma, kdotoolPath, xdotoolPath);
        var windowTool = SelectWindowTool(isKdePlasma, kdotoolPath, xdotoolPath);

        return new LinuxDesktopBackendStatus(
            DesktopEnvironment: desktopEnvironment,
            IsWaylandSession: isWaylandSession,
            IsKdePlasma: isKdePlasma,
            HasDisplayServer: hasWaylandDisplay || hasX11Display,
            InputTool: inputTool,
            InputToolPath: GetToolPath(inputTool, kdotoolPath, xdotoolPath, ydotoolPath),
            CursorQueryTool: cursorTool,
            CursorQueryToolPath: GetToolPath(cursorTool, kdotoolPath, xdotoolPath, ydotoolPath),
            WindowControlTool: windowTool,
            WindowControlToolPath: GetToolPath(windowTool, kdotoolPath, xdotoolPath, ydotoolPath));
    }

    public static string? GetToolName(LinuxDesktopTool tool) => tool switch
    {
        LinuxDesktopTool.Kdotool => "kdotool",
        LinuxDesktopTool.Xdotool => "xdotool",
        LinuxDesktopTool.Ydotool => "ydotool",
        _ => null,
    };

    public static string? FindExecutable(string name)
    {
        try
        {
            var psi = new ProcessStartInfo("which", name)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return null;
            }

            var path = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(2000);
            return proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }

    private static LinuxDesktopTool SelectInputTool(bool isWaylandSession, string? xdotoolPath, string? ydotoolPath)
    {
        if (isWaylandSession)
        {
            if (!string.IsNullOrWhiteSpace(ydotoolPath))
            {
                return LinuxDesktopTool.Ydotool;
            }

            if (!string.IsNullOrWhiteSpace(xdotoolPath))
            {
                return LinuxDesktopTool.Xdotool;
            }
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(xdotoolPath))
            {
                return LinuxDesktopTool.Xdotool;
            }

            if (!string.IsNullOrWhiteSpace(ydotoolPath))
            {
                return LinuxDesktopTool.Ydotool;
            }
        }

        return LinuxDesktopTool.None;
    }

    private static LinuxDesktopTool SelectCursorTool(bool isKdePlasma, string? kdotoolPath, string? xdotoolPath)
    {
        if (isKdePlasma && !string.IsNullOrWhiteSpace(kdotoolPath))
        {
            return LinuxDesktopTool.Kdotool;
        }

        if (!string.IsNullOrWhiteSpace(xdotoolPath))
        {
            return LinuxDesktopTool.Xdotool;
        }

        return LinuxDesktopTool.None;
    }

    private static LinuxDesktopTool SelectWindowTool(bool isKdePlasma, string? kdotoolPath, string? xdotoolPath)
    {
        if (isKdePlasma && !string.IsNullOrWhiteSpace(kdotoolPath))
        {
            return LinuxDesktopTool.Kdotool;
        }

        if (!string.IsNullOrWhiteSpace(xdotoolPath))
        {
            return LinuxDesktopTool.Xdotool;
        }

        return LinuxDesktopTool.None;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static bool ContainsToken(string? value, string token)
        => !string.IsNullOrWhiteSpace(value) &&
           value.Contains(token, StringComparison.OrdinalIgnoreCase);

    private static string? GetToolPath(LinuxDesktopTool tool, string? kdotoolPath, string? xdotoolPath, string? ydotoolPath)
        => tool switch
        {
            LinuxDesktopTool.Kdotool => kdotoolPath,
            LinuxDesktopTool.Xdotool => xdotoolPath,
            LinuxDesktopTool.Ydotool => ydotoolPath,
            _ => null,
        };
}
