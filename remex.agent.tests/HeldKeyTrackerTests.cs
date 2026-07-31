using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Remex.Agent.Handlers;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Covers the held-key bookkeeping that stops a disconnecting client leaving keys down on the PC
/// (RemEx-e2p4).
/// </summary>
/// <remarks>
/// <para>
/// A key press from the phone is two independent messages, and Ctrl+Shift+C is six. Nothing
/// guarantees the client sends the closing half — it can be force-stopped, lose Wi-Fi, or tear the
/// stream down before its own keyUps drain. The host has already told Windows the key is down, so a
/// modifier ends up physically held on the user's desktop and every later keystroke becomes a chord,
/// with no way to clear it from the phone because the phone is what went away.
/// </para>
/// <para>
/// The client-side ordering work (RemEx-3uhp, RemEx-7rq3, RemEx-krvz) makes those messages arrive in
/// order when they arrive at all. This is the half that covers them never arriving, which no amount
/// of client-side ordering can.
/// </para>
/// </remarks>
public class HeldKeyTrackerTests
{
    private const int VkControl = 0x11;
    private const int VkShift = 0x10;
    private const int VkC = 0x43;

    [Fact]
    public void AKeyPressedAndReleasedNormallyLeavesNothingToCleanUp()
    {
        // The positive control, and the common case by far. If teardown released keys that were
        // already released, every clean disconnect would send spurious keyUps to the user's desktop.
        var tracker = new HeldKeyTracker();

        tracker.Pressed(VkC);
        tracker.Released(VkC);

        Assert.Empty(tracker.TakeAll());
    }

    [Fact]
    public void AChordAbandonedHalfwayLeavesExactlyItsUnreleasedKeys()
    {
        // THE SCENARIO. Ctrl+Shift+C where the client vanishes after sending the key's own keyUp but
        // before the modifiers' — the precise shape that leaves Ctrl and Shift latched on the host.
        var tracker = new HeldKeyTracker();

        tracker.Pressed(VkControl);
        tracker.Pressed(VkShift);
        tracker.Pressed(VkC);
        tracker.Released(VkC);

        var stranded = tracker.TakeAll();

        Assert.Equal(2, stranded.Count);
        Assert.Contains(VkControl, stranded);
        Assert.Contains(VkShift, stranded);
        Assert.DoesNotContain(VkC, stranded);
    }

    [Fact]
    public void TakeAllClearsSoASecondTeardownSendsNothing()
    {
        // Dispose can be reached more than once, and the release is best-effort per key. Reading
        // without clearing would send a second round of keyUps for keys already released - which on
        // a real desktop is a real key event the user did not ask for.
        var tracker = new HeldKeyTracker();
        tracker.Pressed(VkControl);

        Assert.Single(tracker.TakeAll());
        Assert.Empty(tracker.TakeAll());
    }

    [Fact]
    public void RepeatedPressesOfOneKeyReleaseOnce()
    {
        // Auto-repeat sends KeyDown over and over with no intervening KeyUp. Counting presses would
        // make teardown send a burst of keyUps for a single physically-held key.
        var tracker = new HeldKeyTracker();

        for (int i = 0; i < 20; i++)
        {
            tracker.Pressed(VkControl);
        }

        Assert.Equal(new[] { VkControl }, tracker.TakeAll());
    }

    [Fact]
    public void ReleasingAKeyThatWasNeverPressedIsHarmless()
    {
        // Reachable in practice: the client reconnects mid-chord and sends the keyUp half to a
        // handler that never saw the keyDown. It must not throw and must not invent state.
        var tracker = new HeldKeyTracker();

        tracker.Released(VkControl);

        Assert.Empty(tracker.TakeAll());
    }

    [Fact]
    public async Task TeardownRacingTheInputThreadReleasesEachKeyExactlyOnce()
    {
        // Input is dispatched on a dedicated thread while teardown runs on another, so the set is
        // genuinely shared. The risk is not a torn read but a DOUBLE release: two teardowns both
        // seeing a key, or a take that does not clear. Every key must come back from exactly one
        // TakeAll, whichever wins.
        var tracker = new HeldKeyTracker();
        const int keyCount = 200;
        for (int key = 0; key < keyCount; key++)
        {
            tracker.Pressed(key);
        }

        var taken = new ConcurrentBag<int>();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            foreach (var key in tracker.TakeAll())
            {
                taken.Add(key);
            }
        })));

        Assert.Equal(keyCount, taken.Count);
        Assert.Equal(keyCount, taken.Distinct().Count());
        Assert.Equal(0, tracker.Count);
    }
}
