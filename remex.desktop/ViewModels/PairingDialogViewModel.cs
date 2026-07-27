using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remex.Desktop.Services;

namespace Remex.Desktop.ViewModels;

public partial class PairingDialogViewModel : ObservableObject
{
    [ObservableProperty]
    private string _pinInput = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorText;

    private readonly TaskCompletionSource<string?> _tcs = new();

    public Task<string?> ResultTask => _tcs.Task;

    [RelayCommand]
    private void Submit()
    {
        if (string.IsNullOrWhiteSpace(PinInput) || PinInput.Length != 6)
        {
            ErrorText = LocalizationService.Instance["Pairing_InvalidPin"] ?? "PIN must be 6 digits.";
            return;
        }

        ErrorText = null;
        IsBusy = true;
        _tcs.TrySetResult(PinInput);
    }

    [RelayCommand]
    private void Cancel()
    {
        _tcs.TrySetResult(null);
    }
}
