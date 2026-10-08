using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Remex.Core.Helpers;
using Remex.Core.Models;
using Remex.Desktop.Services.FilePreview;
using Remex.Desktop.Services.FileTransfer;

namespace Remex.Desktop.ViewModels;

/// <summary>What the preview pane is showing.</summary>
public enum PreviewState
{
    /// <summary>Nothing selected.</summary>
    Nothing,
    Loading,
    Image,
    Text,
    /// <summary>A folder, or a file with nothing to preview: icon and details only.</summary>
    Details,
    /// <summary>Over the size a preview fetches; "Download" instead.</summary>
    TooLarge,
    /// <summary>A file whose bytes are not text.</summary>
    Binary,
    /// <summary>The device on the other end predates previews; it should be updated.</summary>
    NeedsUpdate,
    Failed,
}

/// <summary>How a pasted hash compared.</summary>
public enum HashCompareOutcome { None, Match, Mismatch, NotAHash }

/// <summary>How a "Verify against…" check ended.</summary>
public enum CounterpartOutcome { None, Identical, Different, Failed }

/// <summary>One line of a text preview, with its colouring. <see cref="Number"/> is null in a live tail, where it is unknown.</summary>
public sealed record PreviewLine(int? Number, string Text, IReadOnlyList<SyntaxSpan> Spans);

/// <summary>The file the preview pane is about: where it is, and the client that reaches it.</summary>
public sealed record PreviewTarget(FileTransferClient Client, FileSourceOption Source, string RootId, string RelativePath, FileEntry Entry)
{
    public bool IsPhone => !Source.IsThisPc;
}

/// <summary>
/// The preview pane of the File Transfer screen (2026-10-08 redesign): follows the selection, shows an image at full
/// size or text with colouring and an optional live tail, the file's details, and its SHA-256 with copy, compare
/// against a pasted hash, and "Verify against…" a copy on the other device.
/// </summary>
public sealed partial class FilePreviewViewModel : ObservableObject, IDisposable
{
    /// <summary>A preview waits this long after the selection stops moving, so arrowing down a list reads nothing.</summary>
    internal static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(150);

    /// <summary>A live tail keeps at most this many lines on screen.</summary>
    internal const int MaxTailLines = 5000;

    private readonly Func<FileSourceOption, CancellationToken, Task<FileTransferClient?>> _clientFor;
    private readonly ILogger _logger;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _tailCts;
    private SyntaxLanguage _language;

    public FilePreviewViewModel(
        Func<FileSourceOption, CancellationToken, Task<FileTransferClient?>> clientFor, ILogger? logger = null)
    {
        _clientFor = clientFor;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Turns image bytes into something the pane can draw. A seam: unit tests run without a renderer.</summary>
    internal Func<byte[], IImage?> DecodeImage { get; set; } = bytes =>
    {
        using var stream = new MemoryStream(bytes);
        return new Bitmap(stream);
    };

    /// <summary>Puts text on the clipboard. Wired by the view, which owns the top level.</summary>
    public Func<string, Task>? CopyText { get; set; }

    /// <summary>
    /// Where each device was last browsed (shared folder, folder), so "Verify against…" can guess the counterpart:
    /// the same file name in that folder. Supplied by the screen.
    /// </summary>
    public Func<FileSourceOption, (string RootId, string Folder)?>? LastFolderOn { get; set; }

    /// <summary>The devices "Verify against…" may use: every connected device except this file's. Supplied by the screen.</summary>
    public Func<IEnumerable<FileSourceOption>>? OtherDevices { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTarget))]
    [NotifyPropertyChangedFor(nameof(IsFile))]
    private PreviewTarget? _target;

    public bool HasTarget => Target is not null;

    public bool IsFile => Target is { Entry.IsDirectory: false };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading))]
    [NotifyPropertyChangedFor(nameof(ShowsImage))]
    [NotifyPropertyChangedFor(nameof(ShowsText))]
    [NotifyPropertyChangedFor(nameof(ShowsIcon))]
    [NotifyPropertyChangedFor(nameof(IsTooLarge))]
    [NotifyPropertyChangedFor(nameof(IsBinary))]
    [NotifyPropertyChangedFor(nameof(NeedsUpdate))]
    [NotifyPropertyChangedFor(nameof(HasFailed))]
    private PreviewState _state;

    public bool IsLoading => State == PreviewState.Loading;
    public bool ShowsImage => State == PreviewState.Image;
    public bool ShowsText => State == PreviewState.Text;
    public bool ShowsIcon => State is PreviewState.Details or PreviewState.TooLarge or PreviewState.Binary
        or PreviewState.NeedsUpdate or PreviewState.Failed;
    public bool IsTooLarge => State == PreviewState.TooLarge;
    public bool IsBinary => State == PreviewState.Binary;
    public bool NeedsUpdate => State == PreviewState.NeedsUpdate;
    public bool HasFailed => State == PreviewState.Failed;

    /// <summary>The full-size image, while <see cref="ShowsImage"/>.</summary>
    [ObservableProperty]
    private IImage? _image;

    /// <summary>A host-made JPEG thumbnail (Base64), shown when the full image cannot be: HEIC, too large, or an older device.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThumbnail))]
    private string? _thumbnailBase64;

    public bool HasThumbnail => !string.IsNullOrEmpty(ThumbnailBase64);

    /// <summary>0..1 while a large image is fetched.</summary>
    [ObservableProperty]
    private double _loadProgress;

    public ObservableCollection<PreviewLine> Lines { get; } = new();

    /// <summary>The line still being written in a live tail (no newline yet).</summary>
    [ObservableProperty]
    private string _pendingLine = string.Empty;

    /// <summary>True when only the first 2 MiB of a text file is shown.</summary>
    [ObservableProperty]
    private bool _isTruncated;

    /// <summary>Whether the Live toggle is offered: text from a device that answers range reads.</summary>
    [ObservableProperty]
    private bool _canTail;

    [ObservableProperty]
    private bool _isLive;

    /// <summary>Size, dates and type, once the host has answered.</summary>
    [ObservableProperty]
    private FileMetadataResponse? _metadata;

    /// <summary>A plain reason the preview could not be shown (the host's own words when it gave some).</summary>
    [ObservableProperty]
    private string? _failureText;

    // ── Integrity ──

    /// <summary>Whether "Compute SHA-256" is offered: a file, on a device that answers hash requests.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ComputeHashCommand))]
    private bool _canComputeHash;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ComputeHashCommand))]
    private bool _isHashing;

    /// <summary>The file's SHA-256 in lowercase hex, once computed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHash))]
    [NotifyCanExecuteChangedFor(nameof(CopyHashCommand))]
    private string? _hashHex;

    public bool HasHash => HashHex is not null;

    [ObservableProperty]
    private string? _hashFailure;

    /// <summary>A hash the person pasted to compare against.</summary>
    [ObservableProperty]
    private string _compareInput = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CompareMatches))]
    [NotifyPropertyChangedFor(nameof(CompareDiffers))]
    [NotifyPropertyChangedFor(nameof(CompareNotAHash))]
    private HashCompareOutcome _compareOutcome;

    public bool CompareMatches => CompareOutcome == HashCompareOutcome.Match;
    public bool CompareDiffers => CompareOutcome == HashCompareOutcome.Mismatch;
    public bool CompareNotAHash => CompareOutcome == HashCompareOutcome.NotAHash;

    partial void OnCompareInputChanged(string value) => UpdateCompare();
    partial void OnHashHexChanged(string? value) => UpdateCompare();

    private void UpdateCompare()
    {
        if (string.IsNullOrWhiteSpace(CompareInput) || HashHex is null)
            CompareOutcome = HashCompareOutcome.None;
        else if (!HashFormat.TryNormalize(CompareInput, out _))
            CompareOutcome = HashCompareOutcome.NotAHash;
        else
            CompareOutcome = HashFormat.Matches(CompareInput, HashHex) ? HashCompareOutcome.Match : HashCompareOutcome.Mismatch;
    }

    // ── Verify against the other device ──

    public ObservableCollection<FileSourceOption> CounterpartDevices { get; } = new();

    [ObservableProperty]
    private FileSourceOption? _counterpartDevice;

    public ObservableCollection<FileSharedRoot> CounterpartRoots { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(VerifyAgainstCommand))]
    private FileSharedRoot? _counterpartRoot;

    /// <summary>The counterpart's path inside <see cref="CounterpartRoot"/>, prefilled with the same name in the folder last browsed there.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(VerifyAgainstCommand))]
    private string _counterpartPath = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(VerifyAgainstCommand))]
    private bool _isVerifyingAgainst;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CounterpartIdentical))]
    [NotifyPropertyChangedFor(nameof(CounterpartDifferent))]
    [NotifyPropertyChangedFor(nameof(CounterpartFailed))]
    private CounterpartOutcome _counterpartOutcome;

    public bool CounterpartIdentical => CounterpartOutcome == CounterpartOutcome.Identical;
    public bool CounterpartDifferent => CounterpartOutcome == CounterpartOutcome.Different;
    public bool CounterpartFailed => CounterpartOutcome == CounterpartOutcome.Failed;

    [ObservableProperty]
    private string? _counterpartFailure;

    public bool HasCounterpartDevices => CounterpartDevices.Count > 0;

    // ── Showing a file ──

    /// <summary>
    /// Shows <paramref name="target"/> after <see cref="Debounce"/>, cancelling whatever was loading or tailing.
    /// Null clears the pane.
    /// </summary>
    public void Show(PreviewTarget? target)
    {
        StopTail();
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;
        _ = ShowAfterDebounceAsync(target, ct);
    }

    private async Task ShowAfterDebounceAsync(PreviewTarget? target, CancellationToken ct)
    {
        try
        {
            await Task.Delay(Debounce, ct);
            await LoadAsync(target, ct);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer selection; that one owns the pane now.
        }
    }

    /// <summary>Loads <paramref name="target"/> now. The debounce-free core of <see cref="Show"/>, for tests.</summary>
    internal async Task LoadAsync(PreviewTarget? target, CancellationToken ct)
    {
        Reset(target);
        if (target is null)
            return;

        var entry = target.Entry;
        _ = LoadMetadataAsync(target, ct);
        PrepareIntegrity(target);

        var kind = PreviewClassifier.Classify(entry.Name, entry.IsDirectory);
        if (kind == PreviewKind.None)
        {
            State = PreviewState.Details;
            return;
        }

        if (kind == PreviewKind.ImageThumbnailOnly)
        {
            await LoadThumbnailAsync(target, ct);
            State = PreviewState.Details;
            return;
        }

        if (!target.Client.SupportsReadRange)
        {
            if (kind == PreviewKind.Image)
                await LoadThumbnailAsync(target, ct);
            State = PreviewState.NeedsUpdate;
            return;
        }

        State = PreviewState.Loading;
        var read = Reader(target);
        try
        {
            if (kind == PreviewKind.Image)
            {
                var progress = new Progress<double>(p => LoadProgress = p);
                var bytes = await ImagePreviewLoader.LoadAsync(read, progress, ct);
                ct.ThrowIfCancellationRequested();
                Image = DecodeImage(bytes);
                State = Image is null ? PreviewState.Failed : PreviewState.Image;
                return;
            }

            var head = await TextPreviewLoader.LoadHeadAsync(read, ct);
            ct.ThrowIfCancellationRequested();
            if (head.IsBinary)
            {
                // A file named as text that is not, and an unknown one that turned out binary, get details only.
                State = kind == PreviewKind.Sniff ? PreviewState.Details : PreviewState.Binary;
                return;
            }

            _language = SyntaxTokenizer.LanguageFor(entry.Name);
            // A file that ends with a newline has no line after it; Split would invent an empty one.
            var text = head.Text.EndsWith('\n') ? head.Text[..^1] : head.Text;
            ShowLines(text.Split('\n'), numbered: true);
            IsTruncated = head.Truncated;
            CanTail = true;
            State = PreviewState.Text;
        }
        catch (PreviewTooLargeException)
        {
            if (kind == PreviewKind.Image)
                await LoadThumbnailAsync(target, ct);
            State = PreviewState.TooLarge;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (FileTransferHostException ex)
        {
            _logger.LogInformation(ex, "Previewing a file failed - the host refused it");
            FailureText = ex.HostMessage;
            State = PreviewState.Failed;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogInformation(ex, "Previewing a file failed");
            State = PreviewState.Failed;
        }
    }

    private void Reset(PreviewTarget? target)
    {
        Target = target;
        State = target is null ? PreviewState.Nothing : PreviewState.Loading;
        Image = null;
        ThumbnailBase64 = null;
        LoadProgress = 0;
        Lines.Clear();
        PendingLine = string.Empty;
        IsTruncated = false;
        CanTail = false;
        IsLive = false;
        Metadata = null;
        FailureText = null;
        HashHex = null;
        HashFailure = null;
        IsHashing = false;
        CompareInput = string.Empty;
        CompareOutcome = HashCompareOutcome.None;
        CounterpartOutcome = CounterpartOutcome.None;
        CounterpartFailure = null;
        CanComputeHash = false;
    }

    private static RangeReader Reader(PreviewTarget target) =>
        (offset, length, fromEnd, ct) => target.Client.ReadRangeRemoteAsync(target.RootId, target.RelativePath, offset, length, fromEnd, ct);

    private async Task LoadMetadataAsync(PreviewTarget target, CancellationToken ct)
    {
        try
        {
            var metadata = await target.Client.GetMetadataRemoteAsync(target.RootId, target.RelativePath, ct);
            if (!ct.IsCancellationRequested && ReferenceEquals(Target, target))
                Metadata = metadata;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Details are a nicety; the listing already showed size and date.
            _logger.LogDebug(ex, "Reading a file's details for the preview failed");
        }
    }

    private async Task LoadThumbnailAsync(PreviewTarget target, CancellationToken ct)
    {
        try
        {
            var jpeg = await target.Client.GetThumbnailRemoteAsync(target.RootId, target.RelativePath, 512, ct);
            if (!ct.IsCancellationRequested && ReferenceEquals(Target, target))
                ThumbnailBase64 = jpeg;
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            _logger.LogDebug(ex, "Fetching a preview thumbnail failed");
        }
    }

    private void ShowLines(IEnumerable<string> lines, bool numbered)
    {
        Lines.Clear();
        var number = 1;
        foreach (var raw in lines)
        {
            var text = raw.TrimEnd('\r');
            Lines.Add(new PreviewLine(numbered ? number : null, text, SyntaxTokenizer.Tokenize(text, _language)));
            number++;
        }
    }

    // ── Live tail ──

    partial void OnIsLiveChanged(bool value)
    {
        if (value && Target is { } target && CanTail)
            StartTail(target);
        else
            StopTail();
    }

    private void StartTail(PreviewTarget target)
    {
        StopTail();
        _tailCts = new CancellationTokenSource();
        _ = RunTailAsync(target, _tailCts.Token);
    }

    private void StopTail()
    {
        _tailCts?.Cancel();
        _tailCts?.Dispose();
        _tailCts = null;
    }

    private async Task RunTailAsync(PreviewTarget target, CancellationToken ct)
    {
        var tail = new TextTail(Reader(target));
        try
        {
            ApplyTail(await tail.StartAsync(ct));
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(FileTransferLimits.PreviewTailPollMilliseconds, ct);
                ApplyTail(await tail.PollAsync(ct));
            }
        }
        catch (OperationCanceledException)
        {
            // Live was turned off, or the selection moved.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogInformation(ex, "Live tail stopped");
            IsLive = false;
        }
    }

    /// <summary>Applies one tail step to the shown lines. Internal for tests.</summary>
    internal void ApplyTail(TailUpdate update)
    {
        if (update.Reset)
        {
            IsTruncated = false;
            ShowLines(update.Lines, numbered: false);
        }
        else
        {
            foreach (var line in update.Lines)
                Lines.Add(new PreviewLine(null, line, SyntaxTokenizer.Tokenize(line, _language)));
        }

        while (Lines.Count > MaxTailLines)
            Lines.RemoveAt(0);
        PendingLine = update.PendingLine;
    }

    // ── Integrity commands ──

    private void PrepareIntegrity(PreviewTarget target)
    {
        CanComputeHash = !target.Entry.IsDirectory && target.Client.SupportsHash;

        CounterpartDevices.Clear();
        foreach (var device in OtherDevices?.Invoke() ?? [])
            CounterpartDevices.Add(device);
        OnPropertyChanged(nameof(HasCounterpartDevices));
        CounterpartDevice = CounterpartDevices.FirstOrDefault();
    }

    [RelayCommand(CanExecute = nameof(CanRunComputeHash))]
    private async Task ComputeHashAsync()
    {
        if (Target is not { } target)
            return;
        IsHashing = true;
        HashFailure = null;
        try
        {
            var base64 = await target.Client.VerifyRemoteHashAsync(
                target.RootId, target.RelativePath, CancellationToken.None,
                target.IsPhone ? FileTransferClient.PhoneHashTimeout : null);
            if (ReferenceEquals(Target, target))
                HashHex = HashFormat.ToHex(base64);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogInformation(ex, "Computing a file's SHA-256 failed");
            if (ReferenceEquals(Target, target))
                HashFailure = ex is FileTransferHostException host
                    ? host.HostMessage
                    : Services.LocalizationService.Instance["FilePreview_HashFailed"];
        }
        finally
        {
            IsHashing = false;
        }
    }

    private bool CanRunComputeHash() => CanComputeHash && !IsHashing;

    [RelayCommand(CanExecute = nameof(HasHash))]
    private async Task CopyHashAsync()
    {
        if (HashHex is { } hex && CopyText is { } copy)
            await copy(hex);
    }

    partial void OnCounterpartDeviceChanged(FileSourceOption? value) => _ = LoadCounterpartRootsAsync(value);

    private async Task LoadCounterpartRootsAsync(FileSourceOption? device)
    {
        CounterpartRoots.Clear();
        CounterpartRoot = null;
        CounterpartPath = string.Empty;
        if (device is null)
            return;
        try
        {
            var client = await _clientFor(device, CancellationToken.None);
            if (client is null || !ReferenceEquals(device, CounterpartDevice))
                return;
            var roots = await client.ListRemoteRootsAsync(CancellationToken.None);
            if (!ReferenceEquals(device, CounterpartDevice))
                return;
            foreach (var root in roots.OrderBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase))
                CounterpartRoots.Add(root);

            // The likely counterpart: the same name, in the folder last opened on that device.
            var last = LastFolderOn?.Invoke(device);
            CounterpartRoot = CounterpartRoots.FirstOrDefault(r => r.RootId == last?.RootId) ?? CounterpartRoots.FirstOrDefault();
            if (Target is { } target)
            {
                var folder = last is { } l && l.RootId == CounterpartRoot?.RootId ? l.Folder : string.Empty;
                CounterpartPath = FileTreeViewModel.Combine(folder, target.Entry.Name);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogInformation(ex, "Listing the other device's shared folders failed");
        }
    }

    [RelayCommand(CanExecute = nameof(CanVerifyAgainst))]
    private async Task VerifyAgainstAsync()
    {
        if (Target is not { } target || CounterpartDevice is not { } device || CounterpartRoot is not { } root)
            return;
        IsVerifyingAgainst = true;
        CounterpartOutcome = CounterpartOutcome.None;
        CounterpartFailure = null;
        try
        {
            var other = await _clientFor(device, CancellationToken.None)
                ?? throw new PhoneNotConnectedException();
            var mine = HashHex is { } known
                ? Task.FromResult(known)
                : HashAsync(target.Client, target.RootId, target.RelativePath, target.IsPhone);
            var theirs = HashAsync(other, root.RootId, FileTreeViewModel.Normalize(CounterpartPath), !device.IsThisPc);
            await Task.WhenAll(mine, theirs);
            if (!ReferenceEquals(Target, target))
                return;
            HashHex ??= mine.Result;
            CounterpartOutcome = HashFormat.Matches(mine.Result, theirs.Result)
                ? CounterpartOutcome.Identical
                : CounterpartOutcome.Different;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogInformation(ex, "Verifying a file against its copy on another device failed");
            CounterpartFailure = ex is FileTransferHostException host ? host.HostMessage : null;
            CounterpartOutcome = CounterpartOutcome.Failed;
        }
        finally
        {
            IsVerifyingAgainst = false;
        }
    }

    private bool CanVerifyAgainst() =>
        !IsVerifyingAgainst && CounterpartRoot is not null && !string.IsNullOrWhiteSpace(CounterpartPath) && IsFile;

    private static async Task<string> HashAsync(FileTransferClient client, string rootId, string path, bool isPhone)
    {
        var base64 = await client.VerifyRemoteHashAsync(rootId, path, CancellationToken.None,
            isPhone ? FileTransferClient.PhoneHashTimeout : null);
        return HashFormat.ToHex(base64) ?? throw new IOException("The device did not return a SHA-256.");
    }

    public void Dispose()
    {
        StopTail();
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
    }
}
