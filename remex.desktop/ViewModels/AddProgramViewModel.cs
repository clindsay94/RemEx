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
using Remex.Desktop.Services;
using Remex.Core.Services;

namespace Remex.Desktop.ViewModels;

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

    [ObservableProperty]
    private bool _isEditMode;

    /// <summary>The Id of the entry being edited, or null when adding a brand-new entry.</summary>
    private Guid? _editingId;

    /// <summary>The Order of the entry being edited — preserved across an edit so the card doesn't jump.</summary>
    private int _editingOrder;

    /// <summary>True while <see cref="LoadForEdit"/> is seeding fields, so it doesn't trigger a re-extract.</summary>
    private bool _isSeeding;

    public Action? OnCloseRequested { get; set; }
    public Func<AppEntry, Task>? OnSaveRequested { get; set; }
    public Func<FilePickerOpenOptions, Task<System.Collections.Generic.IReadOnlyList<IStorageFile>>>? PickFileAsync { get; set; }

    public AddProgramViewModel(IIconExtractionService iconService)
    {
        _iconService = iconService;
        UpdateValidatedColor();
    }

    /// <summary>
    /// Seeds this dialog's fields from an existing entry for editing. The entry's Id and Order
    /// are preserved and re-applied verbatim in <see cref="SaveAsync"/> — the wire protocol
    /// requires stable Ids across edits. The icon is preserved as-is (not re-extracted) since the
    /// user didn't change the target path.
    /// </summary>
    public void LoadForEdit(AppEntry entry)
    {
        _isSeeding = true;

        _editingId = entry.Id;
        _editingOrder = entry.Order;
        IsEditMode = true;

        DisplayName = entry.DisplayName;
        TargetPath = entry.TargetPath;
        HexColor = entry.HexColor;
        IconBase64 = entry.IconBase64;

        _isSeeding = false;
    }

    partial void OnHexColorChanged(string value)
    {
        UpdateValidatedColor();
    }

    partial void OnTargetPathChanged(string value)
    {
        if (_isSeeding) return;

        if (!string.IsNullOrWhiteSpace(value) && File.Exists(value))
        {
            if (string.IsNullOrWhiteSpace(DisplayName))
            {
                DisplayName = Path.GetFileNameWithoutExtension(value);
            }
            IconBase64 = _iconService.ExtractIconAsBase64(value);
        }
    }

    /// <summary>Re-extracts the icon from the current <see cref="TargetPath"/> on demand — available in both add and edit mode.</summary>
    [RelayCommand]
    private void RefreshIcon()
    {
        if (!string.IsNullOrWhiteSpace(TargetPath) && File.Exists(TargetPath))
        {
            IconBase64 = _iconService.ExtractIconAsBase64(TargetPath);
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
            Title = LocalizationService.Instance["FilePicker_SelectApplication"],
            AllowMultiple = false,
            FileTypeFilter = OperatingSystem.IsWindows()
                ? new[]
                {
                    new FilePickerFileType(LocalizationService.Instance["FilePicker_Applications"])
                    {
                        Patterns = new[] { "*.exe", "*.lnk", "*.bat" }
                    },
                    FilePickerFileTypes.All
                }
                : new[]
                {
                    new FilePickerFileType(LocalizationService.Instance["FilePicker_Applications"])
                    {
                        // On Linux, using Patterns with '*' (like in FilePickerFileTypes.All) can 
                        // sometimes restrict directory navigation in certain GTK versions. 
                        // Using MimeTypes exclusively is more reliable for applications.
                        MimeTypes = new[] { "application/x-executable", "application/x-desktop" }
                    },
                    new FilePickerFileType(LocalizationService.Instance["FilePicker_AllFiles"] ?? "All Files")
                    {
                        MimeTypes = new[] { "*/*" }
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

        // Id + Order are preserved in edit mode — the wire protocol requires stable Ids.
        var entry = new AppEntry(
            _editingId ?? Guid.NewGuid(),
            DisplayName,
            TargetPath,
            HexColor,
            IconBase64 ?? string.Empty,
            _editingOrder
        );

        if (OnSaveRequested != null)
        {
            await OnSaveRequested(entry);
        }
        OnCloseRequested?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        OnCloseRequested?.Invoke();
    }
}
