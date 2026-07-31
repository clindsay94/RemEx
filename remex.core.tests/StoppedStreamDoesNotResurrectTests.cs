using System;
using System.Threading.Tasks;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Native;
using Xunit;

namespace Remex.Core.Tests;

/// <summary>
/// Pins that input arriving after a deliberate stop does not reconnect and start a new capture
/// session (RemEx-yzbb).
/// </summary>
/// <remarks>
/// <para>
/// <c>SendInputAsync</c> and <c>SendPointerBatchAsync</c> auto-start a stream when one is not
/// running. That is correct recovery after a transient socket drop — the receive loop clears the
/// streaming flag, and the next input should bring the session back. It is completely wrong after the
/// user stops: desktop work is serialised on one queue (RemEx-krvz), so a straggler keyUp or a burst
/// of pointer samples from the user's last touch can be queued BEHIND the stop, and each one would
/// reconnect and start a whole new capture session. The phone then encodes and receives frames nobody
/// asked for, and the host holds a session open with its keep-awake engaged — while the UI shows the
/// stream as stopped.
/// </para>
/// <para>
/// The distinction cannot be made from the streaming flag alone, because it reads false for both
/// reasons. Hence a separate record of INTENT.
/// </para>
/// <para>
/// <c>RemexDesktopClient</c> is a process singleton with a private constructor, so these run against
/// <c>Current</c> and share state, the same caveat <c>RemexClientManagerStateTests</c> documents on
/// the Kotlin side. They deliberately leave the client STOPPED — the safe direction, since a leaked
/// "stopped" only makes a later test drop input it would have sent.
/// </para>
/// <para>
/// WHAT THESE DO NOT COVER, since the doc above could be read as claiming more: the singleton is
/// never CONNECTED here, so no test ever has a live stream to be resurrected. All three pin the
/// same reachable half — that a stop records the intent and a later send honours it without opening
/// a socket. Exercising a real stop-of-a-running-stream needs a fake host, which the private
/// constructor rules out.
/// </para>
/// </remarks>
public class StoppedStreamDoesNotResurrectTests
{
    /// <summary>TEST-NET-1 (RFC 5737). Guaranteed not to route, so any real connect attempt hangs.</summary>
    private const string UnroutableHost = "192.0.2.1";
    private const int Port = 5005;

    private static readonly TimeSpan MustReturnWithin = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Runs <paramref name="send"/> and reports whether it returned without trying to connect.
    /// </summary>
    /// <remarks>
    /// The observable difference IS the connect attempt. Guarded, the call returns synchronously
    /// having touched no socket. Unguarded, it reaches <c>EnsureConnectedAsync</c> and blocks on a
    /// TCP handshake to an unroutable address, which cannot complete inside this window — the OS
    /// gives up in minutes, not seconds.
    /// </remarks>
    private static async Task<bool> ReturnedWithoutConnecting(Func<Task> send)
    {
        var sendTask = send();
        var finished = await Task.WhenAny(sendTask, Task.Delay(MustReturnWithin));
        return ReferenceEquals(finished, sendTask) && !sendTask.IsFaulted;
    }

    [Fact]
    public async Task InputAfterADeliberateStopIsDroppedRatherThanReconnecting()
    {
        // THE BEAD. A keyUp queued behind the stop must not bring the stream back from the dead.
        await RemexDesktopClient.Current.StopStreamAsync();

        var delivered = await ReturnedWithoutConnecting(() =>
            RemexDesktopClient.Current.SendInputAsync(
                UnroutableHost, Port, new InputEvent { EventType = InputEventTypes.KeyUp, KeyCode = 0x11 }));

        Assert.True(delivered, "input after a stop must return without opening a socket");
        Assert.False(RemexDesktopClient.Current.IsConnected, "and must not have opened a socket");
    }

    [Fact]
    public async Task PointerBatchesAfterADeliberateStopAreDroppedToo()
    {
        // Likelier to fire than the keyUp: a touch produces a burst of samples, and the tail of that
        // burst can still be in flight when the user stops.
        await RemexDesktopClient.Current.StopStreamAsync();

        var delivered = await ReturnedWithoutConnecting(() =>
            RemexDesktopClient.Current.SendPointerBatchAsync(
                UnroutableHost, Port, new DesktopPointerBatch { Samples = [] }));

        Assert.True(delivered, "pointer batches after a stop must return without opening a socket");
        Assert.False(RemexDesktopClient.Current.IsConnected, "and must not have opened a socket");
    }

    [Fact]
    public async Task StoppingWhenNothingWasRunningStillRecordsTheIntent()
    {
        // StopStreamAsync returns early when no stream is running, and the intent is recorded BEFORE
        // that return. It has to be: the not-running case is exactly where a straggler would
        // otherwise start one.
        await RemexDesktopClient.Current.StopStreamAsync();
        await RemexDesktopClient.Current.StopStreamAsync();

        var delivered = await ReturnedWithoutConnecting(() =>
            RemexDesktopClient.Current.SendInputAsync(
                UnroutableHost, Port, new InputEvent { EventType = InputEventTypes.KeyUp, KeyCode = 0x10 }));

        Assert.True(delivered);
    }
}
