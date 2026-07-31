using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Remex.Core.Models;

namespace Remex.Agent.Services.Input;

public sealed class LinuxDesktopWindowControlService : IDesktopWindowControlService
{
    private readonly ILogger<LinuxDesktopWindowControlService> _logger;
    private readonly LinuxDesktopBackendStatus _backendStatus;
    private readonly string? _display;

    public LinuxDesktopWindowControlService(ILogger<LinuxDesktopWindowControlService> logger)
    {
        _logger = logger;
        _backendStatus = LinuxDesktopBackendProbe.Probe();
        _display = Environment.GetEnvironmentVariable("DISPLAY");

        _logger.LogInformation(
            "Linux window backend: {WindowBackend} ({WindowPath}); cursor backend: {CursorBackend} ({CursorPath})",
            _backendStatus.WindowControlBackendName ?? "none",
            _backendStatus.WindowControlToolPath ?? "n/a",
            _backendStatus.CursorQueryBackendName ?? "none",
            _backendStatus.CursorQueryToolPath ?? "n/a");
    }

    public DesktopWindowResult QueryWindows(DesktopWindowQuery query)
    {
        if (!_backendStatus.SupportsAdvancedWindowControl || _backendStatus.WindowControlToolPath is null)
        {
            return new DesktopWindowResult
            {
                RequestId = query.RequestId,
                Success = false,
                ErrorText = "Advanced window control is unavailable on this host.",
            };
        }

        try
        {
            var windowIds = SearchWindowIds(query);
            var activeWindowId = GetSingleValue("getactivewindow");
            var currentDesktop = TryParseInt(GetSingleValue("get_desktop"));
            var desktopCount = TryParseInt(GetSingleValue("get_num_desktops"));

            var windows = new List<DesktopWindowInfo>(windowIds.Count);
            foreach (var windowId in windowIds)
            {
                windows.Add(BuildWindowInfo(windowId, activeWindowId));
            }

            return new DesktopWindowResult
            {
                RequestId = query.RequestId,
                Success = true,
                Backend = _backendStatus.WindowControlBackendName,
                CurrentDesktop = currentDesktop,
                DesktopCount = desktopCount,
                Windows = windows,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Desktop window query failed for text '{SearchText}'.", query.SearchText);
            return new DesktopWindowResult
            {
                RequestId = query.RequestId,
                Success = false,
                Backend = _backendStatus.WindowControlBackendName,
                ErrorText = ex.Message,
            };
        }
    }

    public DesktopWindowResult ExecuteAction(DesktopWindowAction action)
    {
        if (!_backendStatus.SupportsAdvancedWindowControl || _backendStatus.WindowControlToolPath is null)
        {
            return new DesktopWindowResult
            {
                RequestId = action.RequestId,
                Action = action.Action,
                Success = false,
                ErrorText = "Advanced window control is unavailable on this host.",
            };
        }

        if (string.IsNullOrWhiteSpace(action.WindowId))
        {
            return new DesktopWindowResult
            {
                RequestId = action.RequestId,
                Action = action.Action,
                Success = false,
                ErrorText = "A target window ID is required.",
            };
        }

        try
        {
            switch (action.Action)
            {
                case DesktopWindowActionTypes.Activate:
                    RunWindowCommand("windowactivate", action.WindowId);
                    break;

                case DesktopWindowActionTypes.Raise:
                    RunWindowCommand("windowraise", action.WindowId);
                    break;

                case DesktopWindowActionTypes.Minimize:
                    RunWindowCommand("windowminimize", action.WindowId);
                    break;

                case DesktopWindowActionTypes.Close:
                    RunWindowCommand(GetCloseCommandName(), action.WindowId);
                    break;

                case DesktopWindowActionTypes.Resize:
                    if (!action.Width.HasValue || !action.Height.HasValue)
                    {
                        return FailedAction(action, "Resize requires both width and height.");
                    }

                    RunWindowCommand("windowsize", action.WindowId, action.Width.Value.ToString(), action.Height.Value.ToString());
                    break;

                case DesktopWindowActionTypes.MoveToDesktop:
                    if (!action.DesktopNumber.HasValue)
                    {
                        return FailedAction(action, "Move-to-desktop requires a desktop number.");
                    }

                    RunWindowCommand("set_desktop_for_window", action.WindowId, action.DesktopNumber.Value.ToString());
                    break;

                default:
                    return FailedAction(action, $"Unsupported desktop window action '{action.Action}'.");
            }

            return new DesktopWindowResult
            {
                RequestId = action.RequestId,
                Action = action.Action,
                Success = true,
                Backend = _backendStatus.WindowControlBackendName,
                CurrentDesktop = TryParseInt(GetSingleValue("get_desktop")),
                DesktopCount = TryParseInt(GetSingleValue("get_num_desktops")),
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Desktop window action {Action} failed for {WindowId}.", action.Action, action.WindowId);
            return new DesktopWindowResult
            {
                RequestId = action.RequestId,
                Action = action.Action,
                Success = false,
                Backend = _backendStatus.WindowControlBackendName,
                ErrorText = ex.Message,
            };
        }
    }

    private DesktopWindowResult FailedAction(DesktopWindowAction action, string errorText) => new()
    {
        RequestId = action.RequestId,
        Action = action.Action,
        Success = false,
        Backend = _backendStatus.WindowControlBackendName,
        ErrorText = errorText,
    };

    private List<string> SearchWindowIds(DesktopWindowQuery query)
    {
        var searchText = string.IsNullOrWhiteSpace(query.SearchText) ? ".*" : query.SearchText;
        var limit = Math.Clamp(query.Limit, 1, 100);
        var arguments = new List<string> { "search" };

        if (_backendStatus.WindowControlTool == LinuxDesktopTool.Kdotool)
        {
            arguments.AddRange(["-l", limit.ToString()]);
            if (!query.IncludeAllDesktops)
            {
                var currentDesktop = GetSingleValue("get_desktop");
                if (!string.IsNullOrWhiteSpace(currentDesktop))
                {
                    arguments.AddRange(["-D", currentDesktop]);
                }
            }
        }
        else
        {
            arguments.AddRange(["--limit", limit.ToString()]);
            if (!query.IncludeAllDesktops)
            {
                var currentDesktop = GetSingleValue("get_desktop");
                if (!string.IsNullOrWhiteSpace(currentDesktop))
                {
                    arguments.AddRange(["--desktop", currentDesktop]);
                }
            }
        }

        arguments.Add(searchText);
        return SplitLines(RunWindowCommandWithOutput(arguments.ToArray()));
    }

    private DesktopWindowInfo BuildWindowInfo(string windowId, string? activeWindowId)
    {
        var info = new DesktopWindowInfo
        {
            Id = windowId,
            Title = GetSingleValue("getwindowname", windowId),
            ClassName = GetSingleValue("getwindowclassname", windowId),
            ProcessId = TryParseInt(GetSingleValue("getwindowpid", windowId)),
            DesktopNumber = TryParseInt(GetSingleValue("get_desktop_for_window", windowId)),
            IsActive = string.Equals(windowId, activeWindowId, StringComparison.Ordinal),
        };

        var geometry = TryGetWindowGeometry(windowId);
        if (geometry is null)
        {
            return info;
        }

        return info with
        {
            X = geometry.Value.X,
            Y = geometry.Value.Y,
            Width = geometry.Value.Width,
            Height = geometry.Value.Height,
        };
    }

    private (int X, int Y, int Width, int Height)? TryGetWindowGeometry(string windowId)
    {
        try
        {
            var output = _backendStatus.WindowControlTool == LinuxDesktopTool.Xdotool
                ? RunWindowCommandWithOutput("getwindowgeometry", "--shell", windowId)
                : RunWindowCommandWithOutput("getwindowgeometry", windowId);

            if (string.IsNullOrWhiteSpace(output))
            {
                return null;
            }

            var shellPairs = ParseShellPairs(output);
            if (shellPairs.TryGetValue("X", out var xText) &&
                shellPairs.TryGetValue("Y", out var yText) &&
                shellPairs.TryGetValue("WIDTH", out var widthText) &&
                shellPairs.TryGetValue("HEIGHT", out var heightText) &&
                int.TryParse(xText, out var x) &&
                int.TryParse(yText, out var y) &&
                int.TryParse(widthText, out var width) &&
                int.TryParse(heightText, out var height))
            {
                return (x, y, width, height);
            }

            var regex = new Regex(@"x\s*[:=]\s*(-?\d+).*?y\s*[:=]\s*(-?\d+).*?width\s*[:=]\s*(\d+).*?height\s*[:=]\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            var match = regex.Match(output);
            if (match.Success &&
                int.TryParse(match.Groups[1].Value, out x) &&
                int.TryParse(match.Groups[2].Value, out y) &&
                int.TryParse(match.Groups[3].Value, out width) &&
                int.TryParse(match.Groups[4].Value, out height))
            {
                return (x, y, width, height);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Window geometry query failed for {WindowId}.", windowId);
        }

        return null;
    }

    private string GetCloseCommandName() => _backendStatus.WindowControlTool switch
    {
        LinuxDesktopTool.Xdotool => "windowclose",
        _ => "windowclose",
    };

    private string GetSingleValue(params string[] arguments)
        => RunWindowCommandWithOutput(arguments).Trim();

    private void RunWindowCommand(params string[] arguments)
    {
        _ = RunWindowCommandWithOutput(arguments);
    }

    private string RunWindowCommandWithOutput(params string[] arguments)
    {
        if (_backendStatus.WindowControlToolPath is null)
        {
            throw new InvalidOperationException("No window-control backend is available.");
        }

        return RunTool(_backendStatus.WindowControlTool, _backendStatus.WindowControlToolPath, arguments);
    }

    private string RunTool(LinuxDesktopTool tool, string toolPath, params string[] arguments)
    {
        var psi = new ProcessStartInfo(toolPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        if (tool == LinuxDesktopTool.Xdotool && !string.IsNullOrWhiteSpace(_display))
        {
            psi.Environment["DISPLAY"] = _display;
        }

        using var proc = Process.Start(psi);
        if (proc is null)
        {
            throw new InvalidOperationException($"Failed to start {toolPath}.");
        }

        var output = proc.StandardOutput.ReadToEnd();
        var error = proc.StandardError.ReadToEnd().Trim();
        proc.WaitForExit(2000);

        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? $"{toolPath} exited with code {proc.ExitCode}."
                : error);
        }

        return output;
    }

    private static List<string> SplitLines(string value)
        => value
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    private static int? TryParseInt(string? value)
        => int.TryParse(value, out var parsed) ? parsed : null;

    private static Dictionary<string, string> ParseShellPairs(string output)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0 || separator == line.Length - 1)
            {
                continue;
            }

            result[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        return result;
    }
}
