using System.Text.Json;
using Microsoft.Extensions.Logging;
using Remex.Core.Guards;
using Remex.Core.Models;
using Remex.Core.Services;

namespace Remex.Agent.Services.FileTransfer;

/// <summary>
/// One entry in the PC host's persistent transfer queue (plan §1.4). Host-local state — NOT a wire
/// message — so it lives in <c>remex.agent</c> and is (de)serialized with the reflection-based
/// <see cref="JsonSerializer"/>, never through the NativeAOT source-gen context. Shares the wire
/// <see cref="TransferState"/> vocabulary from <c>Remex.Core</c> so the UI and the queue agree on states.
/// </summary>
public sealed record TransferQueueItem
{
    /// <summary>Stable transfer id (GUID) — the primary key, matches the <c>transferId</c> on the wire.</summary>
    public required string TransferId { get; init; }

    /// <summary>Transfer mode: <see cref="FileTransferModes"/> ("download" | "upload" | "push").</summary>
    public required string Mode { get; init; }

    public required string FileName { get; init; }
    public long Size { get; init; }

    /// <summary>The paired peer this transfer is with (Android device / client id).</summary>
    public string? PeerClientId { get; init; }

    /// <summary>Host-side shared-root id the file is read from / written to.</summary>
    public string? DestRoot { get; init; }
    public string? DestRelativePath { get; init; }

    /// <summary>Opaque peer-side path (Android content URI / save location); host-local diagnostic only.</summary>
    public string? SourcePath { get; init; }

    public TransferState State { get; init; } = TransferState.Queued;
    public long BytesTransferred { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// The data-flow direction of a transfer relative to this PC host. A transfer is <see cref="Inbound"/>
/// when the host receives bytes (upload / push) and <see cref="Outbound"/> when the host sends bytes
/// (download). The queue admits at most one <see cref="TransferState.Active"/> transfer per direction
/// (plan §1.4: "one active per direction").
/// </summary>
public enum TransferDirection
{
    Inbound,
    Outbound,
}

/// <summary>
/// FIFO, process-death-surviving transfer queue for transfers this PC host participates in (plan §1.4).
/// Persisted to <c>%ProgramData%\Remex\transfer_queue.json</c> (machine-wide via
/// <see cref="RemexDataPaths"/>, unchanged on Linux). States follow
/// <c>Queued → Negotiating → Active → (Paused) → Verifying → Done|Failed|Cancelled</c>. At most one
/// <see cref="TransferState.Active"/> transfer is admitted per <see cref="TransferDirection"/>.
///
/// <para>
/// This service lives in <c>remex.agent</c> and is NOT NativeAOT-constrained; the queue file is host-local
/// state, never a wire message, so it uses the same reflection-based <see cref="JsonSerializer"/> pattern
/// as the sibling <see cref="FileTrustService"/> / <see cref="FileTransferService"/> stores.
/// </para>
/// </summary>
public sealed class TransferQueueService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly ILogger<TransferQueueService> _logger;
    private readonly string _storePath;

    // Single lock guards both the in-memory list and the on-disk file so enqueue/update/remove are atomic
    // with their persistence. The queue is low-frequency (one entry per user-initiated transfer), so a
    // coarse lock is simpler and safer than finer-grained concurrency here.
    private readonly object _gate = new();
    private readonly List<TransferQueueItem> _items = new();

    public TransferQueueService(ILogger<TransferQueueService> logger)
        : this(logger, storePath: null)
    {
    }

    /// <summary>Test seam: overrides the store path so persistence can be exercised in a temp directory.</summary>
    internal TransferQueueService(ILogger<TransferQueueService> logger, string? storePath)
    {
        _logger = Guard.NotNull(logger);

        if (storePath is null)
        {
            var legacyFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Remex");
            var baseFolder = RemexDataPaths.ResolveDirectory(legacyFolder);
            RemexDataPaths.TryMigrateWindowsFile("transfer_queue.json");
            _storePath = Path.Combine(baseFolder, "transfer_queue.json");
        }
        else
        {
            _storePath = storePath;
        }

        LoadFromDisk();
    }

    /// <summary>Maps a transfer <paramref name="mode"/> to its data-flow direction relative to the host.</summary>
    public static TransferDirection DirectionOf(string mode) => mode switch
    {
        FileTransferModes.Download => TransferDirection.Outbound,
        _ => TransferDirection.Inbound, // upload | push | anything else host-receives.
    };

    /// <summary>Adds (or replaces, by <see cref="TransferQueueItem.TransferId"/>) a queue entry and persists.</summary>
    public TransferQueueItem Enqueue(TransferQueueItem item)
    {
        Guard.NotNull(item);
        var now = DateTimeOffset.UtcNow;
        var stamped = item with
        {
            CreatedUtc = item.CreatedUtc == default ? now : item.CreatedUtc,
            UpdatedUtc = now,
        };

        lock (_gate)
        {
            _items.RemoveAll(existing => existing.TransferId == stamped.TransferId);
            _items.Add(stamped);
            PersistLocked();
        }
        return stamped;
    }

    /// <summary>
    /// Applies <paramref name="mutate"/> to the entry with <paramref name="transferId"/> (if present),
    /// stamps <see cref="TransferQueueItem.UpdatedUtc"/>, and persists. Returns the updated entry or null.
    /// </summary>
    public TransferQueueItem? Update(string transferId, Func<TransferQueueItem, TransferQueueItem> mutate)
    {
        Guard.NotNull(mutate);
        if (string.IsNullOrWhiteSpace(transferId))
            return null;

        lock (_gate)
        {
            var index = _items.FindIndex(i => i.TransferId == transferId);
            if (index < 0)
                return null;

            var updated = mutate(_items[index]) with { UpdatedUtc = DateTimeOffset.UtcNow };
            _items[index] = updated;
            PersistLocked();
            return updated;
        }
    }

    /// <summary>Convenience for the common case of only moving an entry to a new <paramref name="state"/>.</summary>
    public TransferQueueItem? SetState(string transferId, TransferState state)
        => Update(transferId, item => item with { State = state });

    /// <summary>Removes the entry with <paramref name="transferId"/> and persists. Returns true when removed.</summary>
    public bool Remove(string transferId)
    {
        if (string.IsNullOrWhiteSpace(transferId))
            return false;

        lock (_gate)
        {
            var removed = _items.RemoveAll(i => i.TransferId == transferId) > 0;
            if (removed)
                PersistLocked();
            return removed;
        }
    }

    /// <summary>Snapshot of all queue entries in FIFO order (oldest <see cref="TransferQueueItem.CreatedUtc"/> first).</summary>
    public IReadOnlyList<TransferQueueItem> GetAll()
    {
        lock (_gate)
        {
            return _items
                .OrderBy(i => i.CreatedUtc)
                .ThenBy(i => i.TransferId, StringComparer.Ordinal)
                .ToList();
        }
    }

    /// <summary>
    /// True when a transfer in the given <paramref name="direction"/> is currently
    /// <see cref="TransferState.Active"/> (or <see cref="TransferState.Verifying"/>) — used to enforce the
    /// "one active per direction" rule before promoting the next queued transfer.
    /// </summary>
    public bool IsDirectionBusy(TransferDirection direction)
    {
        lock (_gate)
        {
            return _items.Any(i =>
                DirectionOf(i.Mode) == direction
                && i.State is TransferState.Active or TransferState.Negotiating or TransferState.Verifying);
        }
    }

    /// <summary>
    /// Returns the oldest <see cref="TransferState.Queued"/> transfer for <paramref name="direction"/> that
    /// could be started next, or null when the direction is already busy or has nothing queued. Pure query —
    /// the caller promotes it (e.g. to <see cref="TransferState.Negotiating"/>) via <see cref="SetState"/>.
    /// </summary>
    public TransferQueueItem? PeekNextQueued(TransferDirection direction)
    {
        lock (_gate)
        {
            if (IsDirectionBusy(direction))
                return null;

            return _items
                .Where(i => DirectionOf(i.Mode) == direction && i.State == TransferState.Queued)
                .OrderBy(i => i.CreatedUtc)
                .ThenBy(i => i.TransferId, StringComparer.Ordinal)
                .FirstOrDefault();
        }
    }

    private void LoadFromDisk()
    {
        // BEFORE THE EXISTENCE CHECK, for the reason PairedClientRegistry gives (RemEx-njzcx):
        // a first write killed between staging and rename leaves an orphan and NO store, so a sweep
        // below an early return is walked past on every startup of the machine that most needs it.
        RemexDataPaths.SweepStagingOrphans(_storePath);

        try
        {
            if (!File.Exists(_storePath))
                return;

            var json = File.ReadAllText(_storePath);
            var items = JsonSerializer.Deserialize<List<TransferQueueItem>>(json, JsonOptions);
            if (items is null)
                return;

            lock (_gate)
            {
                _items.Clear();
                foreach (var item in items)
                {
                    if (item is null || string.IsNullOrWhiteSpace(item.TransferId))
                        continue;

                    // A transfer that was mid-flight when the process died can't resume its live socket
                    // state; surface it as Paused so the UI can offer resume, rather than a phantom Active.
                    var normalized = item.State is TransferState.Active or TransferState.Negotiating or TransferState.Verifying
                        ? item with { State = TransferState.Paused }
                        : item;
                    _items.Add(normalized);
                }
            }

            _logger.LogInformation("Loaded {Count} transfer-queue entr(ies) from {Path}.", _items.Count, _storePath);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse transfer queue at {Path}; starting empty.", _storePath);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Failed to load transfer queue at {Path}; starting empty.", _storePath);
        }
    }

    private void PersistLocked()
    {
        try
        {
            var directory = Path.GetDirectoryName(_storePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var ordered = _items
                .OrderBy(i => i.CreatedUtc)
                .ThenBy(i => i.TransferId, StringComparer.Ordinal)
                .ToList();

            var json = JsonSerializer.Serialize(ordered, JsonOptions);

            // Per-write staging name, not a fixed <store>.tmp shared with every other writer of this
            // file — see RemexDataPaths.WriteAllTextAtomic (RemEx-kow1).
            RemexDataPaths.WriteAllTextAtomic(_storePath, json);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Failed to persist transfer queue at {Path}.", _storePath);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "No access to persist transfer queue at {Path}.", _storePath);
        }
    }
}
