namespace Remex.Desktop.Services.FileTransfer;

/// <summary>
/// A download abandoned because the destination could not keep up and the unwritten backlog grew
/// past <see cref="LimitBytes"/>.
/// </summary>
/// <remarks>
/// <para>
/// Downloads hand every arriving chunk to a channel and a single writer task, which is what keeps
/// them in order. Nothing in the v3 file protocol lets the receiver slow the sender down — the host
/// streams chunks and expects no per-chunk reply — so when the destination writes slower than the
/// network delivers, that channel is where the difference accumulates. Unbounded, it accumulates
/// the ENTIRE speed difference: a 10 GB file arriving twice as fast as a USB stick can write it
/// queues about 5 GB of managed byte arrays before the transfer ends (RemEx-gyf4).
/// </para>
/// <para>
/// So the ceiling is a genuine trade, not a free win, and it is worth being honest about which way
/// it cuts. Below the limit nothing changes. Above it, a transfer that would previously have
/// completed — by spending gigabytes of RAM — now fails instead. That is the better failure: it is
/// immediate, it names a cause the user can act on, and it does not put the whole application at
/// the mercy of one slow drive. The fix that would let such a transfer both bound its memory AND
/// finish is receiver-driven flow control in the protocol itself, which is a wire change on both
/// sides and is tracked separately.
/// </para>
/// <para>
/// A distinct exception type rather than an <see cref="IOException"/> with a recognisable message,
/// for the reason <see cref="FileTransferIntegrityException"/> documents: the transfer queue
/// localizes failures by TYPE, and matching on message text silently reverts to raw English the
/// first time somebody rewords it (RemEx-s4p4).
/// </para>
/// </remarks>
public sealed class FileTransferBacklogException : IOException
{
    /// <summary>Unwritten bytes queued when the transfer was abandoned.</summary>
    public long QueuedBytes { get; }

    /// <summary>The ceiling that was exceeded.</summary>
    public long LimitBytes { get; }

    public FileTransferBacklogException(long queuedBytes, long limitBytes)
        : base($"Download abandoned: {queuedBytes} bytes were waiting to be written, over the " +
               $"{limitBytes}-byte limit. The destination is slower than the connection delivering the file.")
    {
        QueuedBytes = queuedBytes;
        LimitBytes = limitBytes;
    }
}
