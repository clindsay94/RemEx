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
        var query = _lastRawProcesses.AsEnumerable();

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
            var elevate = "Yes";
            if (elevate == "Yes")
            {
                var elevResp = await _connection.KillProcessWithResponseAsync(process.Id, true);
                if (!elevResp.Success)
                {
                    System.Diagnostics.Debug.WriteLine($"Elevated kill failed: {elevResp.Message}");
                }
            }
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
        if (_pollingCts != null)
        {
            _pollingCts.Cancel();
            _pollingCts.Dispose();
            _pollingCts = null;
        }
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
            catch
            {
                // Use a non-cancelable delay here to avoid TaskCanceledException
                // escaping from within the catch block when stopping polling.
                await Task.Delay(2000);
            }
        }
    }

    public void Dispose()
    {
        _connection.ProcessListReceived -= Connection_ProcessListReceived;
        StopPolling();
    }
}
