using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remex.Core.Models;
using Remex.Core.Services.FileTransfer;
using Remex.Desktop.Services;

namespace Remex.Desktop.ViewModels;

/// <summary>
/// View-model for the file-sharing consent prompt (plan §2). The serving PC raises this when a paired
/// device asks for sensitive access (full-device browse or an incoming file push). Mirrors the
/// <see cref="PairingDialogViewModel"/> pattern: the dialog awaits <see cref="ResultTask"/> and closes
/// with the user's decision; the caller relays that decision to
/// <see cref="IFileTrustService.ResolveConsent"/>.
/// </summary>
public partial class FileConsentDialogViewModel : ObservableObject
{
    private readonly TaskCompletionSource<FileConsentDecision> _tcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes with the user's decision (or a clean deny if the window is dismissed).</summary>
    public Task<FileConsentDecision> ResultTask => _tcs.Task;

    /// <summary>The consent kind ("full_browse" | "incoming_push") — carried so the caller can log/route.</summary>
    public string Kind { get; }

    /// <summary>Human-readable detail supplied by the requester (file names / total size).</summary>
    public string? Detail { get; }

    /// <summary>Localized title, chosen by <see cref="Kind"/>.</summary>
    public string Title { get; }

    /// <summary>Localized explanatory body, chosen by <see cref="Kind"/>.</summary>
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

        var isFullBrowse = string.Equals(request.Kind, FileConsentKinds.FullBrowse, StringComparison.Ordinal);
        Title = isFullBrowse
            ? LocalizationService.Instance["FileConsent_FullBrowseTitle"]
            : LocalizationService.Instance["FileConsent_IncomingPushTitle"];
        Message = isFullBrowse
            ? LocalizationService.Instance["FileConsent_FullBrowseMessage"]
            : LocalizationService.Instance["FileConsent_IncomingPushMessage"];
    }

    [RelayCommand]
    private void Allow() => _tcs.TrySetResult(new FileConsentDecision(Granted: true, Remember: Remember));

    [RelayCommand]
    private void Deny() => _tcs.TrySetResult(new FileConsentDecision(Granted: false, Remember: false));

    /// <summary>Resolves the prompt as a clean deny — used when the window is closed without a choice.</summary>
    public void ResolveAsDeny() => _tcs.TrySetResult(new FileConsentDecision(Granted: false, Remember: false));
}
