using Moq;
using Remex.Agent.Services;
using Remex.Agent.Services.Input;
using Remex.Core.Services;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Pins that an empty MAC is retried rather than cached for the session (RemEx-bgq8).
/// </summary>
/// <remarks>
/// The bug is Linux-only and invisible on Windows. On Linux the agent is started by XDG autostart
/// at login and <c>HostBootstrapper</c> forces the capability record during startup, so an
/// interface still coming up yields no MAC — and the empty answer was then cached for the whole
/// session. Every phone that connected was told this PC has no MAC, Wake-on-LAN silently reverted
/// to "the user must type it in" (the exact setup step RemEx-izuj removed), and a reconnect could
/// not fix it because the value was cached rather than re-read.
/// </remarks>
public class HostCapabilitiesMacReprobeTests
{
    /// <summary>Counts probes and lets the test decide when the interface "comes up".</summary>
    private sealed class ScriptedMacProbe
    {
        private readonly Queue<string> _answers;

        public ScriptedMacProbe(params string[] answers) => _answers = new Queue<string>(answers);

        private readonly Lock _gate = new();

        public int Calls { get; private set; }

        /// <summary>
        /// The last scripted answer repeats once the queue is exhausted.
        /// </summary>
        /// <remarks>
        /// LOCKED because the concurrency test genuinely races this. Without it, concurrent
        /// <c>Calls++</c> and <c>Queue.Dequeue</c> is a data race that can throw or corrupt the
        /// queue - the test would then be measuring its own fake rather than the code.
        /// </remarks>
        public string Probe()
        {
            lock (_gate)
            {
                Calls++;
                return _answers.Count > 1 ? _answers.Dequeue() : _answers.Peek();
            }
        }
    }

    private static HostCapabilitiesProvider Create(ScriptedMacProbe probe) =>
        new(new FakeScreenCaptureService(), Mock.Of<IInputSimulationService>(), probe.Probe);

    [Fact]
    public void AnInterfaceThatComesUpLateIsPickedUpOnTheNextCall()
    {
        // THE BUG. The first GetCurrent happens at login with the NIC still coming up; the second
        // is a phone connecting moments later, by which point the adapter exists. Before the fix
        // the second call returned the cached empty string and every session stayed broken.
        //
        // TWO EMPTY ANSWERS, NOT ONE, because a single GetCurrent can probe twice: once inside
        // Build() and once in the retry immediately after. Scripting only one empty answer had the
        // very first call succeed, which is a nicer story than reality and would have tested
        // nothing - the whole point is the state where the interface is still down.
        var probe = new ScriptedMacProbe("", "", "9C:6B:00:9B:1B:D2");

        var provider = Create(probe);

        Assert.Equal(string.Empty, provider.GetCurrent().MacAddress);
        Assert.Equal("9C:6B:00:9B:1B:D2", provider.GetCurrent().MacAddress);
    }

    [Fact]
    public void OnceFoundTheMacIsNotProbedAgain()
    {
        // The re-probe must not become a per-handshake cost. A found MAC is a one-way latch: there
        // is nothing left to retry, so every later call is the same cheap read it was before.
        var probe = new ScriptedMacProbe("", "9C:6B:00:9B:1B:D2");
        var provider = Create(probe);

        provider.GetCurrent();
        provider.GetCurrent();
        var callsAfterResolution = probe.Calls;

        provider.GetCurrent();
        provider.GetCurrent();
        provider.GetCurrent();

        Assert.Equal(callsAfterResolution, probe.Calls);
        Assert.Equal("9C:6B:00:9B:1B:D2", provider.GetCurrent().MacAddress);
    }

    [Fact]
    public void AMacFoundAtStartupIsNeverReProbed()
    {
        // The healthy path, which is every Windows host and most Linux ones. A good answer must not
        // be re-probed at all - it cannot be improved, and re-reading it could only make it worse.
        var probe = new ScriptedMacProbe("9C:6B:00:9B:1B:D2");
        var provider = Create(probe);

        provider.GetCurrent();
        var callsAfterFirst = probe.Calls;
        provider.GetCurrent();
        provider.GetCurrent();

        Assert.Equal(callsAfterFirst, probe.Calls);
    }

    [Fact]
    public void AMachineWithNoAdapterAtAllKeepsAnsweringEmptyRatherThanFailing()
    {
        // A host genuinely without a qualifying adapter must degrade to the manual-entry fallback,
        // which predates this feature and still works - not throw, and not invent a value.
        var probe = new ScriptedMacProbe("");
        var provider = Create(probe);

        Assert.Equal(string.Empty, provider.GetCurrent().MacAddress);
        var callsAfterFirst = probe.Calls;
        Assert.Equal(string.Empty, provider.GetCurrent().MacAddress);

        // AND IT KEEPS TRYING, which is the accepted cost of the fix stated as a test rather than
        // as prose. There is no adapter to find, so the retry never latches and every GetCurrent
        // probes again. That is affordable because GetCurrent is per-CONNECTION - three call sites,
        // none of them per-message or per-frame - and PrimaryNetworkAdapter.Find is one interface
        // enumeration with no subprocess. If that ever stops being true, this assertion is where
        // the decision to bound the retries should announce itself.
        Assert.True(probe.Calls > callsAfterFirst, "a machine with no adapter should still be retrying");
    }

    [Fact]
    public void TheRestOfTheRecordIsStillCachedAcrossTheReProbe()
    {
        // The re-probe is deliberately not a rebuild. Re-running the Linux prerequisite evaluation
        // on every handshake would spawn `which` subprocesses for facts that cannot change, which
        // is the cost the cache exists to avoid - so everything except the MAC must be identical.
        var probe = new ScriptedMacProbe("", "9C:6B:00:9B:1B:D2");
        var provider = Create(probe);

        var before = provider.GetCurrent();
        var callsAfterResolution = probe.Calls;
        var after = provider.GetCurrent();

        // THE ASSERTION THAT ACTUALLY PINS "NOT A REBUILD", and the one the first draft was
        // missing. Build() is deterministic on a given machine, so comparing the fields below
        // cannot tell a cached record from a freshly rebuilt one - every one of them would match,
        // and so would NotSame. But Build() also calls the probe, so a rebuild is visible as an
        // extra probe call. Without this the whole cost argument was pinned by nothing.
        Assert.Equal(callsAfterResolution, probe.Calls);

        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.Platform, after.Platform);
        Assert.Equal(before.RuntimeMode, after.RuntimeMode);
        Assert.Equal(before.SupportsRemoteDesktop, after.SupportsRemoteDesktop);
        Assert.Equal(before.SupportsInputSimulation, after.SupportsInputSimulation);
        Assert.Equal(before.InputBackend, after.InputBackend);

        // ...and the record it returns must be a copy, not a mutation of the cached one, or the
        // late MAC would be written into a snapshot other callers already hold.
        Assert.NotSame(before, after);
    }

    [Fact]
    public void ConcurrentCallersAllSeeTheMacOnceItResolves()
    {
        // GetCurrent is on the WebSocket handshake path, so several clients genuinely race here.
        //
        // THE RACE HAS TO HAPPEN WHILE THE MAC IS STILL EMPTY, which the first draft did not
        // arrange: it resolved the MAC before the Parallel.For, so all 32 threads took the latched
        // fast path and none of them probed. The concurrent probe - the thing the code reasons
        // about - was never executed. Scripting enough empty answers to survive the first
        // GetCurrent leaves every parallel caller entering the retry for real.
        //
        // What must hold is not merely "nobody sees empty" but "everybody sees THE SAME MAC". Two
        // concurrent probes during interface bring-up can genuinely return different adapters,
        // wlan0 and eth0 microseconds apart, and handing two phones two different MACs for one
        // machine is the defect CompareExchange exists to prevent.
        var probe = new ScriptedMacProbe("", "", "9C:6B:00:9B:1B:D2");
        var provider = Create(probe);

        provider.GetCurrent();

        var results = new string?[32];
        Parallel.For(0, results.Length, i => results[i] = provider.GetCurrent().MacAddress);

        Assert.All(results, mac => Assert.Equal("9C:6B:00:9B:1B:D2", mac));
        Assert.Single(results.Distinct());
    }
}
