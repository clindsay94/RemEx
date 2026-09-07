using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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

    /// <summary>
    /// Rows the user has selected in the live list.
    /// </summary>
    /// <remarks>
    /// THE LIST HAS OFFERED MULTI-SELECT SINCE IT SHIPPED AND NOTHING WAS BOUND TO IT
    /// (RemEx-7xhln), so selecting rows was a gesture the app accepted and discarded. Avalonia keeps
    /// this in SELECTION order, which is why <see cref="FormatForClipboard"/> does not read it
    /// directly.
    /// </remarks>
    public ObservableCollection<LogEntry> SelectedEntries { get; } = new();

    /// <summary>
    /// Set by the view to put text on the system clipboard.
    /// </summary>
    /// <remarks>
    /// The seam <c>RemoteViewModel</c> already uses, for the same reason: a clipboard lives on the
    /// view and a view model that reached for one could not be tested without a running Avalonia.
    /// </remarks>
    public Func<string, Task>? CopyToClipboardAsync { get; set; }

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

    /// <summary>
    /// Whether new entries should keep the list scrolled to the newest one. On by default; a manual
    /// scroll away from the end pauses it (see <see cref="OnLogListScrolled"/>) and only
    /// <see cref="JumpToNewest"/> turns it back on — the view never re-enables it on its own, so a
    /// user reading mid-list is not yanked back down by the next line that arrives.
    /// </summary>
    [ObservableProperty] private bool _isFollowingTail = true;

    /// <summary>
    /// Raised so the view scrolls the log list to the newest entry — either a live arrival while
    /// following, or a resumed follow via <see cref="JumpToNewest"/>. A plain event rather than
    /// bound state: "scroll now" is a one-shot action, not something to observe, which is the same
    /// reasoning behind <see cref="CopyToClipboardAsync"/> being a delegate instead of a property.
    /// </summary>
    public event Action? ScrollToEndRequested;

    /// <summary>Set by the view to present a save-file picker (prompts the user to name the export).</summary>
    public Func<FilePickerSaveOptions, Task<IStorageFile?>>? PickSaveFileAsync { get; set; }

    /// <summary>Full local path of the most recent successful export, or <see langword="null"/> if
    /// nothing has been exported this session. Drives the "Open containing folder" affordance —
    /// NotificationService.Notify takes no action callback, so this is the inline route (RemEx-a8du).</summary>
    [ObservableProperty] private string? _lastExportedFilePath;

    /// <summary>The bare file name of <see cref="LastExportedFilePath"/>, for the inline status row —
    /// the same "name only, not the full path" choice the export toast already made.</summary>
    [ObservableProperty] private string? _lastExportedFileName;

    partial void OnLastExportedFileNameChanged(string? value) => OnPropertyChanged(nameof(LastExportedStatusText));

    /// <summary>The inline status row's text. Reuses the export toast's own format string rather than
    /// duplicating "Saved to {0}." as a second resource key.</summary>
    public string? LastExportedStatusText => LastExportedFileName is null
        ? null
        : string.Format(LocalizationService.Instance["Notification_LogsExported_Message"], LastExportedFileName);

    /// <summary>
    /// Launches the OS file manager on a directory. Replaceable so a test can verify routing without
    /// spawning a real process — the same seam <see cref="CopyToClipboardAsync"/> and
    /// <see cref="PickSaveFileAsync"/> use for the same reason. Defaults to the real launcher.
    /// </summary>
    public Action<string> FolderLauncher { get; set; } = LaunchFolder;

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
        // LastExportedStatusText resolves "Notification_LogsExported_Message" when GOT, so a
        // language switch while the export status row is showing would otherwise leave it in
        // whatever language was active at export time until the next export recomputed it
        // (RemEx-6ddx's class of defect — see AboutViewModel.OnLocalizationChanged, the reference
        // implementation this mirrors).
        LocalizationService.Instance.PropertyChanged += OnLocalizationChanged;
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
    {
        // SetCulture raises "Item", "Item[]" and "" in sequence; act once per switch.
        if (!string.IsNullOrEmpty(e.PropertyName)) return;

        OnPropertyChanged(nameof(LastExportedStatusText));
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

    /// <summary>Puts the selected rows on the clipboard, in the order they are displayed.</summary>
    [RelayCommand]
    public async Task CopySelectedAsync()
    {
        if (CopyToClipboardAsync is null) return;

        var text = FormatForClipboard(VisibleEntries, SelectedEntries);
        if (text.Length == 0) return;

        await CopyToClipboardAsync(text);
    }

    /// <summary>
    /// Renders a selection as the text to paste.
    /// </summary>
    /// <remarks>
    /// <para>
    /// **IN DISPLAY ORDER, NOT SELECTION ORDER, AND THAT IS THE ONLY REAL DECISION HERE.** Avalonia
    /// reports <c>SelectedItems</c> in the order the user clicked, so ctrl-clicking three lines
    /// bottom-up would paste an incident backwards. A log read out of sequence is worse than no log:
    /// the reader draws a causal order from it that never happened.
    /// </para>
    /// <para>
    /// NOTHING IS REFORMATTED. <see cref="LogEntry.ToString"/> is what the list already renders, so
    /// what lands on the clipboard is what the user was looking at — including the exception block,
    /// which is the part anyone pasting a log into a bug report actually needs.
    /// </para>
    /// <para>
    /// An empty selection returns empty rather than a blank line, so the command can decline to touch
    /// the clipboard at all instead of silently replacing whatever the user had in it.
    /// </para>
    /// </remarks>
    internal static string FormatForClipboard(
        IEnumerable<LogEntry> displayed, IEnumerable<LogEntry> selected)
    {
        var chosen = new HashSet<LogEntry>(selected);
        if (chosen.Count == 0) return string.Empty;

        return string.Join(Environment.NewLine, displayed.Where(chosen.Contains));
    }

    /// <summary>Puts one row's bare message on the clipboard — no timestamp, level, category, or
    /// exception. For pasting a single line into a chat or a ticket without the surrounding noise.</summary>
    [RelayCommand]
    public async Task CopyEntryMessageAsync(LogEntry? entry)
    {
        if (entry is null || CopyToClipboardAsync is null) return;
        await CopyToClipboardAsync(entry.Message);
    }

    /// <summary>Puts one row on the clipboard exactly as rendered — <see cref="LogEntry.ToString"/>,
    /// which already appends the exception block when the entry carries one. The counterpart to
    /// <see cref="CopyEntryMessageAsync"/> for when the stack trace is the point of pasting it.</summary>
    [RelayCommand]
    public async Task CopyEntryWithStackAsync(LogEntry? entry)
    {
        if (entry is null || CopyToClipboardAsync is null) return;
        await CopyToClipboardAsync(entry.ToString());
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

    private void OnLogAdded(LogEntry entry) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => ProcessIncomingEntry(entry));

    /// <summary>
    /// The synchronous half of a live arrival, factored out of the dispatcher-posted lambda above so
    /// a test can drive it directly — nothing pumps <c>Dispatcher.UIThread</c> in the test assembly
    /// (no Avalonia.Headless reference in the repo), so a test going through
    /// <see cref="OnLogAdded"/> itself would never observe this body running at all.
    /// </summary>
    internal void ProcessIncomingEntry(LogEntry entry)
    {
        _all.Add(entry);
        if (_all.Count > MaxTrackedEntries)
        {
            var removed = _all[0];
            _all.RemoveAt(0);
            VisibleEntries.Remove(removed);
        }

        if (PassesDisplay(entry))
        {
            VisibleEntries.Add(entry);
            // Only an entry that actually lands ON SCREEN is worth scrolling for — one filtered out
            // below the display level must not yank a following view toward a row it will never show.
            if (IsFollowingTail)
                ScrollToEndRequested?.Invoke();
        }

        OnPropertyChanged(nameof(EntryCountText));
    }

    /// <summary>
    /// Called by the view whenever the log list's scroll position changes, reporting whether it is
    /// currently at the end. Moving away from the end pauses following; reaching the end again on
    /// its own does NOT resume it — only <see cref="JumpToNewest"/> does, so scrolling down to catch
    /// up manually is not silently reinterpreted as asking to follow again.
    /// </summary>
    public void OnLogListScrolled(bool isAtEnd)
    {
        if (!isAtEnd)
            IsFollowingTail = false;
    }

    /// <summary>Resumes following and snaps straight to the newest entry — the "Jump to newest" chip.</summary>
    [RelayCommand]
    public void JumpToNewest()
    {
        IsFollowingTail = true;
        ScrollToEndRequested?.Invoke();
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

        // Scoped rather than `await using var` so the writer has actually flushed and closed before
        // the notification below claims the export finished.
        await using (var stream = await file.OpenWriteAsync())
        await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            if (isJson)
                await writer.WriteAsync(SerializeJson(entries));
            else
                await writer.WriteAsync(string.Join(Environment.NewLine, entries.Select(e => e.ToString())));
        }

        // An export is an outcome the user asked for and is waiting on. The save dialog closing is
        // the only feedback it had, and that is indistinguishable from cancelling it (RemEx-5wc2).
        NotificationService.Instance.Notify(
            NotificationImportance.Outcome,
            LocalizationService.Instance["Notification_LogsExported_Title"],
            string.Format(LocalizationService.Instance["Notification_LogsExported_Message"], file.Name));

        // The toast has no action slot to attach "Open containing folder" to (NotificationService.
        // Notify takes a title and a message, nothing else), so the reveal affordance lives here
        // instead, inline under the export controls.
        LastExportedFilePath = file.TryGetLocalPath();
        LastExportedFileName = file.Name;
    }

    /// <summary>Opens the most recent export's containing folder. A no-op with nothing exported yet
    /// or when the path has no directory component, rather than throwing on either.</summary>
    [RelayCommand]
    public void OpenExportFolder()
    {
        if (string.IsNullOrEmpty(LastExportedFilePath)) return;

        var directory = Path.GetDirectoryName(LastExportedFilePath);
        if (string.IsNullOrEmpty(directory)) return;

        try
        {
            FolderLauncher(directory);
        }
        catch (Exception ex)
        {
            // A failed reveal is a missing convenience, not a defect worth crashing over — but
            // swallowing it silently would mean the click looked like it worked. Same lesson as
            // RemEx-43ha: a failure with no trace anywhere is worse than one recorded here, which is
            // exactly the sink the diagnostics export reads.
            InMemoryLogSink.Append(LogLevel.Warning, "Logs", "Could not open the export folder", ex);
        }
    }

    /// <summary>
    /// The real folder launcher: Explorer on Windows, xdg-open on Linux. Opens the DIRECTORY, not a
    /// "reveal and select the file" — RemEx has no select-in-file-manager plumbing yet, and this
    /// bead scoped to opening the folder (RemEx-a8du).
    /// </summary>
    private static void LaunchFolder(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
        }
        else if (OperatingSystem.IsLinux())
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "xdg-open",
                Arguments = $"\"{directory}\"",
                UseShellExecute = false,
            });
        }
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

    // ─── System event logs tab: entries the OS recorded about RemEx, NOT a service. There is no
    // RemEx service; remex.agent runs in the signed-in user's session. Windows reads the
    // Application event log by source; Linux reads the user journal by process name
    // (RemEx-2vfx — the previous query named a systemd unit the installer deletes). ─────────────

    [RelayCommand]
    public async Task FetchServiceLogsAsync()
    {
        ServiceLogsText = LocalizationService.Instance["Logs_Service_Reading"] + "\n";
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var powershellCmd = "Get-EventLog -LogName Application -Source 'Remex.Agent' -Newest 100 -ErrorAction SilentlyContinue | " +
                                    "Select-Object TimeGenerated, EntryType, Message | " +
                                    "ForEach-Object { '[' + $_.TimeGenerated.ToString('yyyy-MM-dd HH:mm:ss') + '] [' + $_.EntryType.ToString().ToUpper() + '] ' + $_.Message }";

                var (_, output) = await RunCommandAsync("powershell.exe", $"-Command \"{powershellCmd}\"");
                ServiceLogsText = string.IsNullOrWhiteSpace(output)
                    ? LocalizationService.Instance["Logs_Service_WindowsEmpty"]
                    : output.Trim();
            }
            else if (OperatingSystem.IsLinux())
            {
                // BY PROCESS NAME, NOT BY UNIT (RemEx-2vfx). The old query was
                // `-u remex-host`, a systemd unit the installer actively REMOVES
                // (agent-install.sh names it LEGACY_SERVICE_UNIT) — so this tab was permanently
                // empty on every current Linux install. RemEx starts from an XDG autostart
                // .desktop as an ordinary user process, so the closest thing to an OS record of
                // it is the USER journal, attributed by _COMM (the kernel's 15-char process
                // name; the binary is Remex.Agent, 11). Whether anything lands there depends on
                // the desktop: environments that run XDG autostart through systemd (GNOME) put
                // the process's stdout/stderr in the user journal, ones that spawn it directly
                // may capture nothing — which is why the empty case explains itself below
                // instead of reading as "no problems".
                var (ok, output) = await RunCommandAsync(
                    "journalctl", LinuxJournalArguments);
                ServiceLogsText = ok
                    ? DescribeLinuxJournal(output)
                    : string.Format(LocalizationService.Instance["Logs_Service_JournalFailed"], output);
            }
            else
            {
                ServiceLogsText = LocalizationService.Instance["Logs_Service_Unsupported"];
            }
        }
        catch (Exception ex)
        {
            ServiceLogsText = string.Format(LocalizationService.Instance["Logs_Service_ReadError"], ex.Message);
        }
    }

    /// <summary>
    /// The user-journal query for entries the OS recorded about this process (RemEx-2vfx).
    /// Internal so a test can pin it: the previous query named a systemd unit the installer
    /// deletes, and nothing could tell that permanently-empty answer from a healthy one.
    /// </summary>
    internal const string LinuxJournalArguments = "--user _COMM=Remex.Agent -n 100 --no-pager";

    /// <summary>
    /// Turns the journal output into what the tab shows. An empty journal is the expected state
    /// on desktops that do not route XDG autostart through systemd, so it explains itself rather
    /// than reading as "nothing has gone wrong" — the in-app Logs tab is the authoritative record
    /// either way, and this text says so.
    /// </summary>
    internal static string DescribeLinuxJournal(string output)
    {
        var trimmed = output.Trim();
        // journalctl prints "-- No entries --" rather than nothing when the query matches nothing.
        // StartsWith on the WHOLE trimmed output, not Contains: a real entry whose message embeds
        // that literal must not suppress a hundred genuine lines (review finding).
        if (trimmed.Length == 0 || trimmed.StartsWith("-- No entries --", StringComparison.Ordinal))
        {
            return string.Format(
                LocalizationService.Instance["Logs_Service_LinuxEmpty"],
                LocalizationService.Instance["Logs_LiveTab"]);
        }

        return trimmed;
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

    public void Dispose()
    {
        InMemoryLogSink.LogAdded -= OnLogAdded;
        LocalizationService.Instance.PropertyChanged -= OnLocalizationChanged;
    }
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
