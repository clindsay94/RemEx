using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Threading;
using Remex.Core.Services;

namespace Remex.Desktop.Services;

/// <summary>
/// The kind of user-facing event recorded in the Home "Recent activity" feed. Persisted as a
/// string (see <see cref="ActivityService"/>'s serializer options) so reordering this enum never
/// silently corrupts an existing <c>recent_activity.json</c>.
/// </summary>
public enum ActivityKind
{
    /// <summary>A file the connected phone pushed onto this PC. Host-bridged (follow-up work).</summary>
    FileReceived,

    /// <summary>A file sent from this PC to the connected phone (send-to-device).</summary>
    FileSent,

    /// <summary>A local file uploaded into the connected host's shared root.</summary>
    FileUploaded,

    /// <summary>A file downloaded from the connected host onto local disk.</summary>
    FileDownloaded,

    /// <summary>An app started from the App Launcher.</summary>
    AppLaunched,

    /// <summary>A power/remote command issued from the Remote screen.</summary>
    CommandRun,

    /// <summary>The connected phone completed its pairing/reconnect handshake with this PC.</summary>
    DeviceConnected,

    /// <summary>
    /// A phone's connection ended. The detail carries the device and, where the host knows it, WHY.
    /// </summary>
    /// <remarks>
    /// A FEED THAT RECORDS ARRIVALS AND NOT DEPARTURES READS AS THOUGH EVERY PHONE THAT EVER
    /// CONNECTED IS STILL ATTACHED (RemEx-2xjv). And the reason is not decoration: a phone that
    /// closed cleanly and one whose socket died are different facts, and this feed is where somebody
    /// would look to tell a flapping network from a device somebody walked away with.
    /// </remarks>
    DeviceDisconnected,
}

/// <summary>
/// One entry in the Home recent-activity feed. Only <see cref="Kind"/>, <see cref="Detail"/> and
/// <see cref="TimestampLocal"/> are persisted; the description is computed live from the current UI
/// culture so a language switch relabels existing history correctly (the same lazy-read approach
/// <c>FileTransferQueueItem</c> uses for its state labels). The leading icon shown for
/// <see cref="Kind"/> is resolved in XAML via <c>ActivityKindToIconKindConverter</c>, not here.
/// </summary>
public sealed class ActivityEntry
{
    public ActivityKind Kind { get; set; }

    /// <summary>The subject of the event — a file name, app name, or command verb.</summary>
    public string Detail { get; set; } = string.Empty;

    public DateTime TimestampLocal { get; set; }

    /// <summary>Localized one-line description, e.g. "Sent report.pdf to phone".</summary>
    [JsonIgnore]
    public string Description
    {
        get
        {
            var key = $"Activity_{Kind}";
            var format = LocalizationService.Instance[key];
            // The indexer returns the key itself when a resource is missing — fall back readably.
            return string.Equals(format, key, StringComparison.Ordinal)
                ? $"{Kind}: {Detail}"
                : string.Format(format, Detail);
        }
    }

    /// <summary>Short, culture-aware time label — clock time for today, else short date + time.</summary>
    [JsonIgnore]
    public string TimeLabel =>
        TimestampLocal.Date == DateTime.Now.Date
            ? TimestampLocal.ToString("t")
            : TimestampLocal.ToString("g");
}

/// <summary>
/// Process-wide store of recent user-facing activity (file transfers, app launches, remote
/// commands), surfaced on the Home page. A static singleton — mirroring
/// <see cref="LocalizationService"/> — so any call site across <em>both</em> PC-side DI containers
/// (the desktop view-models and the host→desktop bridge in <c>App</c>) can record without threading
/// a dependency through constructors. The newest event is always at index 0. Writes are debounced to
/// <c>%LocalAppData%\Remex\recent_activity.json</c>.
/// </summary>
public sealed class ActivityService
{
    private static readonly Lazy<ActivityService> _instance = new(() => new ActivityService());
    public static ActivityService Instance => _instance.Value;

    /// <summary>Maximum number of events kept in memory and on disk.</summary>
    private const int MaxStored = 60;
    private const int DebounceMs = 1500;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _filePath;
    private readonly object _saveGate = new();
    private Timer? _debounceTimer;
    private volatile List<ActivityEntry>? _pendingSnapshot;

    /// <summary>Live, newest-first feed bound by the Home page. Mutated only on the UI thread.</summary>
    public ObservableCollection<ActivityEntry> Recent { get; } = new();

    /// <summary>
    /// The activity file: the per-user RemEx directory, or the test redirect when it is set. This
    /// singleton is reachable from the agent's message handlers, so a test that drove a transfer or
    /// a ping appended fixture entries to the developer's own activity feed (RemEx-ln0k).
    /// </summary>
    private static string DefaultFilePath =>
        Path.Combine(RemexDataPaths.PerUserDirectory, "recent_activity.json");

    /// <summary>Exposes the resolved default path so tests can assert the redirect covers it.</summary>
    internal static string DefaultFilePathForTests => DefaultFilePath;

    /// <summary>The file this instance actually resolved, so a test can pin the constructor.</summary>
    internal string FilePathForTests => _filePath;

    /// <summary>
    /// UI-thread state for the async load (perf audit P3-59). Until the stored feed has been merged,
    /// a save would overwrite the file with only the events recorded since launch, so it is deferred.
    /// </summary>
    private bool _loaded;
    private bool _saveRequestedBeforeLoad;
    private bool _clearedBeforeLoad;

    private ActivityService()
    {
        _filePath = DefaultFilePath;

        // THE FILE IS READ ON THE THREAD POOL, NOT ON THE FIRST CALLER'S THREAD (perf audit P3-59).
        // The first caller can be an agent socket thread (PingPongHandler, TransferSessionManager), and
        // this used to do the directory create and the read right there - and then fill Recent, a
        // UI-bound collection, from that thread too. Now the read happens off every caller's thread and
        // the merge is posted to the UI thread, where Recent is always mutated. App also touches
        // Instance at startup so the read starts before the first event rather than on it.
        _ = Task.Run(ReadStored).ContinueWith(
            t => Post(() => ApplyLoaded(t.Result)),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>Starts the singleton (and so its background load) from a known thread.</summary>
    public static void Warm() => _ = Instance;

    private void ApplyLoaded(IReadOnlyList<ActivityEntry> stored)
    {
        MergeLoaded(Recent, stored, _clearedBeforeLoad, MaxStored);
        _loaded = true;
        if (_saveRequestedBeforeLoad)
            RequestSave();
    }

    /// <summary>
    /// Appends the stored (older) feed after whatever was recorded while it loaded (newer, already at
    /// the head), capped - unless the feed was cleared in that window, in which case the stored history
    /// is what the user just cleared. Internal and static so the ordering is pinned by a test.
    /// </summary>
    internal static void MergeLoaded(IList<ActivityEntry> recent, IReadOnlyList<ActivityEntry> stored,
        bool clearedBeforeLoad, int max)
    {
        if (clearedBeforeLoad) return;
        foreach (var ev in stored)
        {
            if (recent.Count >= max) break;
            recent.Add(ev);
        }
    }

    /// <summary>Records a new event at the head of the feed. Safe to call from any thread.</summary>
    public void Record(ActivityKind kind, string? detail)
    {
        var ev = new ActivityEntry
        {
            Kind = kind,
            Detail = string.IsNullOrWhiteSpace(detail) ? string.Empty : detail.Trim(),
            TimestampLocal = DateTime.Now,
        };

        Post(() =>
        {
            Recent.Insert(0, ev);
            while (Recent.Count > MaxStored)
                Recent.RemoveAt(Recent.Count - 1);
            RequestSave();
        });
    }

    /// <summary>Clears the entire feed. Safe to call from any thread.</summary>
    public void Clear()
    {
        Post(() =>
        {
            if (!_loaded) _clearedBeforeLoad = true;
            Recent.Clear();
            RequestSave();
        });
    }

    /// <summary>Reads the persisted feed (newest-first). Runs on the thread pool; never throws.</summary>
    private IReadOnlyList<ActivityEntry> ReadStored()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            if (!File.Exists(_filePath))
                return Array.Empty<ActivityEntry>();

            var json = File.ReadAllText(_filePath);
            var stored = JsonSerializer.Deserialize<List<ActivityEntry>>(json, JsonOptions);
            return stored is null ? Array.Empty<ActivityEntry>() : stored.Take(MaxStored).ToList();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RemexActivity] Failed to load '{_filePath}': {ex.Message}");
            return Array.Empty<ActivityEntry>();
        }
    }

    private void RequestSave()
    {
        if (!_loaded)
        {
            // Saving now would replace the stored history with only this session's events.
            _saveRequestedBeforeLoad = true;
            return;
        }

        // Snapshot on the UI thread (where Recent is mutated); the timer thread only reads it.
        _pendingSnapshot = Recent.ToList();
        _debounceTimer?.Dispose();
        _debounceTimer = new Timer(_ => Flush(), null, DebounceMs, Timeout.Infinite);
    }

    private void Flush()
    {
        var snapshot = _pendingSnapshot;
        if (snapshot is null)
            return;
        _pendingSnapshot = null;

        lock (_saveGate)
        {
            try
            {
                var json = JsonSerializer.Serialize(snapshot, JsonOptions);
                File.WriteAllText(_filePath, json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RemexActivity] Failed to save '{_filePath}': {ex.Message}");
            }
        }
    }

    private static void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }
}
