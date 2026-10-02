using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remex.Core.Models;
using Remex.Core.Services.FileTransfer;
using Remex.Desktop.Services;

namespace Remex.Desktop.ViewModels;

/// <summary>
/// View-model for the file-sharing consent prompt (plan §2). The serving PC raises this when a paired
/// phone asks to browse every drive. The dialog awaits <see cref="ResultTask"/> and closes with the
/// user's decision; the caller relays that decision to <see cref="IFileTrustService.ResolveConsent"/>.
/// </summary>
/// <remarks>
/// FULL BROWSE IS THE ONLY KIND THE PC ASKS ABOUT (RemEx-bezf). The incoming-push prompt was deleted
/// with RemEx-e11w (a phone pushing to a shared writable folder IS the consent), and its title/message
/// branch here, its two strings and the per-device "auto-accept incoming" toggle in Settings went with
/// it, because a control that does nothing still reads like protection. The phone keeps its own
/// incoming-push consent for files the PC sends to it; that is a different prompt on a different device.
/// </remarks>
public partial class FileConsentDialogViewModel : ObservableObject
{
    private readonly TaskCompletionSource<FileConsentDecision> _tcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes with the user's decision (or a clean deny if the window is dismissed).</summary>
    public Task<FileConsentDecision> ResultTask => _tcs.Task;

    /// <summary>The consent kind as the host sent it — carried so the caller can log/route.</summary>
    public string Kind { get; }

    /// <summary>Human-readable detail supplied by the requester.</summary>
    public string? Detail { get; }

    /// <summary>Localized title (full-device browse is the only prompt the PC raises).</summary>
    public string Title { get; }

    /// <summary>Localized explanatory body.</summary>
    public string Message { get; }

    /// <summary>When true, persist the grant so future requests of this kind auto-accept.</summary>
    [ObservableProperty]
    private bool _remember;

    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);

    public FileConsentDialogViewModel(FileConsentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        Kind = request.Kind;
        Detail = request.Detail;

        Title = LocalizationService.Instance["FileConsent_FullBrowseTitle"];
        Message = LocalizationService.Instance["FileConsent_FullBrowseMessage"];
    }

    [RelayCommand]
    private void Allow() => _tcs.TrySetResult(new FileConsentDecision(Granted: true, Remember: Remember));

    [RelayCommand]
    private void Deny() => _tcs.TrySetResult(new FileConsentDecision(Granted: false, Remember: false));

    /// <summary>Resolves the prompt as a clean deny — used when the window is closed without a choice.</summary>
    public void ResolveAsDeny() => _tcs.TrySetResult(new FileConsentDecision(Granted: false, Remember: false));
}
