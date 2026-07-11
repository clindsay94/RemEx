using Microsoft.Extensions.Logging.Abstractions;
using Remex.Agent.Services.FileTransfer;
using Remex.Core.Models;

namespace Remex.Agent.Tests;

/// <summary>
/// WP4 coverage for <see cref="TransferQueueService"/> (plan §1.4): FIFO ordering, persistence across
/// "process restarts", normalization of a mid-flight transfer to <see cref="TransferState.Paused"/> on
/// reload, and the "one active per direction" admission rule.
/// </summary>
public sealed class TransferQueueServiceTests
{
    private static TransferQueueService New(string storePath)
        => new(NullLogger<TransferQueueService>.Instance, storePath);

    private static TransferQueueItem Item(
        string tid, string mode = "upload", TransferState state = TransferState.Queued, DateTimeOffset created = default)
        => new()
        {
            TransferId = tid,
            Mode = mode,
            FileName = tid + ".bin",
            Size = 1000,
            State = state,
            CreatedUtc = created,
        };

    [Fact]
    public void Enqueue_GetAll_ReturnsFifoByCreatedUtc()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var svc = New(Path.Combine(dir.FullName, "transfer_queue.json"));
            var t0 = DateTimeOffset.UtcNow;

            // Enqueue out of chronological order; GetAll must still return oldest-first.
            svc.Enqueue(Item("b", created: t0.AddSeconds(2)));
            svc.Enqueue(Item("a", created: t0.AddSeconds(1)));
            svc.Enqueue(Item("c", created: t0.AddSeconds(3)));

            var ordered = svc.GetAll().Select(i => i.TransferId).ToArray();
            Assert.Equal(new[] { "a", "b", "c" }, ordered);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Enqueue_ReplacesByTransferId()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var svc = New(Path.Combine(dir.FullName, "transfer_queue.json"));
            svc.Enqueue(Item("x", state: TransferState.Queued));
            svc.Enqueue(Item("x", state: TransferState.Active));

            var all = svc.GetAll();
            Assert.Single(all);
            Assert.Equal(TransferState.Active, all[0].State);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void SetState_PersistsAcrossInstances()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var storePath = Path.Combine(dir.FullName, "transfer_queue.json");
            var svc = New(storePath);
            svc.Enqueue(Item("x", state: TransferState.Queued, created: DateTimeOffset.UtcNow));
            svc.SetState("x", TransferState.Done);

            // A brand-new instance against the same file sees the terminal state.
            var reloaded = New(storePath);
            var item = reloaded.GetAll().Single();
            Assert.Equal(TransferState.Done, item.State);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Reload_NormalizesMidFlightStatesToPaused()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var storePath = Path.Combine(dir.FullName, "transfer_queue.json");
            var svc = New(storePath);
            var now = DateTimeOffset.UtcNow;
            svc.Enqueue(Item("active", state: TransferState.Active, created: now));
            svc.Enqueue(Item("negotiating", state: TransferState.Negotiating, created: now.AddSeconds(1)));
            svc.Enqueue(Item("verifying", state: TransferState.Verifying, created: now.AddSeconds(2)));
            svc.Enqueue(Item("done", state: TransferState.Done, created: now.AddSeconds(3)));

            // Process death: a live socket can't survive, so mid-flight states come back Paused for resume.
            var reloaded = New(storePath);
            var byId = reloaded.GetAll().ToDictionary(i => i.TransferId, i => i.State);

            Assert.Equal(TransferState.Paused, byId["active"]);
            Assert.Equal(TransferState.Paused, byId["negotiating"]);
            Assert.Equal(TransferState.Paused, byId["verifying"]);
            Assert.Equal(TransferState.Done, byId["done"]); // terminal state left untouched.
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void OneActivePerDirection_IsEnforcedIndependentlyPerDirection()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var svc = New(Path.Combine(dir.FullName, "transfer_queue.json"));
            var now = DateTimeOffset.UtcNow;
            svc.Enqueue(Item("u1", mode: "upload", created: now));            // inbound
            svc.Enqueue(Item("u2", mode: "upload", created: now.AddSeconds(1))); // inbound
            svc.Enqueue(Item("d1", mode: "download", created: now.AddSeconds(2))); // outbound

            // Nothing running yet: the oldest queued inbound is promotable.
            Assert.False(svc.IsDirectionBusy(TransferDirection.Inbound));
            Assert.Equal("u1", svc.PeekNextQueued(TransferDirection.Inbound)?.TransferId);

            // Start u1 → inbound direction is now busy, so nothing else inbound may be promoted.
            svc.SetState("u1", TransferState.Active);
            Assert.True(svc.IsDirectionBusy(TransferDirection.Inbound));
            Assert.Null(svc.PeekNextQueued(TransferDirection.Inbound));

            // The outbound direction is independent and still free.
            Assert.False(svc.IsDirectionBusy(TransferDirection.Outbound));
            Assert.Equal("d1", svc.PeekNextQueued(TransferDirection.Outbound)?.TransferId);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Remove_DropsEntryAndPersists()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var storePath = Path.Combine(dir.FullName, "transfer_queue.json");
            var svc = New(storePath);
            svc.Enqueue(Item("x", created: DateTimeOffset.UtcNow));

            Assert.True(svc.Remove("x"));
            Assert.False(svc.Remove("x")); // idempotent — already gone.
            Assert.Empty(svc.GetAll());
            Assert.Empty(New(storePath).GetAll()); // persisted removal.
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void DirectionOf_MapsDownloadOutbound_EverythingElseInbound()
    {
        Assert.Equal(TransferDirection.Outbound, TransferQueueService.DirectionOf("download"));
        Assert.Equal(TransferDirection.Inbound, TransferQueueService.DirectionOf("upload"));
        Assert.Equal(TransferDirection.Inbound, TransferQueueService.DirectionOf("push"));
    }
}
