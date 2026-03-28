using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remex.Core.Models;

namespace Remex.Client.ViewModels;

public partial class TaskManagerViewModel : ObservableObject, IDisposable
{
    private readonly ConnectionViewModel _connection;
    private CancellationTokenSource? _pollingCts;

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

    private async Task KillProcessAsync(ProcessInfo? process)
    {
        if (process == null) return;
        var resp = await _connection.KillProcessWithResponseAsync(process.Id);
        if (!resp.Success)
        {
            System.Diagnostics.Debug.WriteLine($"Kill process failed: {resp.Message}");
        }
        await RefreshProcessesAsync();
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
            catch (OperationCanceledException) { }
            catch { await Task.Delay(2000, ct); }
        }
    }

    public void Dispose()
    {
        _connection.ProcessListReceived -= Connection_ProcessListReceived;
        StopPolling();
    }
}
