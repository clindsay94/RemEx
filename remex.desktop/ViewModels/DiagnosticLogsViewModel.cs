using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Remex.Core.Logging;
using Remex.Desktop.Services;

namespace Remex.Desktop.ViewModels;

/// <summary>
/// Diagnostic logs page. The sink retains entries down to a low capture floor; this view-model
/// filters that retained buffer <b>non-destructively and live</b> — changing the display level,
/// preset, or search text re-projects what is shown without discarding anything captured. Logs
/// can be exported (TXT or JSON) at a chosen scope, independent of the current on-screen view.
/// </summary>
public partial class DiagnosticLogsViewModel : ObservableObject, IDisposable
{
    private const int MaxTrackedEntries = 3000;

    private readonly ShellViewModel _shell;
    private readonly List<LogEntry> _all = new();

    /// <summary>Filtered entries currently shown in the live log list.</summary>
    public ObservableCollection<LogEntry> VisibleEntries { get; } = new();

    /// <summary>Diagnostic presets that scope the live view by subsystem / severity.</summary>
    public ObservableCollection<LogPreset> Presets { get; } = new();

    public ObservableCollection<string> LevelOptions { get; } = new() { "Trace", "Debug", "Information", "Warning", "Error" };
    public ObservableCollection<string> CaptureLevelOptions { get; } = new() { "Trace", "Debug", "Information" };
    public ObservableCollection<string> ExportFormats { get; } = new() { "TXT", "JSON" };
    public ObservableCollection<LogExportScope> ExportScopes { get; } = new();

    [ObservableProperty] private string _selectedDisplayLevel = "Information";
    [ObservableProperty] private LogPreset? _selectedPreset;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _selectedCaptureLevel = "Debug";
    [ObservableProperty] private string _selectedExportFormat = "TXT";
    [ObservableProperty] private LogExportScope? _selectedExportScope;
    [ObservableProperty] private string _serviceLogsText = string.Empty;

    /// <summary>"shown / retained" counter for the status line.</summary>
    public string EntryCountText => $"{VisibleEntries.Count} / {_all.Count}";

    /// <summary>Set by the view to present a save-file picker (prompts the user to name the export).</summary>
    public Func<FilePickerSaveOptions, Task<IStorageFile?>>? PickSaveFileAsync { get; set; }

    public DiagnosticLogsViewModel(ShellViewModel shell)
    {
        _shell = shell;

        BuildPresetsAndScopes();
        SelectedPreset = Presets.FirstOrDefault();
        SelectedExportScope = ExportScopes.FirstOrDefault();

        // Reflect the current capture floor without triggering a reload before the buffer loads.
        _selectedCaptureLevel = InMemoryLogSink.MinimumLogLevel.ToString();

        _all.AddRange(InMemoryLogSink.GetEntries());
        RebuildVisible();

        InMemoryLogSink.LogAdded += OnLogAdded;
    }

    private void BuildPresetsAndScopes()
    {
        var l = LocalizationService.Instance;

        Presets.Add(new LogPreset(l["Logs_PresetAll"], null, Array.Empty<string>()));
        Presets.Add(new LogPreset(l["Logs_PresetErrors"], LogLevel.Warning, Array.Empty<string>()));
        Presets.Add(new LogPreset(l["Logs_PresetConnection"], null, new[] { "Pairing", "Certificate", "Paired", "Connection", "WebSocket", "Ws", "Handshake" }));
        Presets.Add(new LogPreset(l["Logs_PresetRemoteDesktop"], null, new[] { "Desktop", "Capture", "Encoder", "H264", "Stream", "Frame", "Nvenc" }));
        Presets.Add(new LogPreset(l["Logs_PresetFileTransfer"], null, new[] { "FileTransfer", "Transfer", "FileHost", "Consent", "Trust" }));
        Presets.Add(new LogPreset(l["Logs_PresetInput"], null, new[] { "Input", "Command", "SendInput", "PingPong", "Remote" }));
        Presets.Add(new LogPreset(l["Logs_PresetStartup"], null, new[] { "Bootstrap", "Host", "Elevation", "Startup", "Program", "Autostart" }));

        // Export scopes: current on-screen view, everything retained, verbosity floors, and each preset.
        ExportScopes.Add(new LogExportScope(l["Logs_ScopeCurrentView"], LogExportScopeKind.CurrentView, null, null));
        ExportScopes.Add(new LogExportScope(l["Logs_ScopeEverything"], LogExportScopeKind.Everything, null, null));
        ExportScopes.Add(new LogExportScope(l["Logs_ScopeWarningsPlus"], LogExportScopeKind.LevelFloor, LogLevel.Warning, null));
        ExportScopes.Add(new LogExportScope(l["Logs_ScopeErrorsOnly"], LogExportScopeKind.LevelFloor, LogLevel.Error, null));
        foreach (var preset in Presets.Skip(1)) // skip "All" — "Everything" already covers it
            ExportScopes.Add(new LogExportScope(preset.Name, LogExportScopeKind.Preset, null, preset));
    }

    private LogLevel DisplayLevel =>
        Enum.TryParse<LogLevel>(SelectedDisplayLevel, out var level) ? level : LogLevel.Information;

    private bool PassesDisplay(LogEntry e)
    {
        if (e.Level < DisplayLevel) return false;
        if (SelectedPreset is { } preset && !preset.Matches(e)) return false;
        if (!string.IsNullOrWhiteSpace(SearchText) &&
            !e.Message.Contains(SearchText, StringComparison.OrdinalIgnoreCase) &&
            !e.Category.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    private void RebuildVisible()
    {
        VisibleEntries.Clear();
        foreach (var e in _all)
            if (PassesDisplay(e))
                VisibleEntries.Add(e);
        OnPropertyChanged(nameof(EntryCountText));
    }

    partial void OnSelectedDisplayLevelChanged(string value) => RebuildVisible();
    partial void OnSelectedPresetChanged(LogPreset? value) => RebuildVisible();
    partial void OnSearchTextChanged(string value) => RebuildVisible();

    partial void OnSelectedCaptureLevelChanged(string value)
    {
        if (!Enum.TryParse<LogLevel>(value, out var level)) return;
        // Adjust the capture floor (forward-looking). Lowering it captures more going forward;
        // raising it stops capturing below the new floor but does NOT drop what is already retained.
        InMemoryLogSink.MinimumLogLevel = level;
        _all.Clear();
        _all.AddRange(InMemoryLogSink.GetEntries());
        RebuildVisible();
    }

    [RelayCommand]
    public void RefreshLogs()
    {
        _all.Clear();
        _all.AddRange(InMemoryLogSink.GetEntries());
        RebuildVisible();
    }

    /// <summary>
    /// Delegate set by the View to display a confirmation dialog.
    /// Parameters: (title, message, confirmButtonText). Returns true if the user confirmed.
    /// </summary>
    public Func<string, string, string, Task<bool>>? OnConfirmationRequested { get; set; }

    /// <summary>
    /// Discards the in-memory diagnostic log. Async only so the confirmation can be awaited; the
    /// MVVM Toolkit still generates <c>ClearLogsCommand</c>, so the existing binding is unaffected.
    /// </summary>
    [RelayCommand]
    public async Task ClearLogsAsync()
    {
        // These entries are the only copy of anything not yet flushed to disk, and there is no undo,
        // so confirm first (RemEx-6p1f). Fails CLOSED: with no dialog wired the log is kept.
        if (OnConfirmationRequested is null
            || !await OnConfirmationRequested(
                LocalizationService.Instance["Confirm_ClearLogs_Title"],
                LocalizationService.Instance["Confirm_ClearLogs_Msg"],
                LocalizationService.Instance["Logs_Clear"]))
        {
            return;
        }

        InMemoryLogSink.Clear();
        _all.Clear();
        VisibleEntries.Clear();
        OnPropertyChanged(nameof(EntryCountText));
    }

    private void OnLogAdded(LogEntry entry)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _all.Add(entry);
            if (_all.Count > MaxTrackedEntries)
            {
                var removed = _all[0];
                _all.RemoveAt(0);
                VisibleEntries.Remove(removed);
            }

            if (PassesDisplay(entry))
                VisibleEntries.Add(entry);

            OnPropertyChanged(nameof(EntryCountText));
        });
    }

    // ─────────────────── Export ───────────────────

    [RelayCommand]
    public async Task ExportLogsAsync()
    {
        if (PickSaveFileAsync is null || SelectedExportScope is null)
            return;

        var entries = ResolveScope(SelectedExportScope);
        var isJson = string.Equals(SelectedExportFormat, "JSON", StringComparison.OrdinalIgnoreCase);
        var ext = isJson ? "json" : "txt";

        var file = await PickSaveFileAsync(new FilePickerSaveOptions
        {
            Title = LocalizationService.Instance["Logs_ExportTitle"],
            SuggestedFileName = $"RemEx-log-{DateTime.Now:yyyyMMdd-HHmmss}",
            DefaultExtension = ext,
            ShowOverwritePrompt = true,
            FileTypeChoices = new[]
            {
                new FilePickerFileType(isJson ? "JSON" : "Text")
                {
                    Patterns = new[] { $"*.{ext}" },
                },
            },
        });
        if (file is null)
            return;

        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        if (isJson)
            await writer.WriteAsync(SerializeJson(entries));
        else
            await writer.WriteAsync(string.Join(Environment.NewLine, entries.Select(e => e.ToString())));
    }

    private IReadOnlyList<LogEntry> ResolveScope(LogExportScope scope) => scope.Kind switch
    {
        LogExportScopeKind.CurrentView => VisibleEntries.ToList(),
        LogExportScopeKind.Everything => _all.ToList(),
        LogExportScopeKind.LevelFloor => _all.Where(e => e.Level >= scope.MinLevel!.Value).ToList(),
        LogExportScopeKind.Preset => _all.Where(e => scope.Preset!.Matches(e)).ToList(),
        _ => _all.ToList(),
    };

    private static string SerializeJson(IReadOnlyList<LogEntry> entries)
    {
        var payload = entries.Select(e => new LogEntryExport(
            e.TimeStamp.ToString("o", CultureInfo.InvariantCulture),
            e.Level.ToString(),
            e.Category,
            e.Message,
            e.Exception?.ToString())).ToList();
        return JsonSerializer.Serialize(payload, LogExportJsonContext.Default.ListLogEntryExport);
    }

    // ─── System event logs tab: entries RemEx wrote to the OS log, NOT a service. There is no
    // RemEx service; remex.agent runs in the signed-in user's session. Windows reads the
    // Application event log by source; the Linux branch still targets the retired remex-host
    // unit and is tracked as RemEx-2vfx. ────────────────────────────────────────────────────────

    [RelayCommand]
    public async Task FetchServiceLogsAsync()
    {
        ServiceLogsText = "Reading system event log entries written by RemEx...\n";
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var powershellCmd = "Get-EventLog -LogName Application -Source 'Remex.Agent' -Newest 100 -ErrorAction SilentlyContinue | " +
                                    "Select-Object TimeGenerated, EntryType, Message | " +
                                    "ForEach-Object { '[' + $_.TimeGenerated.ToString('yyyy-MM-dd HH:mm:ss') + '] [' + $_.EntryType.ToString().ToUpper() + '] ' + $_.Message }";

                var (_, output) = await RunCommandAsync("powershell.exe", $"-Command \"{powershellCmd}\"");
                ServiceLogsText = string.IsNullOrWhiteSpace(output)
                    ? "No Windows event log entries found for RemEx.\nThis is normal — RemEx only writes here for serious startup problems, so an empty list usually means nothing has gone wrong."
                    : output.Trim();
            }
            else if (OperatingSystem.IsLinux())
            {
                var (ok, output) = await RunCommandAsync("journalctl", "-u remex-host -n 100 --no-pager");
                ServiceLogsText = ok ? output.Trim() : $"Failed to query journalctl: {output}";
            }
            else
            {
                ServiceLogsText = "System event logs are only available on Windows and Linux.";
            }
        }
        catch (Exception ex)
        {
            ServiceLogsText = $"Could not read the system event log: {ex.Message}";
        }
    }

    private static async Task<(bool Success, string Output)> RunCommandAsync(string fileName, string arguments)
    {
        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo.FileName = fileName;
            process.StartInfo.Arguments = arguments;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.CreateNoWindow = true;

            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            await Task.WhenAll(outputTask, errorTask);
            await process.WaitForExitAsync();

            var success = process.ExitCode == 0;
            var output = success ? outputTask.Result : errorTask.Result;

            return (success, output);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public void Dispose() => InMemoryLogSink.LogAdded -= OnLogAdded;
}

/// <summary>A named diagnostic filter over the retained log buffer (subsystem categories + optional level floor).</summary>
public sealed record LogPreset(string Name, LogLevel? MinLevel, string[] Categories)
{
    public bool Matches(LogEntry e)
    {
        if (MinLevel.HasValue && e.Level < MinLevel.Value) return false;
        if (Categories.Length == 0) return true;
        foreach (var category in Categories)
            if (e.Category.Contains(category, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    public override string ToString() => Name;
}

public enum LogExportScopeKind { CurrentView, Everything, LevelFloor, Preset }

/// <summary>A selectable export scope, so a report can differ from what is currently on screen.</summary>
public sealed record LogExportScope(string Name, LogExportScopeKind Kind, LogLevel? MinLevel, LogPreset? Preset)
{
    public override string ToString() => Name;
}

/// <summary>Flat DTO for JSON export (avoids serializing live <see cref="Exception"/> object graphs).</summary>
public sealed record LogEntryExport(string Timestamp, string Level, string Category, string Message, string? Exception);

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<LogEntryExport>))]
internal partial class LogExportJsonContext : JsonSerializerContext;
