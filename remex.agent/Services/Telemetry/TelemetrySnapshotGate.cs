namespace Remex.Agent.Services.Telemetry;

/// <summary>
/// Holds the newest telemetry snapshot and lets readers wait for the next one (RemEx-uj7s).
/// </summary>
/// <typeparam name="T">The snapshot type. Reference equality is what "already seen" means.</typeparam>
/// <remarks>
/// <para>
/// **THE SAMPLER AND EVERY CLIENT STREAM USED TO RUN THEIR OWN 1-SECOND LOOP.** That is one
/// thread-pool timer per connected client per second on top of the sampler's, which at the one to
/// three clients this app sees is noise — and is not the reason to fix it.
/// </para>
/// <para>
/// **THE REASON IS THAT THE TWO CLOCKS DRIFT AGAINST EACH OTHER.** The sampler's period is 1000 ms
/// PLUS however long the sample took, while a stream's was a flat 1000 ms — so the stream was
/// systematically the faster loop and routinely woke to find the snapshot it had already sent. It
/// skipped, correctly, which meant a client's updates were mostly one second apart with a two-second
/// gap whenever the phase caught up. That is invisible on screen, because the skipped frame carried
/// identical numbers — but the phone's history chart appends one point per message against an INDEX
/// axis, so those gaps render as though uniform and the x-axis quietly stops being linear in time.
/// One clock means every client gets exactly the samples that exist, in step.
/// </para>
/// <para>
/// **THE SNAPSHOT AND ITS PULSE ARE ONE IMMUTABLE OBJECT BEHIND ONE FIELD, AND THAT IS THE WHOLE
/// CORRECTNESS ARGUMENT.** Held as two fields, a waiter has to read both, and a publish landing
/// between the two reads is lost: the waiter sees the old snapshot, then takes the pulse installed BY
/// that publish, and sleeps until the sample AFTER the one it was waiting for. Which read comes first
/// only moves the hole. One atomic read closes it — the pulse a waiter holds is by construction the
/// one that the publish superseding its snapshot will complete, so a waiter either sees the new
/// snapshot or is holding the pulse that announces it. There is no third case.
/// </para>
/// <para>
/// **THAT IS A STRUCTURAL ARGUMENT, NOT A TESTED ONE, AND DELIBERATELY SO.** A lost wakeup cannot be
/// driven deterministically from a test: <c>WaitForNextAsync</c> runs synchronously to its first
/// await, so a test that calls it and then publishes has already parked the waiter and never lands in
/// the gap. Reproducing it needs either a seam inside this method or a stress loop that fails by
/// luck. The design removes the race instead of testing for it — but do not read the passing suite as
/// cover for splitting these back into two fields, because nothing in it would notice.
/// </para>
/// </remarks>
internal sealed class TelemetrySnapshotGate<T> where T : class
{
    /// <summary>A snapshot and the signal that announces its successor, bound together.</summary>
    /// <remarks>
    /// **A PLAIN CLASS, DELIBERATELY NOT A <c>record</c>.** A positional record hands out <c>with</c>,
    /// and the generated copy constructor copies FIELDS rather than re-running property initialisers —
    /// so <c>_state with { Snapshot = x }</c> would compile, read as the idiomatic way to build the
    /// next state, and silently carry the PREVIOUS pulse. See <see cref="Retire"/> for why an
    /// already-completed pulse is a hot spin rather than a visible failure. Nothing here wants value
    /// equality or deconstruction, so the record bought nothing and cost that.
    /// </remarks>
    private sealed class State(T? snapshot)
    {
        /// <remarks>
        /// <c>RunContinuationsAsynchronously</c> is load-bearing, not boilerplate: without it,
        /// completing this runs every waiting stream's continuation INLINE on the completing thread,
        /// so one client slow to serialise or write would delay the next sample for every other
        /// client — the fix reintroducing, through its own plumbing, the coupling it exists to remove.
        /// </remarks>
        private readonly TaskCompletionSource _pulse = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Newest published snapshot; null before the first publish.</summary>
        public T? Snapshot { get; } = snapshot;

        /// <summary>Completes when a later publish supersedes this state.</summary>
        public Task Superseded => _pulse.Task;

        /// <summary>Marks this state superseded and wakes everyone parked on it.</summary>
        /// <remarks>
        /// **ONLY EVER CALL THIS ON THE STATE A SWAP RETIRES, NEVER ON THE ONE JUST INSTALLED.** The
        /// latter leaves a completed pulse sitting in the current state, so every later waiter awaits
        /// a finished task, loops, and spins hot — pinning a core with no exception and nothing in the
        /// log. The name is most of the defence: <c>Interlocked.Exchange(…).Retire()</c> reads
        /// correctly, while "retire the state I have just installed" reads as the nonsense it is.
        /// </remarks>
        public void Retire() => _pulse.TrySetResult();
    }

    private State _state = new(null);

    /// <summary>The newest snapshot, or null before the first publish.</summary>
    public T? Current => Volatile.Read(ref _state).Snapshot;

    /// <summary>Publishes a snapshot and wakes everyone waiting.</summary>
    public void Publish(T snapshot) =>
        // The swap installs the new snapshot and the signal for the one AFTER it together, and hands
        // back the state every current waiter is holding - which is exactly the one to retire.
        Interlocked.Exchange(ref _state, new State(snapshot)).Retire();

    /// <summary>
    /// Returns as soon as a snapshot exists that is not <paramref name="alreadySeen"/>.
    /// </summary>
    /// <remarks>
    /// Returns immediately when one is already available, which is what lets a client connecting
    /// mid-cycle get the current reading rather than waiting up to a second for the next one. There
    /// is no queue: a caller that stalls resumes at the newest snapshot, because an older telemetry
    /// reading has no value once a newer one exists.
    /// </remarks>
    public async Task<T> WaitForNextAsync(T? alreadySeen, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // ONE read. See the class remarks - snapshot and pulse must be taken together.
            var state = Volatile.Read(ref _state);

            if (state.Snapshot is { } snapshot && !ReferenceEquals(snapshot, alreadySeen))
                return snapshot;

            await state.Superseded.WaitAsync(ct);
        }
    }
}
