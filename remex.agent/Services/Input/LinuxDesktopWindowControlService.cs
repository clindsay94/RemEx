using System.Diagnostics;
using System.Globalization;
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

                    RunWindowCommand("windowsize", action.WindowId, Arg(action.Width.Value), Arg(action.Height.Value));
                    break;

                case DesktopWindowActionTypes.MoveToDesktop:
                    if (!action.DesktopNumber.HasValue)
                    {
                        return FailedAction(action, "Move-to-desktop requires a desktop number.");
                    }

                    RunWindowCommand("set_desktop_for_window", action.WindowId, Arg(action.DesktopNumber.Value));
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

    /// <summary>
    /// Formats a number for an xdotool/kdotool argument list, invariantly.
    /// </summary>
    /// <remarks>
    /// Same rule and same reason as <c>LinuxInputSimulationService.Arg</c> (RemEx-hbma):
    /// <c>NumberFormatInfo.NegativeSign</c> is culture-dependent, and sv-SE, lt-LT and fi-FI all
    /// define it as U+2212 MINUS SIGN, which xdotool cannot parse. Not hypothetical here —
    /// <c>Width</c>, <c>Height</c> and <c>DesktopNumber</c> are unvalidated <c>int?</c> on
    /// <c>DesktopWindowAction</c> and arrive straight off the <c>/ws</c> socket, with only HasValue
    /// checks between them and this call, so a negative reaches the argument list without anything
    /// having to go wrong first. The search limit is clamped to 1..100 and so cannot; it goes
    /// through the same helper because one rule with no exceptions is what keeps the negative cases
    /// safe by construction rather than by anyone remembering which values can go negative.
    /// <para>
    /// This file was outside the sweep the bead described — it names the input service and "the
    /// Windows/router equivalents" — and was found by a reviewer asked whether the sweep was
    /// complete rather than whether the named files were done.
    /// </para>
    /// </remarks>
    internal static string Arg(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads a number out of xdotool/kdotool output, invariantly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE MIRROR IMAGE OF <see cref="Arg"/>, AND IT BREAKS A DIFFERENT SET OF LOCALES (RemEx-j7el).
    /// <c>int.TryParse</c> without a provider uses <c>CurrentCulture</c>, and a culture whose
    /// <c>NegativeSign</c> is not the ASCII hyphen rejects the hyphen xdotool actually emits. Measured
    /// across all 890 runtime cultures: parsing <c>"1920"</c> fails in none, parsing <c>"-1920"</c>
    /// fails in 57 — the ar, ckb, fa, he, ks, lrc, mzn, pa, ps, sd, ur and uz families.
    /// </para>
    /// <para>
    /// THOSE 57 ARE A SUBSET OF THE 95 THAT BREAK WHEN FORMATTING, not a disjoint set, and a first
    /// draft of this comment said the opposite. It cannot be disjoint: rejecting <c>"-1920"</c>
    /// requires <c>NegativeSign</c> to differ from the ASCII hyphen, which is the same condition that
    /// makes formatting differ. Measured — overlap 57, format-only 38.
    /// </para>
    /// <para>
    /// The 38 are the reason this still needed its own fix. They use U+2212, so they PRODUCE an
    /// unparseable argument but READ an ASCII hyphen back without complaint, because .NET accepts
    /// both where <c>NegativeSign</c> is U+2212. So fixing the formatting direction leaves parsing
    /// broken on 57 cultures and repairs nothing on the parse side at all: the two are the same
    /// condition seen from opposite ends, not the same bug.
    /// </para>
    /// <para>
    /// The values that can actually be negative are window X and Y: a window on a monitor left of or
    /// above the primary has a negative origin, the same case RemEx-r29r existed for. On an affected
    /// host the parse silently returned null and geometry simply went missing, with no error anywhere.
    /// Desktop indices, PIDs and counts cannot be negative and go through this anyway, for the reason
    /// the formatting side does: one rule with no exceptions is what keeps the negative cases safe by
    /// construction rather than by anyone remembering which values can go negative.
    /// </para>
    /// </remarks>
    internal static bool TryParse(string? text, out int value) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

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
            arguments.AddRange(["-l", Arg(limit)]);
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
            arguments.AddRange(["--limit", Arg(limit)]);
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
                TryParse(xText, out var x) &&
                TryParse(yText, out var y) &&
                TryParse(widthText, out var width) &&
                TryParse(heightText, out var height))
            {
                return (x, y, width, height);
            }

            var regex = new Regex(@"x\s*[:=]\s*(-?\d+).*?y\s*[:=]\s*(-?\d+).*?width\s*[:=]\s*(\d+).*?height\s*[:=]\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            var match = regex.Match(output);
            if (match.Success &&
                TryParse(match.Groups[1].Value, out x) &&
                TryParse(match.Groups[2].Value, out y) &&
                TryParse(match.Groups[3].Value, out width) &&
                TryParse(match.Groups[4].Value, out height))
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
        => TryParse(value, out var parsed) ? parsed : null;

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
