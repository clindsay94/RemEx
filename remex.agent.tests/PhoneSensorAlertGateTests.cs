using Remex.Agent.Services.Alerts;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// The per-session pacing of a phone's sensor alert requests (RemEx-pp4cm.12): a person editing rules
/// is never held up, a phone in a loop is, and the budget comes back as the window slides.
/// </summary>
public class PhoneSensorAlertGateTests
{
    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, (long)by.TotalMilliseconds);
    }

    [Fact]
    public void ABurstBeyondTheBudgetIsRefused()
    {
        var gate = new PhoneSensorAlertGate(new ManualTime());

        var allowed = Enumerable.Range(0, PhoneSensorAlertGate.MaxRequestsPerWindow + 30).Count(_ => gate.TryEnter());

        Assert.Equal(PhoneSensorAlertGate.MaxRequestsPerWindow, allowed);
    }

    [Fact]
    public void TheBudgetComesBackOnceTheWindowHasPassed()
    {
        var time = new ManualTime();
        var gate = new PhoneSensorAlertGate(time);
        for (var i = 0; i < PhoneSensorAlertGate.MaxRequestsPerWindow; i++) Assert.True(gate.TryEnter());
        Assert.False(gate.TryEnter());

        time.Advance(PhoneSensorAlertGate.Window - TimeSpan.FromMilliseconds(1));
        Assert.False(gate.TryEnter());

        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(gate.TryEnter());
    }

    [Fact]
    public void ARefusedRequestDoesNotExtendTheWait()
    {
        var time = new ManualTime();
        var gate = new PhoneSensorAlertGate(time);
        for (var i = 0; i < PhoneSensorAlertGate.MaxRequestsPerWindow; i++) gate.TryEnter();

        for (var i = 0; i < 50; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(100));
            gate.TryEnter();
        }

        // 5 s of refused hammering later, the first window is still what decides when the phone is let in.
        time.Advance(PhoneSensorAlertGate.Window - TimeSpan.FromSeconds(5));
        Assert.True(gate.TryEnter());
    }

    [Fact]
    public void SessionsDoNotShareABudget()
    {
        var time = new ManualTime();
        var noisy = new PhoneSensorAlertGate(time);
        var quiet = new PhoneSensorAlertGate(time);
        for (var i = 0; i < PhoneSensorAlertGate.MaxRequestsPerWindow + 5; i++) noisy.TryEnter();

        Assert.True(quiet.TryEnter());
    }
}
