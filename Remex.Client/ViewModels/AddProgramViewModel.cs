using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Remex.Core.Models;
using Remex.Core.Services;

namespace Remex.Client.ViewModels;

public partial class AddProgramViewModel : ObservableObject
{
    private readonly IIconExtractionService _iconService;

    private const string DefaultHexColor = "#4A3AFF";

    [ObservableProperty]
    private string _targetPath = string.Empty;

    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private string _hexColor = DefaultHexColor;

    [ObservableProperty]
    private Avalonia.Media.Color _validatedColor;

    [ObservableProperty]
    private string? _iconBase64;

    public Action? OnCloseRequested { get; set; }
    public Func<AppEntry, Task>? OnSaveRequested { get; set; }
    public Func<FilePickerOpenOptions, Task<System.Collections.Generic.IReadOnlyList<IStorageFile>>>? PickFileAsync { get; set; }

    public AddProgramViewModel(IIconExtractionService iconService)
    {
        _iconService = iconService;
        UpdateValidatedColor();
    }

    partial void OnHexColorChanged(string value)
    {
        UpdateValidatedColor();
    }

    partial void OnTargetPathChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && File.Exists(value))
        {
            if (string.IsNullOrWhiteSpace(DisplayName))
            {
                DisplayName = Path.GetFileNameWithoutExtension(value);
            }
            IconBase64 = _iconService.ExtractIconAsBase64(value);
        }
    }

    private void UpdateValidatedColor()
    {
        if (Avalonia.Media.Color.TryParse(HexColor, out var color))
        {
            ValidatedColor = color;
        }
        else
        {
            ValidatedColor = Avalonia.Media.Color.Parse(DefaultHexColor); // Default fallback
        }
    }

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (PickFileAsync == null) return;

        var files = await PickFileAsync(new FilePickerOpenOptions
        {
            Title = "Select Application",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Applications")
                {
                    Patterns = OperatingSystem.IsWindows()
                        ? new[] { "*.exe", "*.lnk", "*.bat" }
                        : new[] { "*.sh", "*.desktop", "*" } // Linux/Mac extensions or no extension
                }
            }
        });

        if (files != null && files.Count > 0)
        {
            var selectedFile = files[0];
            var absolutePath = selectedFile.Path.LocalPath;
            TargetPath = absolutePath;

            // Auto-fill Display Name based on file name without extension
            if (string.IsNullOrWhiteSpace(DisplayName))
            {
                DisplayName = Path.GetFileNameWithoutExtension(absolutePath);
            }

            // Extract Icon
            IconBase64 = _iconService.ExtractIconAsBase64(absolutePath);
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(TargetPath) || string.IsNullOrWhiteSpace(DisplayName))
            return;

        var newEntry = new AppEntry(
            Guid.NewGuid(),
            DisplayName,
            TargetPath,
            HexColor,
            IconBase64 ?? string.Empty
        );

        if (OnSaveRequested != null)
        {
            await OnSaveRequested(newEntry);
        }
        OnCloseRequested?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        OnCloseRequested?.Invoke();
    }
}
