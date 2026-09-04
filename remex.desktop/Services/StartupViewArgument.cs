using System;
using System.Collections.Generic;
using Remex.Desktop.ViewModels;

namespace Remex.Desktop.Services;

/// <summary>
/// Parses the <c>--view &lt;Name&gt;</c> startup argument (RemEx-8q7de) used by
/// <c>scripts/ui-palette-sweep.ps1</c> to open a specific view at launch.
/// </summary>
/// <remarks>
/// <para>
/// THIS EXISTS SO THE SWEEP NEVER SENDS A KEYSTROKE. The original design drove navigation with
/// <c>Ctrl+D1..D7</c> / <c>Ctrl+OemComma</c> via <c>SendKeys</c>, which is banned in this repo
/// (memory: eyes-pass-no-os-keystroke-injection) — nav list items expose no
/// <c>InvokePattern</c> for UI Automation to click instead. A launch argument reaches the same
/// nine destinations without injecting OS input: the sweep stops the host, starts it again with
/// <c>--view X</c>, screenshots, and stops it — one process per cell x view.
/// </para>
/// <para>
/// The names are the sweep's own vocabulary, not <see cref="ShellViewModel"/>'s command names —
/// e.g. "Sensors" is <see cref="ShellViewModel.NavigateToCanvas"/> (the sensor dashboard) and
/// "Commands" is <see cref="ShellViewModel.NavigateToRemote"/> (remote control), matched to what a
/// screenshot's filename should say rather than what the view model happens to call itself.
/// <c>RemoteDesktop</c> has no entry: it shows nothing meaningful without a connected phone, so it
/// stays a manual verification cell (see docs/UI-PALETTE-SWEEP.md).
/// </para>
/// </remarks>
public static class StartupViewArgument
{
    /// <summary>
    /// Every name <c>--view</c> accepts, in <c>Ctrl+D1..D7</c> / <c>Ctrl+OemComma</c> / (no
    /// binding) order, mapped to the <see cref="ShellViewModel"/> navigation call that opens it.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, Action<ShellViewModel>> Navigators =
        new Dictionary<string, Action<ShellViewModel>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Home"] = vm => vm.NavigateToHome(),
            ["Sensors"] = vm => vm.NavigateToCanvas(),
            ["Commands"] = vm => vm.NavigateToRemote(),
            ["Launcher"] = vm => vm.NavigateToAppLauncher(),
            ["Processes"] = vm => vm.NavigateToTaskManager(),
            ["Files"] = vm => vm.NavigateToFileTransfer(),
            ["Logs"] = vm => vm.NavigateToDiagnosticLogs(),
            ["Settings"] = vm => vm.NavigateToSettings(),
            ["About"] = vm => vm.NavigateToAbout(),
        };

    /// <summary>
    /// Reads the value following a <c>--view</c> token in the process args, or <c>null</c> when
    /// the flag is absent — including when it is the last token with no value following it. Does
    /// NOT validate the value against <see cref="Navigators"/>: an unrecognised name is returned
    /// as-is so the caller can log exactly what was typed before falling back to the default view.
    /// </summary>
    public static string? ExtractRequestedViewName(string[]? args)
    {
        if (args is null) return null;

        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--view", StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    /// <summary>
    /// Navigates <paramref name="viewModel"/> to the view named by <c>--view</c> in
    /// <paramref name="args"/>. Returns <c>true</c> when a recognised name was applied and
    /// <c>false</c> when the flag was absent or its value was not one of <see cref="Navigators"/>'
    /// keys — callers should log the unrecognised-name case themselves using
    /// <see cref="ExtractRequestedViewName"/>, since this method alone cannot distinguish "absent"
    /// from "typo'd" the way a log line should.
    /// </summary>
    public static bool TryApply(string[]? args, ShellViewModel viewModel)
    {
        var name = ExtractRequestedViewName(args);
        if (name is null || !Navigators.TryGetValue(name, out var navigate)) return false;

        navigate(viewModel);
        return true;
    }
}
