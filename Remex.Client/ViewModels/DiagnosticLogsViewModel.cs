using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Remex.Core.Logging;

namespace Remex.Client.ViewModels;

public partial class DiagnosticLogsViewModel : ObservableObject, IDisposable
{
    private readonly ShellViewModel _shell;

    [ObservableProperty]
    private string _logText = string.Empty;

    [ObservableProperty]
    private string _serviceLogsText = string.Empty;

    [ObservableProperty]
    private string _selectedVerbosity = "Information";

    public ObservableCollection<string> VerbosityLevels { get; } = new()
    {
        "Trace",
        "Debug",
        "Information",
        "Warning",
        "Error"
    };

    public DiagnosticLogsViewModel(ShellViewModel shell)
    {
        _shell = shell;
        
        // Match current sink filter
        SelectedVerbosity = InMemoryLogSink.MinimumLogLevel.ToString();

        // Load initial logs
        RefreshLogs();

        // Subscribe to live log events
        InMemoryLogSink.LogAdded += OnLogAdded;
    }

    [RelayCommand]
    public void RefreshLogs()
    {
        var entries = InMemoryLogSink.GetEntries();
        var lines = entries.Select(e => e.ToString());
        LogText = string.Join(Environment.NewLine, lines);
    }

    [RelayCommand]
    public void ClearLogs()
    {
        InMemoryLogSink.Clear();
        LogText = string.Empty;
    }

    partial void OnSelectedVerbosityChanged(string value)
    {
        if (Enum.TryParse<LogLevel>(value, out var level))
        {
            InMemoryLogSink.MinimumLogLevel = level;
            RefreshLogs();
        }
    }

    [RelayCommand]
    public async Task FetchServiceLogsAsync()
    {
        ServiceLogsText = "Querying host background service logs...\n";
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Queries Event Viewer for 'Remex.Host' source and builds a clean list
                var powershellCmd = "Get-EventLog -LogName Application -Source 'Remex.Host' -Newest 100 -ErrorAction SilentlyContinue | " +
                                    "Select-Object TimeGenerated, EntryType, Message | " +
                                    "ForEach-Object { '[' + $_.TimeGenerated.ToString('yyyy-MM-dd HH:mm:ss') + '] [' + $_.EntryType.ToString().ToUpper() + '] ' + $_.Message }";

                var (ok, output) = await RunCommandAsync("powershell.exe", $"-Command \"{powershellCmd}\"");
                if (string.IsNullOrWhiteSpace(output))
                {
                    ServiceLogsText = "No background service event logs found for 'Remex.Host' source.\nEnsure the service is installed and has run previously.";
                }
                else
                {
                    ServiceLogsText = output.Trim();
                }
            }
            else if (OperatingSystem.IsLinux())
            {
                var (ok, output) = await RunCommandAsync("journalctl", "-u remex-host -n 100 --no-pager");
                ServiceLogsText = ok ? output.Trim() : $"Failed to query journalctl: {output}";
            }
            else
            {
                ServiceLogsText = "Background service logs are only supported on Windows and Linux.";
            }
        }
        catch (Exception ex)
        {
            ServiceLogsText = $"Failed to fetch service logs: {ex.Message}";
        }
    }

    private void OnLogAdded(LogEntry entry)
    {
        if (entry.Level >= InMemoryLogSink.MinimumLogLevel)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                LogText = string.IsNullOrEmpty(LogText) 
                    ? entry.ToString() 
                    : $"{LogText}{Environment.NewLine}{entry}";
            });
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

    public void Dispose()
    {
        InMemoryLogSink.LogAdded -= OnLogAdded;
    }
}
