using Remex.Desktop.Services.FileTransfer;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remex.Core.Models;
using Remex.Desktop.Services;

namespace Remex.Desktop.ViewModels;

/// <summary>What a queued transfer represents, used to pick a localized label/icon.</summary>
public enum FileTransferQueueKind
{
    Upload,
    Download,
    SendToPhone,
}

/// <summary>
/// A single entry in the local transfer queue (plan §1.4). Surfaced in the transfer-queue panel with its
/// live state and progress. The PC UI drives the actual bytes over the existing (v2-compatible)
/// upload/download path; this item is the local, per-transfer view of that work.
/// </summary>
/// <summary>
/// How far a transfer has got, in BYTES rather than as a ratio (RemEx-oiah).
/// </summary>
/// <param name="BytesTransferred">Bytes moved so far.</param>
/// <param name="TotalBytes">Total size, or null when the source has no length.</param>
/// <remarks>
/// <para>
/// **THE RATIO USED TO BE COMPUTED BY EACH PRODUCER AND THE BYTES THROWN AWAY AT THE BOUNDARY.**
/// <c>FileTransferClient</c> holds <c>BytesTransferred</c> and <c>TotalBytes</c> and reported
/// <c>bytes / total</c>, so a percentage was the only thing that ever crossed. That is lossy in a way
/// no arithmetic downstream can undo: speed is bytes over time and time-remaining is remaining-bytes
/// over speed, and neither can be recovered from a fraction.
/// </para>
/// <para>
/// It is also a correctness problem on its own, independent of any display: with every producer
/// computing its own ratio, a producer that computed it differently — off-by-one, clamped, or
/// against the wrong total — would be invisible. Carrying the raw counts makes <see cref="Fraction"/>
/// the single place the conversion happens.
/// </para>
/// </remarks>
public readonly record struct TransferProgress(long BytesTransferred, long? TotalBytes)
{
    /// <summary>
    /// Progress as 0..1, or 0 when the total is unknown.
    /// </summary>
    /// <remarks>
    /// ZERO RATHER THAN A GUESS when the size is unknown — a streamed source has no length, and
    /// inventing a fraction would drive a progress bar that means nothing. The caller shows an
    /// indeterminate state instead, which is what "we do not know how far along this is" looks like.
    /// </remarks>
    public double Fraction =>
        TotalBytes is > 0 ? Math.Clamp((double)BytesTransferred / TotalBytes.Value, 0.0, 1.0) : 0.0;
}

public sealed partial class FileTransferQueueItem : ObservableObject
{
    public string Id { get; } = Guid.NewGuid().ToString("N");

    public FileTransferQueueKind Kind { get; }

    public string FileName { get; }

    internal Func<IProgress<TransferProgress>, CancellationToken, Task> Work { get; }

    internal CancellationTokenSource Cts { get; } = new();

    /// <summary>Completes when the item reaches a terminal state — used by callers/tests to await the result.</summary>
    internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Where "now" comes from for the rate estimator (RemEx-4lcq). A MONOTONIC millisecond reading,
    /// never <see cref="DateTime"/> — a wall clock can step backwards or jump on a time sync, which
    /// <see cref="TransferRateEstimator.Update"/> would read as a negative or infinite interval.
    /// <see cref="Environment.TickCount64"/> is the production default; tests inject a counter they
    /// control so a "stall" can be simulated by simply not advancing it.
    /// </summary>
    private readonly Func<long> _nowMillis;

    private readonly TransferRateEstimator _rateEstimator = new();

    private TransferProgress _lastProgress;

    // The last CLASSIFIED figures, not just their formatted text — kept so a language switch can
    // re-render "12.3 MB/s · 3 minutes left" in the new language without waiting for the next
    // progress tick. Cleared alongside RateText/EtaText on a terminal transition, so a finished row
    // does not spuriously reacquire text on the next language switch.
    private TransferRate _lastRate = new TransferRate.Unknown();
    private TransferEta _lastEta = new TransferEta.Unknown();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    [NotifyPropertyChangedFor(nameof(IsTerminal))]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    [NotifyPropertyChangedFor(nameof(StateLabel))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private TransferState _state = TransferState.Queued;

    /// <summary>Percentage 0–100.</summary>
    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>Localized throughput ("12.3 MB/s"), or null while unknown (RemEx-4lcq).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RateEtaText))]
    private string? _rateText;

    /// <summary>Localized time remaining ("3 minutes left" / "Finishing…"), or null while unknown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RateEtaText))]
    private string? _etaText;

    /// <summary>"<see cref="RateText"/> · <see cref="EtaText"/>", either half alone, or null when both are.</summary>
    public string? RateEtaText => RateText is null
        ? null
        : EtaText is null
            ? RateText
            : string.Format(LocalizationService.Instance["FileTransfer_RateEtaFormat"], RateText, EtaText);

    public FileTransferQueueItem(FileTransferQueueKind kind, string fileName, Func<IProgress<TransferProgress>, CancellationToken, Task> work, Func<long>? nowMillis = null)
    {
        Kind = kind;
        FileName = fileName;
        Work = work;
        _nowMillis = nowMillis ?? (() => Environment.TickCount64);
    }

    public bool IsActive => State is TransferState.Negotiating or TransferState.Active or TransferState.Verifying;

    public bool IsTerminal => State is TransferState.Done or TransferState.Failed or TransferState.Cancelled;

    public bool CanCancel => !IsTerminal;

    /// <summary>
    /// Feeds a progress observation and refreshes <see cref="Progress"/>, <see cref="RateText"/> and
    /// <see cref="EtaText"/> together, so the three never disagree about how far along the transfer
    /// is (RemEx-4lcq).
    /// </summary>
    internal void ApplyProgress(TransferProgress progress)
    {
        Progress = Math.Clamp(progress.Fraction * 100.0, 0.0, 100.0);
        _lastProgress = progress;

        var now = _nowMillis();
        _rateEstimator.Update(progress.BytesTransferred, now);
        RefreshRateAndEta(now);
    }

    /// <summary>
    /// Recomputes <see cref="RateText"/>/<see cref="EtaText"/> as of <paramref name="nowMillis"/>
    /// WITHOUT feeding the estimator a new observation.
    /// </summary>
    /// <remarks>
    /// AGED AT READ TIME, NOT CACHED FROM THE LAST UPDATE. This calls
    /// <see cref="TransferRateEstimator.BytesPerSecondAt"/> and
    /// <see cref="TransferRateEstimator.SecondsRemainingAt"/> with the "now" it is given, so a stall
    /// goes stale exactly when the estimator says it should — four time constants after the last real
    /// observation — rather than whenever this happens to be called next. <see cref="ApplyProgress"/>
    /// calls this every tick, which is what re-evaluates staleness in the running app; exposed
    /// separately (internal) so a test can prove the aging without needing a new observation to
    /// trigger it.
    /// </remarks>
    internal void RefreshRateAndEta(long nowMillis)
    {
        _lastRate = TransferProgressFormat.Rate(_rateEstimator.BytesPerSecondAt(nowMillis));
        _lastEta = TransferProgressFormat.Eta(
            _rateEstimator.SecondsRemainingAt(_lastProgress.BytesTransferred, _lastProgress.TotalBytes, nowMillis));

        ApplyLocalizedRateAndEta();
    }

    /// <summary>
    /// <see cref="RefreshRateAndEta"/> against THIS item's own clock. What the owning
    /// <see cref="FileTransferQueue"/>'s periodic timer calls on every active item, so a stall goes
    /// stale even though nothing fed a new observation - see that timer's remarks for why one is
    /// needed at all (RemEx-4lcq review fix).
    /// </summary>
    internal void RefreshRateAndEtaNow() => RefreshRateAndEta(_nowMillis());

    /// <summary>(Re)renders <see cref="RateText"/>/<see cref="EtaText"/> from the last CLASSIFIED figures.</summary>
    private void ApplyLocalizedRateAndEta()
    {
        RateText = TransferProgressText.RateText(_lastRate);
        EtaText = TransferProgressText.EtaText(_lastEta);
    }

    /// <summary>
    /// Resets the rate estimate on (re)start and clears the displayed text on completion — the same
    /// two moments <c>FileTransferEngine</c> resets its estimator on Android (RemEx-4lcq).
    /// </summary>
    /// <remarks>
    /// Reached through <see cref="State"/>'s own setter, so it also covers a resume from
    /// <see cref="TransferState.Paused"/> even though nothing on the PC drives that state today: a
    /// resumed transfer restarting the average avoids treating the paused gap as elapsed time, which
    /// would otherwise compute a rate near zero from a stale byte count against a large time delta.
    /// </remarks>
    partial void OnStateChanged(TransferState value)
    {
        if (value == TransferState.Active)
        {
            _rateEstimator.ResetRate();
        }
        else if (IsTerminal)
        {
            _lastRate = new TransferRate.Unknown();
            _lastEta = new TransferEta.Unknown();
            RateText = null;
            EtaText = null;
        }
    }

    /// <summary>
    /// Re-raises the two labels that read <see cref="LocalizationService"/> at get-time. Called by the
    /// owning <see cref="FileTransferQueue"/> on a language switch - the item itself deliberately does
    /// not subscribe, because the service is a process-lifetime singleton and one subscription per
    /// transfer would pin every completed item alive.
    /// </summary>
    internal void RaiseLocalizedLabels()
    {
        OnPropertyChanged(nameof(ModeLabel));
        OnPropertyChanged(nameof(StateLabel));

        // RateText/EtaText are SNAPSHOTTED text (RemEx-4lcq), not properties that read the localizer
        // at get-time — the same shape as AboutViewModel.UpdateHostVersion and
        // SettingsViewModel.UpdateHostCapabilitySummary. Without this, an active transfer's rate and
        // ETA keep the previous language until the next progress tick happens to arrive.
        ApplyLocalizedRateAndEta();

        // RateText/EtaText's own setters already cascade to this via [NotifyPropertyChangedFor], but
        // ONLY when the reformatted text actually differs from what was already there - which it
        // will not on a round-trip back to the same language, or on an item that has neither yet.
        // Raised explicitly so the joiner's word order is never left depending on that coincidence.
        OnPropertyChanged(nameof(RateEtaText));
    }

    /// <summary>Localized one-word description of the transfer direction.</summary>
    public string ModeLabel => Kind switch
    {
        FileTransferQueueKind.Upload => LocalizationService.Instance["FileTransfer_QueueKindUpload"],
        FileTransferQueueKind.Download => LocalizationService.Instance["FileTransfer_QueueKindDownload"],
        FileTransferQueueKind.SendToPhone => LocalizationService.Instance["FileTransfer_QueueKindSend"],
        _ => string.Empty,
    };

    /// <summary>Localized current state (Queued / Transferring / Done / …).</summary>
    public string StateLabel => State switch
    {
        TransferState.Queued => LocalizationService.Instance["FileTransfer_QueueStateQueued"],
        TransferState.Negotiating => LocalizationService.Instance["FileTransfer_QueueStateNegotiating"],
        TransferState.Active => LocalizationService.Instance["FileTransfer_QueueStateActive"],
        TransferState.Paused => LocalizationService.Instance["FileTransfer_QueueStatePaused"],
        TransferState.Verifying => LocalizationService.Instance["FileTransfer_QueueStateVerifying"],
        TransferState.Done => LocalizationService.Instance["FileTransfer_QueueStateDone"],
        TransferState.Failed => LocalizationService.Instance["FileTransfer_QueueStateFailed"],
        TransferState.Cancelled => LocalizationService.Instance["FileTransfer_QueueStateCancelled"],
        _ => string.Empty,
    };

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        try { Cts.Cancel(); }
        catch (ObjectDisposedException) { /* already finished */ }

        // A QUEUED item is terminal the moment it is cancelled, and saying so here is not cosmetic.
        // The queue runs ONE transfer at a time, so an item at position 500 will not be dequeued for
        // a long while - and until the pump reaches it, RunItemAsync is the only thing that writes
        // Cancelled. Before this the row kept both its "Queued" label and its X, so cancelling it
        // looked exactly like the click had missed. Connor found that out by clicking 900 times
        // (RemEx-p5lu2).
        //
        // ONLY from Queued. Anything already running has to reach its terminal state through
        // RunItemAsync, which is what unwinds the wire, deletes the partial file and completes the
        // TaskCompletionSource; short-circuiting it here would mark the row Cancelled while the
        // transfer was still writing to disk. The pump re-asserts Cancelled when it dequeues this
        // item, which is idempotent.
        if (State == TransferState.Queued)
            State = TransferState.Cancelled;
    }
}

/// <summary>
/// Local, in-process transfer queue (plan §1.4): FIFO, one active transfer at a time. Persistence to
/// <c>transfer_queue.json</c> and the binary <c>/ws/files</c> channel are the host-side responsibility
/// (WP4); this queue drives the PC UI's transfers over the existing path and gives the UI a live,
/// cancellable view. UI mutations are marshalled through <see cref="_post"/> so it is safe from any thread
/// and drivable synchronously in tests.
/// </summary>
public sealed class FileTransferQueue : IDisposable
{
    private readonly Action<Action> _post;
    private readonly ILogger<FileTransferQueue> _logger;
    private readonly ConcurrentQueue<FileTransferQueueItem> _pending = new();
    private readonly object _pumpLock = new();
    private bool _pumping;

    /// <summary>
    /// Drives both the per-item clock (RemEx-4lcq) AND the periodic re-evaluation timer below. One
    /// clock rather than two: a test that advances a fake clock has to move both the estimator's
    /// notion of "now" and the timer's schedule together, and a review of the first cut of this bead
    /// found that a timer-less design left an ACTIVE, STALLED transfer showing its last figure
    /// forever — nothing calls <see cref="FileTransferQueueItem.RefreshRateAndEta"/> again once
    /// progress stops arriving, which is the exact failure this bead exists to fix.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Re-evaluates every ACTIVE item's <see cref="FileTransferQueueItem.RateText"/>/<c>EtaText</c>
    /// once a second so a stall goes stale even though nothing fed it a new observation. Started only
    /// while at least one item is active and stopped otherwise, by <see cref="UpdateRefreshTimerRunning"/>.
    /// </summary>
    private readonly ITimer _refreshTimer;

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

    private bool _refreshTimerRunning;

    /// <summary>Live, ordered view of every transfer (active, queued, and completed).</summary>
    public ObservableCollection<FileTransferQueueItem> Items { get; } = new();

    /// <summary>Raised whenever an item is added or an item's state changes (for aggregate UI recomputation).</summary>
    public event Action? Changed;

    /// <summary>Raised on the UI thread once an item completes successfully (reaches <see cref="TransferState.Done"/>).
    /// Feeds the Home "Recent activity" panel; failed/cancelled items are intentionally not surfaced.</summary>
    public event Action<FileTransferQueueItem>? ItemCompleted;

    public FileTransferQueue()
        : this(null)
    {
    }

    /// <param name="post">UI-thread marshaller. Defaults to <see cref="Dispatcher.UIThread"/>; tests pass a synchronous invoker.</param>
    /// <param name="logger">
    /// Where a failure's real detail goes. Optional and null-defaulted to match the other view models,
    /// so existing construction sites and tests are unaffected.
    /// </param>
    /// <param name="timeProvider">
    /// Defaults to <see cref="TimeProvider.System"/>. Tests pass a fake (e.g. the shared
    /// <c>ManualTimeProvider</c>) so both the estimator's clock and the periodic refresh tick are
    /// driven by the same controllable "now" — see the field's remarks.
    /// </param>
    public FileTransferQueue(Action<Action>? post, ILogger<FileTransferQueue>? logger = null, TimeProvider? timeProvider = null)
    {
        _post = post ?? (action => Dispatcher.UIThread.Post(action));
        _logger = logger ?? NullLogger<FileTransferQueue>.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _refreshTimer = _timeProvider.CreateTimer(OnRefreshTick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        Changed += UpdateRefreshTimerRunning;
        LocalizationService.Instance.PropertyChanged += OnLocaleChanged;
    }

    /// <summary>
    /// A monotonic millisecond reading derived from <see cref="_timeProvider"/>. <c>GetTimestamp()</c>
    /// is the monotonic member of <see cref="TimeProvider"/> (the <c>Stopwatch</c> analogue) —
    /// <c>GetUtcNow()</c> is wall-clock time and can step backwards on a sync, which
    /// <see cref="TransferRateEstimator.Update"/> would read as a negative interval.
    /// </summary>
    private long NowMillis() => (long)((double)_timeProvider.GetTimestamp() * 1000.0 / _timeProvider.TimestampFrequency);

    /// <summary>Re-renders every active item's rate/ETA text as of "now" — the periodic tick's callback.</summary>
    private void OnRefreshTick(object? state) => _post(() =>
    {
        foreach (var item in Items)
        {
            if (item.IsActive)
                item.RefreshRateAndEtaNow();
        }
    });

    /// <summary>Starts the refresh timer the moment an item becomes active, stops it once none are.</summary>
    /// <remarks>
    /// Runs off <see cref="Changed"/> rather than a dedicated hook because every place that can make
    /// an item active or inactive already raises it: <see cref="Enqueue"/>, the pump's state
    /// transitions, <see cref="CancelAll"/>, <see cref="ClearCompleted"/>. A timer running while the
    /// queue is idle would tick forever for nothing to refresh; leaving it running across every item
    /// completing would leak the process's last reference to this queue alive via the timer callback.
    /// </remarks>
    private void UpdateRefreshTimerRunning()
    {
        var shouldRun = Items.Any(item => item.IsActive);
        if (shouldRun == _refreshTimerRunning)
            return;

        _refreshTimerRunning = shouldRun;
        var period = shouldRun ? RefreshInterval : Timeout.InfiniteTimeSpan;
        _refreshTimer.Change(period, period);
    }

    /// <summary>
    /// A language switch changes what <see cref="FileTransferQueueItem.ModeLabel"/> and
    /// <see cref="FileTransferQueueItem.StateLabel"/> would return, but nothing on the item itself
    /// changed, so no notification fires and the queue keeps showing the old language until the row's
    /// state happens to change. Fan the change out to every item instead.
    /// </summary>
    private void OnLocaleChanged(object? sender, PropertyChangedEventArgs e) =>
        _post(() =>
        {
            foreach (var item in Items)
                item.RaiseLocalizedLabels();
        });

    /// <summary>
    /// Detaches from the <see cref="LocalizationService"/> singleton and stops the refresh timer. The
    /// localizer subscription is not optional: the service outlives every view, so a queue that
    /// subscribes without detaching is pinned for the process lifetime along with every item it
    /// holds. The timer is the same story - a running <see cref="ITimer"/> holds a reference to its
    /// callback, which closes over this queue.
    /// </summary>
    public void Dispose()
    {
        LocalizationService.Instance.PropertyChanged -= OnLocaleChanged;
        Changed -= UpdateRefreshTimerRunning;
        _refreshTimer.Dispose();
    }

    /// <summary>Adds a transfer to the tail of the queue and starts the pump if idle. Returns the new item.</summary>
    public FileTransferQueueItem Enqueue(FileTransferQueueKind kind, string fileName, Func<IProgress<TransferProgress>, CancellationToken, Task> work)
    {
        // The item's clock is THIS queue's, not its own default - see _timeProvider's remarks. Every
        // item a queue produces shares one controllable "now" with that queue's refresh timer.
        var item = new FileTransferQueueItem(kind, fileName, work, NowMillis);
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FileTransferQueueItem.State))
                _post(() => Changed?.Invoke());
        };
        _pending.Enqueue(item);
        _post(() =>
        {
            Items.Add(item);
            Changed?.Invoke();
        });
        StartPumpIfNeeded();
        return item;
    }

    /// <summary>
    /// Cancels every item that has not reached a terminal state: the one transfer in flight and
    /// everything still queued behind it. Returns nothing - the states are the result.
    /// </summary>
    /// <remarks>
    /// THE PER-ROW X DOES NOT SCALE, AND A FOLDER TRANSFER IS WHERE THAT STOPS BEING A DETAIL.
    /// One folder enqueues one item per file, so abandoning a folder used to cost one click per file
    /// - 900 of them, in the case that produced this (RemEx-l1ddp). "Clear finished" cannot stand in
    /// for it: it removes terminal items, which is by definition none of the ones you want to stop.
    /// <para>
    /// Iterated over a SNAPSHOT because <see cref="FileTransferQueueItem.Cancel"/> now moves a queued
    /// item straight to Cancelled, and a handler reacting to that could otherwise mutate
    /// <see cref="Items"/> underneath the loop.
    /// </para>
    /// </remarks>
    public void CancelAll()
    {
        _post(() =>
        {
            foreach (var item in Items.ToArray())
            {
                if (!item.IsTerminal)
                    item.CancelCommand.Execute(null);
            }
            Changed?.Invoke();
        });
    }

    /// <summary>Removes every item that has reached a terminal state (Done/Failed/Cancelled).</summary>
    public void ClearCompleted()
    {
        _post(() =>
        {
            for (var i = Items.Count - 1; i >= 0; i--)
            {
                if (Items[i].IsTerminal)
                    Items.RemoveAt(i);
            }
            Changed?.Invoke();
        });
    }

    private void StartPumpIfNeeded()
    {
        lock (_pumpLock)
        {
            if (_pumping)
                return;
            _pumping = true;
        }
        _ = Task.Run(PumpLoopAsync);
    }

    private async Task PumpLoopAsync()
    {
        while (true)
        {
            if (!_pending.TryDequeue(out var item))
            {
                lock (_pumpLock)
                {
                    // Re-check under the lock to avoid a lost wakeup: an item enqueued between the failed
                    // dequeue and here would otherwise strand the queue with _pumping cleared.
                    if (_pending.IsEmpty)
                    {
                        _pumping = false;
                        return;
                    }
                    continue;
                }
            }

            await RunItemAsync(item);
        }
    }

    private async Task RunItemAsync(FileTransferQueueItem item)
    {
        // Cancelled while still queued → never touch the wire.
        if (item.Cts.IsCancellationRequested)
        {
            SetState(item, TransferState.Cancelled);
            item.Completion.TrySetResult();
            item.Cts.Dispose();
            return;
        }

        SetState(item, TransferState.Active);
        var progress = new Progress<TransferProgress>(p => _post(() => item.ApplyProgress(p)));

        try
        {
            await item.Work(progress, item.Cts.Token);
            _post(() => item.Progress = 100.0);
            SetState(item, TransferState.Done);
            item.Completion.TrySetResult();
            _post(() => ItemCompleted?.Invoke(item));
        }
        catch (OperationCanceledException)
        {
            SetState(item, TransferState.Cancelled);
            item.Completion.TrySetResult();
        }
        catch (Exception ex)
        {
            // Logged as well as shown. The message on screen is deliberately plain and short — it has
            // to be usable by someone who does not know what an IOException is — so the exception is the
            // only place the actual cause survives. Before this it survived nowhere (RemEx-6tvh).
            _logger.LogWarning(ex, "File transfer {Name} failed.", item.FileName);
            _post(() => item.ErrorMessage = DescribeFailure(ex, item.Kind));
            SetState(item, TransferState.Failed);
            item.Completion.TrySetResult();
        }
        finally
        {
            item.Cts.Dispose();
        }
    }

    private void SetState(FileTransferQueueItem item, TransferState state)
        => _post(() => item.State = state);

    /// <summary>
    /// Turns a transfer failure into text fit for the queue panel.
    /// </summary>
    /// <remarks>
    /// This panel renders whatever it is given, so <c>ex.Message</c> put developer English on screen
    /// in every language - "Download failed: SHA-256 integrity check failed.", the idle-watchdog
    /// timeout, and so on (RemEx-s4p4).
    /// <para>
    /// Dispatch is by TYPE, never by message text. A host refusal carries wording the phone wrote
    /// for a user and is shown verbatim; the failures the PC itself detects get localized
    /// sentences; anything else falls back to a generic one, with the detail written to the log by the
    /// caller. Matching on message content instead would silently revert to raw English the first time
    /// a message was reworded.
    /// </para>
    /// <para>
    /// The <see cref="IOException"/> arm exists because that is what a full or unplugged disk produces
    /// once the transfer flushes explicitly (RemEx-owc3), and it used to fall through to the generic
    /// sentence — which tells a user whose USB stick filled up to go and check their pairing.
    /// </para>
    /// <para>
    /// IT NEEDS THE DIRECTION, which is why this takes a kind. A download writes a local DESTINATION,
    /// so a disk failure there is about the folder it is being saved into. An upload READS a local
    /// source, and the commonest IOException on that path is a sharing violation — the file is open in
    /// Word or Excel — followed by the source file having moved. Describing that as "the folder you
    /// chose may be full" is not vague, it is confidently wrong, which is worse than the generic
    /// sentence it replaced. (RemEx-6tvh)
    /// </para>
    /// <para>
    /// The arm is placed after the three <see cref="IOException"/>-derived transfer exceptions — the
    /// host refusal, the integrity failure and the backlog abandonment, all of which would otherwise be
    /// swallowed by it — but that ordering does not rest on anyone remembering it: hoisting
    /// this arm is a COMPILE ERROR (CS8510, unreachable pattern), so the switch cannot silently start
    /// reporting a host refusal or a slow destination as a disk fault.
    /// </para>
    /// <para>
    /// PERMISSIONS ARE A SEPARATE ARM BECAUSE THEY ARE NOT AN IOException.
    /// <see cref="UnauthorizedAccessException"/> derives from <c>SystemException</c>, so it matched
    /// nothing above and fell through to the generic "check your pairing" — for a read-only folder, a
    /// download into a protected location, or a file whose ACL denies reading, none of which have
    /// anything to do with pairing. That it sits outside the IOException hierarchy is exactly why it
    /// was missed when the disk arm was added (RemEx-60li).
    /// </para>
    /// </remarks>
    private static string DescribeFailure(Exception ex, FileTransferQueueKind kind) => ex switch
    {
        FileTransferHostException host => host.HostMessage,
        FileTransferIntegrityException => LocalizationService.Instance["FileTransfer_ErrIntegrity"],
        FileTransferBacklogException => LocalizationService.Instance["FileTransfer_ErrDestinationTooSlow"],
        TimeoutException => LocalizationService.Instance["FileTransfer_ErrStoppedResponding"],
        IOException => LocalizationService.Instance[
            kind == FileTransferQueueKind.Download
                ? "FileTransfer_ErrDestinationUnavailable"
                : "FileTransfer_ErrSourceUnavailable"],
        UnauthorizedAccessException => LocalizationService.Instance[
            kind == FileTransferQueueKind.Download
                ? "FileTransfer_ErrDestinationNotAllowed"
                : "FileTransfer_ErrSourceNotAllowed"],
        _ => LocalizationService.Instance["FileTransfer_ErrGeneric"],
    };
}
