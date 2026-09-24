namespace Remex.Agent.Services.Telemetry;

/// <summary>What a sampler should do on the tick it has just been woken for.</summary>
public enum SampleTick
{
    /// <summary>Someone is reading and was last tick too: sample as normal.</summary>
    Sample,

    /// <summary>Someone is reading and nobody was last tick: sample, and say sampling has resumed.</summary>
    Resume,

    /// <summary>Nobody is reading and nobody was last tick either: do nothing.</summary>
    Skip,

    /// <summary>
    /// Nobody is reading but somebody was last tick: do nothing, and drop the last published reading
    /// so a consumer arriving later is not handed it as current.
    /// </summary>
    Idle,
}

/// <summary>
/// Counts who is reading a sampler's output, so the sampler can stop doing the work while nobody is
/// (perf audit P0-10).
/// </summary>
/// <remarks>
/// <para>
/// **THE HOST SAMPLERS USED TO RUN FOR THE LIFE OF THE PROCESS.** A 1 Hz telemetry poll over ~450
/// sensors (WMI can block for seconds) and a 1 Hz cross-process media-session read, on every
/// tray-resident PC, whether or not a phone was attached or a window was showing. Each consumer now
/// takes a lease (<see cref="Acquire"/>) for as long as it reads, and the sampler skips its work on
/// any tick where none is held.
/// </para>
/// <para>
/// **A COUNT, NOT A FLAG.** Consumers come and go independently - several phones, the PC's own window,
/// its tray flyout, an armed sensor alert - and a shared boolean is exactly the shape where one of
/// them switching off silences all the others. Each lease releases only itself, once.
/// </para>
/// <para>
/// **THE TIMER KEEPS TICKING WHILE IDLE, ON PURPOSE.** Only the WORK is skipped. The samplers' timers
/// are PeriodicTimers whose even spacing the phone's index-axis history depends on (RemEx-6sibx);
/// leaving them untouched means sampling resumes on the same grid it left, and a do-nothing wake once
/// a second costs nothing next to the poll it replaced.
/// </para>
/// <para>
/// Thread-safety: <see cref="Acquire"/> and lease disposal may run on any thread. <see cref="NextTick"/>
/// is called only by the owning sampler's loop, which is single-flow.
/// </para>
/// </remarks>
public sealed class SamplingDemand
{
    private readonly bool _ungated;
    private int _consumers;

    // Sampler-loop only. A gated demand starts idle, so the first sampled tick reports Resume and the
    // start-up with nobody attached does not log a spurious "went idle".
    private bool _idle;

    /// <summary>A gated demand: the sampler runs only while a lease is held.</summary>
    public SamplingDemand() : this(ungated: false)
    {
    }

    private SamplingDemand(bool ungated)
    {
        _ungated = ungated;
        _idle = !ungated;
    }

    /// <summary>
    /// A demand that always reports someone is reading - the behaviour before P0-10, for a sampler
    /// constructed without a gate (the tests that drive a sampler directly).
    /// </summary>
    public static SamplingDemand Ungated() => new(ungated: true);

    /// <summary>How many leases are currently held.</summary>
    public int Consumers => Volatile.Read(ref _consumers);

    /// <summary>Whether the sampler should be doing its work right now.</summary>
    public bool IsDemanded => _ungated || Consumers > 0;

    /// <summary>Registers a reader until the returned lease is disposed. Disposing twice is harmless.</summary>
    public IDisposable Acquire()
    {
        Interlocked.Increment(ref _consumers);
        return new Lease(this);
    }

    /// <summary>
    /// Decides this tick. Called once per timer tick by the owning sampler, before it does any work.
    /// </summary>
    public SampleTick NextTick()
    {
        if (IsDemanded)
        {
            if (!_idle) return SampleTick.Sample;
            _idle = false;
            return SampleTick.Resume;
        }

        if (_idle) return SampleTick.Skip;
        _idle = true;
        return SampleTick.Idle;
    }

    private sealed class Lease(SamplingDemand owner) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            // Exactly once, whoever calls it how often: a double release would drive the count below
            // the real number of readers and could idle the sampler under a consumer still reading.
            if (Interlocked.Exchange(ref _released, 1) == 0)
                Interlocked.Decrement(ref owner._consumers);
        }
    }
}
