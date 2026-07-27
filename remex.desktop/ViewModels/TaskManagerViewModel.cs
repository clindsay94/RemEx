using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remex.Core.Models;
using Remex.Desktop.Services;

namespace Remex.Desktop.ViewModels;

public partial class TaskManagerViewModel : ObservableObject, IDisposable
{
    private readonly ConnectionViewModel _connection;
    private CancellationTokenSource? _pollingCts;

    /// <summary>Exposes the connection state for view bindings.</summary>
    public ConnectionViewModel Connection => _connection;

    private static readonly HashSet<string> ExcludedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "svchost", "System Idle Process", "System", "Registry",
        "smss", "csrss", "wininit", "services", "lsass",
        "fontdrvhost", "dwm", "conhost", "sihost",
        "dasHost", "ctfmon", "dllhost", "WUDFHost",
        "SearchIndexer", "SecurityHealthService", "SgrmBroker",
        "spoolsv", "LsaIso", "Memory Compression"
    };

    [ObservableProperty]
    private ObservableCollection<ProcessInfo> _processes = new();

    [ObservableProperty]
    private ProcessInfo? _selectedProcess;

    [ObservableProperty]
    private string? _killError;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _sortBy = "Name"; // Name, Cpu, Memory

    [ObservableProperty]
    private bool _sortDescending = false;

    private List<ProcessInfo> _lastRawProcesses = new();

    public ICommand RefreshCommand { get; }
    public ICommand KillProcessCommand { get; }
    public ICommand SortCommand { get; }

    public TaskManagerViewModel(ConnectionViewModel connection)
    {
        _connection = connection;
        RefreshCommand = new AsyncRelayCommand(RefreshProcessesAsync);
        KillProcessCommand = new AsyncRelayCommand<ProcessInfo>(KillProcessAsync);
        SortCommand = new RelayCommand<string>(SortByColumn);

        _connection.ProcessListReceived += Connection_ProcessListReceived;
    }

    partial void OnSearchTextChanged(string value)
    {
        UpdateProcessList();
    }

    private void SortByColumn(string? column)
    {
        if (string.IsNullOrEmpty(column)) return;
        if (SortBy == column)
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortBy = column;
            SortDescending = false;
        }
        UpdateProcessList();
    }

    private void Connection_ProcessListReceived(List<ProcessInfo> list)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _lastRawProcesses = list;
            UpdateProcessList();
        });
    }

    private void UpdateProcessList()
    {
        var query = _lastRawProcesses.Where(p => !ExcludedProcesses.Contains(p.Name)).AsEnumerable();

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            query = query.Where(p => p.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                                     p.Id.ToString().Contains(SearchText));
        }

        query = SortBy switch
        {
            "Cpu" => SortDescending ? query.OrderByDescending(p => p.CpuUsage) : query.OrderBy(p => p.CpuUsage),
            "Memory" => SortDescending ? query.OrderByDescending(p => p.MemoryUsage) : query.OrderBy(p => p.MemoryUsage),
            _ => SortDescending ? query.OrderByDescending(p => p.Name) : query.OrderBy(p => p.Name),
        };

        Processes = new ObservableCollection<ProcessInfo>(query);
    }

    private async Task RefreshProcessesAsync()
    {
        await _connection.RequestProcessListAsync();
    }

    /// <summary>
    /// Delegate set by the View to display a confirmation dialog.
    /// Parameters: (title, message, confirmButtonText). Returns true if the user confirmed.
    /// </summary>
    public Func<string, string, string, Task<bool>>? OnConfirmationRequested { get; set; }

    /// <summary>
    /// True when <paramref name="current"/> still contains the very process the user confirmed, so
    /// it is safe to act on <paramref name="confirmed"/>.<see cref="ProcessInfo.Id"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Extracted and <c>internal</c> so the safety decision can be unit-tested directly; a PID-reuse
    /// race is not something to verify by hand.
    /// </para>
    /// <para>
    /// Fails CLOSED - anything short of an exact match reports "not present" and leaves the process
    /// running, because ending the wrong program has no undo. Comparisons are case-SENSITIVE on
    /// purpose: both sides come from the same host enumeration, so their casing is stable and a
    /// case-only difference means something genuinely changed. Ignoring case could only ever turn a
    /// mismatch into a match, which is the unsafe direction, and it would be plainly wrong on Linux,
    /// where two paths differing only in case are two different files.
    /// </para>
    /// <para>
    /// This deliberately does NOT use <see cref="ProcessInfo.StartTimeUnixMs"/>, even though it now
    /// exists (RemEx-on4n). Adding it here would tighten a check that can never be authoritative:
    /// whatever this concludes, the PID can still change hands during the network round trip that
    /// follows. The start-time comparison that actually decides is the host's, immediately before
    /// the kill, and duplicating it here would suggest this check carries a guarantee it cannot.
    /// What this one is for is failing CLOSED on the obvious case without a round trip at all.
    /// </para>
    /// </remarks>
    internal static bool ConfirmedTargetStillPresent(ProcessInfo confirmed, IReadOnlyList<ProcessInfo> current)
    {
        var match = current.FirstOrDefault(
            p => p.Id == confirmed.Id && string.Equals(p.Name, confirmed.Name, StringComparison.Ordinal));
        if (match is null)
        {
            return false;
        }

        // FilePath is empty when the host could not read it (access denied is common for processes
        // worth protecting), so it can only be compared when BOTH sides have one. Demanding equality
        // against an empty value would refuse every kill of a protected process.
        if (confirmed.FilePath.Length > 0
            && match.FilePath.Length > 0
            && !string.Equals(match.FilePath, confirmed.FilePath, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private async Task KillProcessAsync(ProcessInfo? process)
    {
        if (process == null) return;

        // Clear any previous failure before the dialog: cancelling this kill should not leave an
        // unrelated earlier error on screen.
        KillError = null;

        // Ending a process discards whatever that program had not saved, and there is no undo, so
        // confirm first (RemEx-6p1f). Deliberately fails CLOSED: if no dialog is wired the process
        // is left running, which is the safe outcome.
        if (OnConfirmationRequested is null
            || !await OnConfirmationRequested(
                LocalizationService.Instance["Confirm_KillProcess_Title"],
                string.Format(
                    LocalizationService.Instance["Confirm_KillProcess_Format"],
                    process.Name,
                    process.Id),
                LocalizationService.Instance["TaskMgr_KillProcess"]))
        {
            return;
        }

        // The dialog can sit open indefinitely while polling keeps REPLACING _lastRawProcesses
        // underneath, so by the time the user confirms, the captured PID may belong to a completely
        // different program - operating systems reuse PIDs freely, and the confirmation added by
        // RemEx-6p1f is what opened this window (the previous no-prompt behaviour acted at once).
        // Re-verify the target still matches what the dialog actually named before sending anything
        // (RemEx-2s91).
        //
        // There is no concurrent access here to defend against, so do NOT "harden" this with a lock
        // or Interlocked - that would only obscure why it is already safe. Connection_ProcessListReceived
        // writes _lastRawProcesses exclusively via Dispatcher.UIThread.Post, and this continuation also
        // runs on the UI thread: Avalonia never captures a SynchronizationContext (docs/ASYNC_GUIDELINES.md,
        // which is also why ConfigureAwait is banned repo-wide), so the code after the dialog's await
        // resumes on the thread that completed it - the UI thread driving the dialog. Single-threaded
        // access, not a race won by a lucky read. It also assigns a whole new list rather than mutating
        // this one, so even a hypothetical cross-thread reader could not see a half-updated collection.
        if (!ConfirmedTargetStillPresent(process, _lastRawProcesses))
        {
            KillError = string.Format(
                LocalizationService.Instance["TaskManager_KillTargetChanged"],
                process.Name,
                process.Id);
            await RefreshProcessesAsync();
            return;
        }

        var resp = await _connection.KillProcessWithResponseAsync(
            process.Id,
            expectedName: process.Name,
            expectedStartUnixMs: process.StartTimeUnixMs);
        if (!resp.Success)
        {
            // TaskManager_KillFailed existed in no .resx file at all, and LocalizationService's
            // indexer ends in "?? key", so this branch used to render the literal developer string
            // "TaskManager_KillFailed" on screen - in every language, English included. The old call
            // also passed process.Id into a format string that had no placeholder, so even had the
            // key existed the PID would have been dropped. Both are fixed here rather than deferred:
            // it is the same error surface this bead is extending (RemEx-2s91).
            KillError = string.IsNullOrWhiteSpace(resp.Message)
                ? string.Format(
                    LocalizationService.Instance["TaskManager_KillFailed"],
                    process.Name,
                    process.Id)
                : LocalizeKillFailure(resp.Message);
            System.Diagnostics.Debug.WriteLine($"Kill process failed: {KillError}");
        }
        await RefreshProcessesAsync();
    }

    /// <summary>
    /// Turns a host kill failure into a sentence in the user's language.
    /// </summary>
    /// <remarks>
    /// The host authors these in English and cannot do otherwise — it does not know which language
    /// this window is running in, and the phone does not share this resource file. So it sends
    /// <c>"code␟arg␟englishFallback"</c> and each client owns the wording, the same shape remote
    /// desktop already uses (RemEx-728, RemEx-r37a).
    /// <para>
    /// Untagged text is returned verbatim rather than replaced with something generic. That is the
    /// compatibility path — an older host sends plain English, and showing it is strictly better
    /// than discarding a specific reason for a vague one. Unknown codes fall back the same way,
    /// which is why the fallback must remain a complete sentence on the host side.
    /// </para>
    /// </remarks>
    public static string LocalizeKillFailure(string raw)
    {
        var parts = raw.Split(ProcessKillErrorCodes.Delimiter);
        if (parts.Length < 3)
            return raw;

        var code = parts[0];
        var arg = parts[1];
        // Rejoin, so a fallback that happens to contain the delimiter survives intact.
        var fallback = string.Join(ProcessKillErrorCodes.Delimiter, parts.Skip(2));

        return code switch
        {
            ProcessKillErrorCodes.AccessDenied => LocalizationService.Instance["TaskManager_KillErrAccessDenied"],
            ProcessKillErrorCodes.NotRunning => LocalizationService.Instance["TaskManager_KillErrNotRunning"],
            ProcessKillErrorCodes.IdentityMismatch when !string.IsNullOrWhiteSpace(arg) =>
                string.Format(LocalizationService.Instance["TaskManager_KillErrIdentityChangedFormat"], arg),
            ProcessKillErrorCodes.IdentityMismatch => LocalizationService.Instance["TaskManager_KillErrNotRunning"],
            ProcessKillErrorCodes.Failed => LocalizationService.Instance["TaskManager_KillErrFailed"],
            _ => string.IsNullOrWhiteSpace(fallback) ? raw : fallback,
        };
    }

    public void StartPolling()
    {
        _pollingCts?.Cancel();
        _pollingCts = new CancellationTokenSource();
        _ = PollAsync(_pollingCts.Token);
    }

    public void StopPolling()
    {
        _pollingCts?.Cancel();
    }

    private async Task PollAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RefreshProcessesAsync();
                await Task.Delay(2000, ct); // Poll every 2 seconds
            }
            catch (OperationCanceledException) { /* polling was cancelled because the view went away */ }
            catch { await Task.Delay(2000, ct); }
        }
    }

    public void Dispose()
    {
        _connection.ProcessListReceived -= Connection_ProcessListReceived;
        StopPolling();
    }
}
